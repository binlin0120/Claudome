// Copyright (c) 2026 Claude Environment Deploy contributors. MIT.
package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"github.com/miekg/dns"
	"net"
	"net/http"
	"net/netip"
	"strings"
	"sync"
	"time"
)

var errClosed = errors.New("egress_closed")

const maxActiveFlows = 256

type cachedBootstrapDNS struct {
	answer  *dns.Msg
	created time.Time
	ttl     time.Duration
	epoch   uint64
}

// The gateway does not offer general Internet access. Only pinned node sockets
// can be dialled. Clash and its existing protection remain inside the guest.
type Policy struct {
	VMID              string          `json:"vmId"`
	RunID             string          `json:"runId"`
	GuestMAC          string          `json:"guestMac"`
	GuestPort         int             `json:"guestPort"`
	ListenPort        int             `json:"listenPort"`
	EndpointAddresses []string        `json:"endpointAddresses"`
	EndpointPort      uint16          `json:"endpointPort"`
	NodeHost          string          `json:"nodeHost"`
	Bootstrap         *BootstrapProxy `json:"bootstrap,omitempty"`
}

var reserved = []netip.Prefix{
	netip.MustParsePrefix("0.0.0.0/8"), netip.MustParsePrefix("10.0.0.0/8"),
	netip.MustParsePrefix("100.64.0.0/10"), netip.MustParsePrefix("127.0.0.0/8"),
	netip.MustParsePrefix("169.254.0.0/16"), netip.MustParsePrefix("172.16.0.0/12"),
	netip.MustParsePrefix("192.0.0.0/24"), netip.MustParsePrefix("192.0.2.0/24"),
	netip.MustParsePrefix("192.168.0.0/16"), netip.MustParsePrefix("198.18.0.0/15"),
	netip.MustParsePrefix("198.51.100.0/24"), netip.MustParsePrefix("203.0.113.0/24"),
	netip.MustParsePrefix("224.0.0.0/4"), netip.MustParsePrefix("240.0.0.0/4"),
}

func publicAddress(a netip.Addr) bool {
	// All IPv6, including IPv4-mapped forms, is deliberately unsupported.
	if !a.Is4() || !a.IsGlobalUnicast() {
		return false
	}
	for _, prefix := range reserved {
		if prefix.Contains(a) {
			return false
		}
	}
	return true
}
func (p Policy) Validate() error {
	if !p.Bootstrap.valid(p.GuestPort, p.ListenPort) {
		return errors.New("invalid_bootstrap_binding")
	}
	if len(p.VMID) != 36 || len(p.RunID) != 32 || p.GuestPort < 1024 || p.GuestPort > 65535 || p.ListenPort < 1024 || p.ListenPort > 65535 || p.ListenPort == p.GuestPort || p.EndpointPort == 0 || len(p.EndpointAddresses) == 0 || len(p.EndpointAddresses) > 32 {
		return errors.New("invalid_policy")
	}
	if _, err := hex.DecodeString(p.RunID); err != nil {
		return errors.New("invalid_policy")
	}
	if _, err := hex.DecodeString(strings.ReplaceAll(p.VMID, "-", "")); err != nil {
		return errors.New("invalid_policy")
	}
	mac, err := net.ParseMAC(p.GuestMAC)
	if err != nil || len(mac) != 6 || mac[0]&1 != 0 {
		return errors.New("invalid_guest_mac")
	}
	if len(p.NodeHost) > 253 || strings.ContainsAny(p.NodeHost, "\r\n\x00/ :") {
		return errors.New("invalid_node_host")
	}
	for _, raw := range p.EndpointAddresses {
		a, err := netip.ParseAddr(raw)
		if err != nil || !publicAddress(a) {
			return errors.New("non_public_endpoint")
		}
	}
	return nil
}
func (p Policy) Fingerprint() string {
	b, _ := json.Marshal(p)
	h := sha256.Sum256(b)
	return hex.EncodeToString(h[:])
}

type localNetworks func() ([]netip.Prefix, error)

func hostNetworks() ([]netip.Prefix, error) {
	addrs, err := net.InterfaceAddrs()
	if err != nil || len(addrs) == 0 {
		return nil, errors.New("host_network_unknown")
	}
	var prefixes []netip.Prefix
	for _, a := range addrs {
		prefix, err := netip.ParsePrefix(a.String())
		if err != nil || prefix.Bits() == 0 {
			return nil, errors.New("host_network_unknown")
		}
		prefixes = append(prefixes, prefix.Masked())
	}
	return prefixes, nil
}

type Gate struct {
	mu                  sync.Mutex
	policy              Policy
	networks            localNetworks
	dial                func(context.Context, string, string) (net.Conn, error)
	epoch               uint64
	lease               uint64
	until               time.Time
	ctx                 context.Context
	cancel              context.CancelFunc
	flows               map[*guardedConn]struct{}
	denied              uint64
	timer               *time.Timer
	bootstrapActive     bool
	probeAnswer         *dns.Msg
	probeWorkingAddress string
	probeAddresses      []string
	probeEpoch          uint64
	probeCreated        time.Time
	dnsCache            map[string]cachedBootstrapDNS
	dohTransports       map[string]*http.Transport
}

func newGate(p Policy, networks localNetworks, dial func(context.Context, string, string) (net.Conn, error)) (*Gate, error) {
	if err := p.Validate(); err != nil {
		return nil, err
	}
	ctx, cancel := context.WithCancel(context.Background())
	return &Gate{
		policy:          p,
		networks:        networks,
		dial:            dial,
		ctx:             ctx,
		cancel:          cancel,
		flows:           make(map[*guardedConn]struct{}),
		bootstrapActive: p.Bootstrap != nil,
		dnsCache:        make(map[string]cachedBootstrapDNS),
		dohTransports:   make(map[string]*http.Transport),
	}, nil
}
func (g *Gate) permitsLocked(network string, dst netip.AddrPort) bool {
	if network != "tcp" || !publicAddress(dst.Addr()) {
		return false
	}
	found := g.pinnedDestination(dst)
	if !found && !(g.policy.Bootstrap != nil && g.bootstrapActive && (dst.Port() == 80 || dst.Port() == 443)) {
		return false
	}
	prefixes, err := g.networks()
	if err != nil || len(prefixes) == 0 {
		return false
	}
	for _, p := range prefixes {
		if !p.IsValid() || p.Bits() == 0 || p.Contains(dst.Addr()) {
			return false
		}
	}
	return true
}
func (g *Gate) Allows(network string, dst netip.AddrPort) bool {
	g.mu.Lock()
	defer g.mu.Unlock()
	allowed := time.Now().Before(g.until) && g.permitsLocked(network, dst)
	if !allowed {
		g.denied++
	}
	return allowed
}
func (g *Gate) Renew(fingerprint string, seconds int) error {
	g.mu.Lock()
	defer g.mu.Unlock()
	if fingerprint != g.policy.Fingerprint() || seconds < 1 || seconds > 30 {
		return errClosed
	}
	// Topology failures close existing streams too; do not renew on partial evidence.
	for _, ip := range g.policy.EndpointAddresses {
		if !g.permitsLocked("tcp", netip.AddrPortFrom(netip.MustParseAddr(ip), g.policy.EndpointPort)) {
			return errClosed
		}
	}
	g.until = time.Now().Add(time.Duration(seconds) * time.Second)
	if g.timer != nil {
		g.timer.Stop()
	}
	epoch := g.epoch
	g.lease++
	lease := g.lease
	g.timer = time.AfterFunc(time.Duration(seconds)*time.Second, func() {
		g.mu.Lock()
		var flows []*guardedConn
		var transports []*http.Transport
		if g.epoch == epoch && g.lease == lease && !time.Now().Before(g.until) {
			flows, transports = g.revokeLocked()
		}
		g.mu.Unlock()
		for _, tr := range transports {
			tr.CloseIdleConnections()
		}
		for _, c := range flows {
			c.Close()
		}
	})
	return nil
}
func (g *Gate) Close() {
	g.mu.Lock()
	flows, transports := g.revokeLocked()
	g.mu.Unlock()
	for _, tr := range transports {
		tr.CloseIdleConnections()
	}
	for _, c := range flows {
		c.Close()
	}
}
func (g *Gate) dropBootstrapTransportsLocked() []*http.Transport {
	clear(g.dnsCache)
	transports := make([]*http.Transport, 0, len(g.dohTransports))
	for _, tr := range g.dohTransports {
		transports = append(transports, tr)
	}
	clear(g.dohTransports)
	return transports
}
func (g *Gate) revokeLocked() ([]*guardedConn, []*http.Transport) {
	g.until = time.Time{}
	g.epoch++
	g.cancel()
	g.ctx, g.cancel = context.WithCancel(context.Background())
	if g.timer != nil {
		g.timer.Stop()
	}
	transports := g.dropBootstrapTransportsLocked()
	flows := make([]*guardedConn, 0, len(g.flows))
	for c := range g.flows {
		flows = append(flows, c)
	}
	g.flows = make(map[*guardedConn]struct{})
	return flows, transports
}
func (g *Gate) Dial(ctx context.Context, destination string) (net.Conn, error) {
	return g.dialGuarded(ctx, destination, false)
}
func (g *Gate) dialGuarded(ctx context.Context, destination string, forceBootstrap bool) (net.Conn, error) {
	dst, err := netip.ParseAddrPort(destination)
	if err != nil {
		return nil, errClosed
	}
	g.mu.Lock()
	if !time.Now().Before(g.until) || !g.permitsLocked("tcp", dst) || len(g.flows) >= maxActiveFlows || (forceBootstrap && !g.bootstrapActive) {
		g.denied++
		g.mu.Unlock()
		return nil, errClosed
	}
	epoch, revoked := g.epoch, g.ctx
	useBootstrap := forceBootstrap || !g.pinnedDestination(dst)
	g.mu.Unlock()
	dialCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	stop := context.AfterFunc(revoked, cancel)
	defer func() { stop(); cancel() }()
	var connection net.Conn
	if useBootstrap {
		connection, err = dialBootstrap(dialCtx, g.policy.Bootstrap, dst)
	} else {
		connection, err = g.dial(dialCtx, "tcp", dst.String())
	}
	if err != nil {
		if useBootstrap {
			return nil, errors.New(bootstrapFailure(err))
		}
		return nil, errors.New("node_connection_failed")
	}
	g.mu.Lock()
	if epoch != g.epoch || !time.Now().Before(g.until) || !g.permitsLocked("tcp", dst) || len(g.flows) >= maxActiveFlows || (useBootstrap && !g.bootstrapActive) {
		g.mu.Unlock()
		connection.Close()
		return nil, errClosed
	}
	wrapped := &guardedConn{Conn: connection, gate: g, epoch: epoch, destination: dst, bootstrap: useBootstrap}
	g.flows[wrapped] = struct{}{}
	g.mu.Unlock()
	return wrapped, nil
}

type guardedConn struct {
	net.Conn
	gate        *Gate
	epoch       uint64
	destination netip.AddrPort
	once        sync.Once
	bootstrap   bool
}

func (c *guardedConn) valid() bool {
	c.gate.mu.Lock()
	defer c.gate.mu.Unlock()
	return c.epoch == c.gate.epoch && time.Now().Before(c.gate.until) && c.gate.permitsLocked("tcp", c.destination) && (!c.bootstrap || c.gate.bootstrapActive)
}
func (c *guardedConn) Read(b []byte) (int, error) {
	if !c.valid() {
		c.Close()
		return 0, errClosed
	}
	return c.Conn.Read(b)
}
func (c *guardedConn) Write(b []byte) (int, error) {
	if !c.valid() {
		c.Close()
		return 0, errClosed
	}
	return c.Conn.Write(b)
}
func (c *guardedConn) CloseWrite() error {
	if c, ok := c.Conn.(interface{ CloseWrite() error }); ok {
		return c.CloseWrite()
	}
	return errors.New("half_close_unsupported")
}
func (c *guardedConn) Close() error {
	var err error
	c.once.Do(func() {
		c.SetDeadline(time.Now())
		err = c.Conn.Close()
		c.gate.mu.Lock()
		delete(c.gate.flows, c)
		c.gate.mu.Unlock()
	})
	return err
}
