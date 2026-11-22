package main

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/binary"
	"errors"
	"io"
	"mime"
	"net"
	"net/http"
	"net/netip"
	"strconv"
	"strings"
	"time"

	"github.com/miekg/dns"
)

type BootstrapProxy struct {
	Port     int    `json:"port"`
	Username string `json:"username"`
	Password string `json:"password"`
}

func (p *BootstrapProxy) valid(guestPort, listenPort int) bool {
	if p == nil {
		return true
	}
	if p.Port < 1024 || p.Port > 65535 || p.Port == guestPort || p.Port == listenPort || len(p.Username) < 16 || len(p.Username) > 128 || len(p.Password) < 32 || len(p.Password) > 128 {
		return false
	}
	for _, value := range []string{p.Username, p.Password} {
		for _, c := range value {
			if !((c >= 'a' && c <= 'f') || (c >= '0' && c <= '9')) {
				return false
			}
		}
	}
	return true
}

// The only OS socket used for bootstrap destinations is an authenticated loopback
// SOCKS connection. The proxy core retains the selected node and pinned hosts.
func dialBootstrap(ctx context.Context, proxy *BootstrapProxy, destination netip.AddrPort) (net.Conn, error) {
	if proxy == nil || !publicAddress(destination.Addr()) {
		return nil, errClosed
	}
	connection, err := (&net.Dialer{Timeout: 10 * time.Second}).DialContext(ctx, "tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(proxy.Port)))
	if err != nil {
		return nil, errors.New("bootstrap_unavailable")
	}
	success := false
	defer func() {
		if !success {
			connection.Close()
		}
	}()
	deadline := time.Now().Add(10 * time.Second)
	if d, ok := ctx.Deadline(); ok && d.Before(deadline) {
		deadline = d
	}
	connection.SetDeadline(deadline)
	stop := context.AfterFunc(ctx, func() { connection.Close() })
	defer stop()
	if _, err = connection.Write([]byte{5, 1, 2}); err != nil {
		return nil, err
	}
	var greeting [2]byte
	if _, err = io.ReadFull(connection, greeting[:]); err != nil {
		return nil, err
	}
	if greeting != [2]byte{5, 2} {
		return nil, errors.New("bootstrap_auth_required")
	}
	auth := append([]byte{1, byte(len(proxy.Username))}, []byte(proxy.Username)...)
	auth = append(auth, byte(len(proxy.Password)))
	auth = append(auth, []byte(proxy.Password)...)
	if _, err = connection.Write(auth); err != nil {
		return nil, err
	}
	if _, err = io.ReadFull(connection, greeting[:]); err != nil {
		return nil, err
	}
	if greeting != [2]byte{1, 0} {
		return nil, errors.New("bootstrap_auth_failed")
	}
	address := destination.Addr().As4()
	request := append([]byte{5, 1, 0, 1}, address[:]...)
	request = binary.BigEndian.AppendUint16(request, destination.Port())
	if _, err = connection.Write(request); err != nil {
		return nil, err
	}
	var reply [4]byte
	if _, err = io.ReadFull(connection, reply[:]); err != nil {
		return nil, err
	}
	if reply[0] != 5 || reply[1] != 0 || reply[2] != 0 {
		return nil, errors.New("bootstrap_connect_failed")
	}
	count := 0
	switch reply[3] {
	case 1:
		count = 6
	case 4:
		count = 18
	case 3:
		var length [1]byte
		if _, err = io.ReadFull(connection, length[:]); err != nil {
			return nil, err
		}
		count = int(length[0]) + 2
	default:
		return nil, errors.New("bootstrap_connect_failed")
	}
	if _, err = io.CopyN(io.Discard, connection, int64(count)); err != nil {
		return nil, err
	}
	connection.SetDeadline(time.Time{})
	success = true
	return connection, nil
}

func (g *Gate) bootstrapAvailable() bool {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.bootstrapActive && time.Now().Before(g.until)
}
func (g *Gate) FinishBootstrap() {
	g.mu.Lock()
	g.bootstrapActive = false
	transports := g.dropBootstrapTransportsLocked()
	var connections []*guardedConn
	for c := range g.flows {
		if c.bootstrap {
			connections = append(connections, c)
		}
	}
	g.mu.Unlock()
	for _, tr := range transports {
		tr.CloseIdleConnections()
	}
	for _, c := range connections {
		c.Close()
	}
}
func (g *Gate) pinnedDestination(dst netip.AddrPort) bool {
	if dst.Port() != g.policy.EndpointPort {
		return false
	}
	for _, ip := range g.policy.EndpointAddresses {
		if dst.Addr().String() == ip {
			return true
		}
	}
	return false
}

// Resolver names are used for HTTPS Host/SNI, while their public dial addresses
// are fixed. Both independent services remain inside the selected proxy.
type bootstrapResolver struct{ name, address string }

var bootstrapResolvers = []bootstrapResolver{
	{"cloudflare-dns.com", "1.1.1.1:443"},
	{"dns.google", "8.8.8.8:443"},
}

func bootstrapFailure(err error) string {
	if err == nil {
		return "OK"
	}
	switch err.Error() {
	case "bootstrap_unavailable", "bootstrap_auth_required", "bootstrap_auth_failed", "bootstrap_connect_failed", "egress_closed", "bootstrap_doh_http_failed", "bootstrap_doh_response_invalid", "bootstrap_dns_empty", "bootstrap_dns_tls_failed", "bootstrap_dns_transport_failed", "bootstrap_timeout":
		return err.Error()
	}
	var certificate *tls.CertificateVerificationError
	var authority x509.UnknownAuthorityError
	var hostname x509.HostnameError
	if errors.As(err, &certificate) || errors.As(err, &authority) || errors.As(err, &hostname) {
		return "bootstrap_dns_tls_failed"
	}
	var network net.Error
	if errors.Is(err, context.DeadlineExceeded) || errors.As(err, &network) && network.Timeout() {
		return "bootstrap_timeout"
	}
	return "bootstrap_dns_transport_failed"
}

// No host DNS or environment proxy is consulted by this transport.
func (g *Gate) bootstrapHTTP(endpoint bootstrapResolver) *http.Client {
	transport := &http.Transport{Proxy: nil, ForceAttemptHTTP2: true, DisableKeepAlives: true,
		DialContext: func(c context.Context, network, address string) (net.Conn, error) {
			return g.dialGuarded(c, endpoint.address, true)
		}}
	return &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return errors.New("bootstrap_doh_http_failed") }}
}
func (g *Gate) bootstrapPooledHTTP(endpoint bootstrapResolver) *http.Client {
	g.mu.Lock()
	if !g.bootstrapActive || !time.Now().Before(g.until) {
		g.mu.Unlock()
		return g.bootstrapHTTP(endpoint)
	}
	key := strconv.FormatUint(g.epoch, 10) + "|" + endpoint.name + "|" + endpoint.address
	transport, ok := g.dohTransports[key]
	if !ok {
		transport = &http.Transport{
			Proxy:                 nil,
			ForceAttemptHTTP2:     true,
			MaxIdleConns:          4,
			MaxIdleConnsPerHost:   2,
			IdleConnTimeout:       30 * time.Second,
			TLSHandshakeTimeout:   6 * time.Second,
			ResponseHeaderTimeout: 6 * time.Second,
			DialContext: func(c context.Context, network, address string) (net.Conn, error) {
				return g.dialGuarded(c, endpoint.address, true)
			},
		}
		g.dohTransports[key] = transport
	}
	g.mu.Unlock()
	return &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return errors.New("bootstrap_doh_http_failed") }}
}
func exchangeBootstrapDNS(ctx context.Context, query *dns.Msg, endpoint bootstrapResolver, client *http.Client) (*dns.Msg, error) {
	wireQuery := query.Copy()
	wireQuery.Id = 0 // RFC 8484: HTTP provides transaction matching.
	wire, err := wireQuery.Pack()
	if err != nil || len(wire) > 4096 {
		return nil, errors.New("bootstrap_doh_response_invalid")
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, "https://"+endpoint.name+"/dns-query", bytes.NewReader(wire))
	if err != nil {
		return nil, errors.New("bootstrap_doh_http_failed")
	}
	request.Header.Set("Content-Type", "application/dns-message")
	request.Header.Set("Accept", "application/dns-message")
	response, err := client.Do(request)
	if err != nil {
		return nil, errors.New(bootstrapFailure(err))
	}
	defer response.Body.Close()
	media, _, _ := mime.ParseMediaType(response.Header.Get("Content-Type"))
	if response.StatusCode != http.StatusOK {
		return nil, errors.New("bootstrap_doh_http_failed")
	}
	if media != "application/dns-message" {
		return nil, errors.New("bootstrap_doh_response_invalid")
	}
	data, err := io.ReadAll(io.LimitReader(response.Body, 65537))
	if err != nil {
		return nil, errors.New(bootstrapFailure(err))
	}
	var answer dns.Msg
	if len(data) > 65536 || answer.Unpack(data) != nil || !answer.Response || answer.Truncated || answer.Opcode != dns.OpcodeQuery || answer.Id != 0 || len(answer.Question) != 1 || answer.Question[0].Qtype != query.Question[0].Qtype || answer.Question[0].Qclass != query.Question[0].Qclass || !stringsEqualDNS(answer.Question[0].Name, query.Question[0].Name) {
		return nil, errors.New("bootstrap_doh_response_invalid")
	}
	if answer.Rcode != dns.RcodeSuccess && answer.Rcode != dns.RcodeNameError {
		return nil, errors.New("bootstrap_doh_response_invalid")
	}
	answer.Id = query.Id
	answer.Question = append([]dns.Question(nil), query.Question...)
	return &answer, nil
}
func resolveBootstrapDNS(ctx context.Context, query *dns.Msg, endpoints []bootstrapResolver, exchange func(context.Context, *dns.Msg, bootstrapResolver) (*dns.Msg, error)) (*dns.Msg, error) {
	requestContext, cancel := context.WithTimeout(ctx, 6*time.Second)
	defer cancel()
	type result struct {
		answer *dns.Msg
		err    error
	}
	responses := make(chan result, len(endpoints))
	for _, endpoint := range endpoints {
		go func(endpoint bootstrapResolver) {
			a, e := exchange(requestContext, query.Copy(), endpoint)
			responses <- result{a, e}
		}(endpoint)
	}
	codes := make(map[string]bool)
	for range endpoints {
		select {
		case <-requestContext.Done():
			return nil, errors.New("bootstrap_timeout")
		case response := <-responses:
			if response.err == nil && response.answer != nil {
				return response.answer, nil
			}
			codes[bootstrapFailure(response.err)] = true
		}
	}
	// A stable whitelist, never raw URLs, node data, certificate names or bodies.
	for _, code := range []string{"bootstrap_auth_required", "bootstrap_auth_failed", "bootstrap_unavailable", "bootstrap_connect_failed", "egress_closed", "bootstrap_dns_tls_failed", "bootstrap_doh_response_invalid", "bootstrap_doh_http_failed", "bootstrap_timeout", "bootstrap_dns_transport_failed"} {
		if codes[code] {
			return nil, errors.New(code)
		}
	}
	return nil, errors.New("bootstrap_dns_failed")
}
func (g *Gate) resolveBootstrapDNS(ctx context.Context, query *dns.Msg) (*dns.Msg, error) {
	if !g.bootstrapAvailable() {
		return nil, errClosed
	}
	answer, err := resolveBootstrapDNS(ctx, query, bootstrapResolvers, func(c context.Context, q *dns.Msg, e bootstrapResolver) (*dns.Msg, error) {
		return exchangeBootstrapDNS(c, q, e, g.bootstrapPooledHTTP(e))
	})
	if !g.bootstrapAvailable() {
		return nil, errClosed
	}
	return answer, err
}

// Only A questions are answered; the packet transport deliberately has no IPv6.
func (g *Gate) bootstrapDNS(ctx context.Context, query *dns.Msg) *dns.Msg {
	failure := new(dns.Msg)
	failure.SetReply(query)
	failure.Rcode = dns.RcodeServerFailure
	if !g.bootstrapAvailable() || query.Response || query.Opcode != dns.OpcodeQuery || len(query.Question) != 1 || query.Question[0].Qclass != dns.ClassINET {
		return failure
	}
	if query.Question[0].Qtype == dns.TypeAAAA {
		failure.Rcode = dns.RcodeSuccess
		return failure
	}
	if query.Question[0].Qtype != dns.TypeA {
		return failure
	}
	if answer := g.cachedBootstrapProbeDNS(query); answer != nil {
		return answer
	}
	if answer := g.cachedGeneralBootstrapDNS(query); answer != nil {
		return answer
	}
	answer, err := g.resolveBootstrapDNS(ctx, query)
	if err != nil {
		return failure
	}
	g.storeGeneralBootstrapDNS(query, answer)
	return answer
}

func dnsCacheKey(query *dns.Msg) string {
	if len(query.Question) != 1 || query.Question[0].Qtype != dns.TypeA || query.Question[0].Qclass != dns.ClassINET {
		return ""
	}
	return strings.ToLower(strings.TrimSuffix(query.Question[0].Name, "."))
}

func (g *Gate) cachedGeneralBootstrapDNS(query *dns.Msg) *dns.Msg {
	key := dnsCacheKey(query)
	if key == "" {
		return nil
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	if !g.bootstrapActive || !time.Now().Before(g.until) {
		return nil
	}
	entry, ok := g.dnsCache[key]
	if !ok {
		return nil
	}
	age := time.Since(entry.created)
	if entry.epoch != g.epoch || age >= entry.ttl || entry.answer == nil {
		delete(g.dnsCache, key)
		return nil
	}
	answer := entry.answer.Copy()
	answer.Id = query.Id
	answer.Question = append([]dns.Question(nil), query.Question...)
	for _, section := range [][]dns.RR{answer.Answer, answer.Ns, answer.Extra} {
		for _, rr := range section {
			header := rr.Header()
			if header.Rrtype == dns.TypeOPT {
				continue
			}
			remaining := min(time.Duration(header.Ttl)*time.Second, entry.ttl) - age
			if remaining < time.Second {
				delete(g.dnsCache, key)
				return nil
			}
			header.Ttl = uint32(remaining / time.Second)
		}
	}
	return answer
}

func (g *Gate) storeGeneralBootstrapDNS(query *dns.Msg, answer *dns.Msg) {
	key := dnsCacheKey(query)
	if key == "" || answer == nil || (answer.Rcode != dns.RcodeSuccess && answer.Rcode != dns.RcodeNameError) {
		return
	}
	maxTTL := 60 * time.Second
	seenRecord := false
	for _, section := range [][]dns.RR{answer.Answer, answer.Ns, answer.Extra} {
		for _, rr := range section {
			header := rr.Header()
			if header.Rrtype == dns.TypeOPT {
				continue
			}
			seenRecord = true
			if rrTTL := time.Duration(header.Ttl) * time.Second; rrTTL < maxTTL {
				maxTTL = rrTTL
			}
		}
	}
	if !seenRecord {
		maxTTL = 10 * time.Second
	}
	if maxTTL < 2*time.Second {
		return
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	if !g.bootstrapActive || !time.Now().Before(g.until) {
		return
	}
	if len(g.dnsCache) >= 256 {
		now := time.Now()
		var oldestKey string
		var oldestTime time.Time
		for k, v := range g.dnsCache {
			if v.epoch != g.epoch || now.Sub(v.created) >= v.ttl {
				delete(g.dnsCache, k)
				continue
			}
			if oldestKey == "" || v.created.Before(oldestTime) {
				oldestKey = k
				oldestTime = v.created
			}
		}
		if len(g.dnsCache) >= 256 && oldestKey != "" {
			delete(g.dnsCache, oldestKey)
		}
	}
	g.dnsCache[key] = cachedBootstrapDNS{
		answer:  answer.Copy(),
		created: time.Now(),
		ttl:     maxTTL,
		epoch:   g.epoch,
	}
}

// Briefly reuse the verified answer and place the address that actually
// returned the Windows probe first. Never extend DNS TTL or reuse after revoke.
func (g *Gate) cachedBootstrapProbeDNS(query *dns.Msg) *dns.Msg {
	if len(query.Question) != 1 || !stringsEqualDNS(query.Question[0].Name, "www.msftconnecttest.com") || query.Question[0].Qtype != dns.TypeA {
		return nil
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	age := time.Since(g.probeCreated)
	if !g.bootstrapActive || !time.Now().Before(g.until) || g.probeEpoch != g.epoch || age >= 30*time.Second || g.probeAnswer == nil || g.probeWorkingAddress == "" {
		return nil
	}
	answer := g.probeAnswer.Copy()
	answer.Id = query.Id
	answer.Question = append([]dns.Question(nil), query.Question...)
	for _, section := range [][]dns.RR{answer.Answer, answer.Ns, answer.Extra} {
		for _, rr := range section {
			header := rr.Header()
			if header.Rrtype == dns.TypeOPT {
				continue
			}
			remaining := min(time.Duration(header.Ttl)*time.Second, 30*time.Second) - age
			if remaining <= 0 {
				return nil
			}
			header.Ttl = uint32(remaining / time.Second)
		}
	}
	host, _, _ := net.SplitHostPort(g.probeWorkingAddress)
	first, working := -1, -1
	for i, rr := range answer.Answer {
		if a, ok := rr.(*dns.A); ok {
			if first < 0 {
				first = i
			}
			if a.A.String() == host {
				working = i
			}
		}
	}
	if first >= 0 && working >= 0 {
		answer.Answer[first], answer.Answer[working] = answer.Answer[working], answer.Answer[first]
	}
	return answer
}
func (g *Gate) ProbeBootstrapDNS(ctx context.Context) error {
	return g.probeBootstrapDNSUsing(ctx, g.resolveBootstrapDNS)
}
func (g *Gate) probeBootstrapDNSUsing(ctx context.Context, resolve func(context.Context, *dns.Msg) (*dns.Msg, error)) error {
	g.mu.Lock()
	epoch := g.epoch
	g.probeAddresses = nil
	g.probeAnswer = nil
	g.probeWorkingAddress = ""
	g.mu.Unlock()
	if !g.bootstrapAvailable() {
		return errClosed
	}
	query := new(dns.Msg)
	query.SetQuestion("www.msftconnecttest.com.", dns.TypeA)
	answer, err := resolve(ctx, query)
	if err != nil {
		return err
	}
	var addresses []string
	seen := map[string]bool{}
	for _, rr := range answer.Answer {
		if a, ok := rr.(*dns.A); ok {
			ip, valid := netip.AddrFromSlice(a.A.To4())
			if valid && publicAddress(ip) && !seen[ip.String()] {
				seen[ip.String()] = true
				addresses = append(addresses, net.JoinHostPort(ip.String(), "80"))
				if len(addresses) == 8 {
					break
				}
			}
		}
	}
	if len(addresses) == 0 {
		return errors.New("bootstrap_dns_empty")
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	if epoch != g.epoch || !g.bootstrapActive || !time.Now().Before(g.until) {
		return errClosed
	}
	g.probeAddresses = addresses
	g.probeAnswer = answer.Copy()
	g.probeEpoch = epoch
	g.probeCreated = time.Now()
	return nil
}
func (g *Gate) bootstrapWebHTTP(destination string) *http.Client {
	transport := &http.Transport{Proxy: nil, DisableKeepAlives: true, DialContext: func(c context.Context, network, address string) (net.Conn, error) {
		return g.dialGuarded(c, destination, true)
	}}
	return &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
}
func exchangeBootstrapWeb(ctx context.Context, client *http.Client) error {
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://www.msftconnecttest.com/connecttest.txt", nil)
	if err != nil {
		return errors.New("bootstrap_http_failed")
	}
	response, err := client.Do(request)
	if err != nil {
		if bootstrapFailure(err) == "bootstrap_timeout" {
			return errors.New("bootstrap_http_timeout")
		}
		return errors.New("bootstrap_http_failed")
	}
	defer response.Body.Close()
	if response.StatusCode >= 300 && response.StatusCode <= 399 {
		return errors.New("bootstrap_http_redirect")
	}
	body, err := io.ReadAll(io.LimitReader(response.Body, 4097))
	if err != nil {
		if bootstrapFailure(err) == "bootstrap_timeout" {
			return errors.New("bootstrap_http_timeout")
		}
		return errors.New("bootstrap_http_failed")
	}
	if len(body) > 4096 || response.StatusCode != http.StatusOK || !bytes.Equal(bytes.TrimSpace(body), []byte("Microsoft Connect Test")) {
		return errors.New("bootstrap_http_mismatch")
	}
	return nil
}
func (g *Gate) ProbeBootstrapHTTP(ctx context.Context) error {
	return g.probeBootstrapHTTPUsing(ctx, func(c context.Context, destination string) error {
		client := g.bootstrapWebHTTP(destination)
		defer client.CloseIdleConnections()
		return exchangeBootstrapWeb(c, client)
	})
}
func (g *Gate) probeBootstrapHTTPUsing(ctx context.Context, exchange func(context.Context, string) error) error {
	g.mu.Lock()
	addresses := append([]string(nil), g.probeAddresses...)
	active := g.bootstrapActive && time.Now().Before(g.until)
	valid := g.probeEpoch == g.epoch && time.Since(g.probeCreated) < 30*time.Second
	g.mu.Unlock()
	if !active {
		return errClosed
	}
	if !valid || len(addresses) == 0 {
		return errors.New("bootstrap_dns_required")
	}
	requestContext, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()
	type probeReply struct {
		err     error
		address string
	}
	replies := make(chan probeReply, len(addresses))
	slots := make(chan struct{}, 4)
	for _, destination := range addresses {
		go func(destination string) {
			select {
			case slots <- struct{}{}:
			case <-requestContext.Done():
				replies <- probeReply{errors.New("bootstrap_http_timeout"), destination}
				return
			}
			defer func() { <-slots }()
			attempt, done := context.WithTimeout(requestContext, 4*time.Second)
			defer done()
			replies <- probeReply{exchange(attempt, destination), destination}
		}(destination)
	}
	codes := map[string]bool{}
	for range addresses {
		select {
		case <-requestContext.Done():
			return errors.New("bootstrap_http_timeout")
		case reply := <-replies:
			if reply.err == nil {
				g.mu.Lock()
				valid := g.bootstrapActive && g.probeEpoch == g.epoch && time.Now().Before(g.until)
				if valid {
					g.probeWorkingAddress = reply.address
				}
				g.mu.Unlock()
				if !valid {
					return errClosed
				}
				return nil
			}
			codes[reply.err.Error()] = true
		}
	}
	for _, code := range []string{"bootstrap_http_redirect", "bootstrap_http_mismatch", "bootstrap_http_timeout", "bootstrap_http_failed"} {
		if codes[code] {
			return errors.New(code)
		}
	}
	return errors.New("bootstrap_http_failed")
}

// Legacy single-command callers remain bounded by their own context.
func (g *Gate) ProbeBootstrap(ctx context.Context) error {
	if err := g.ProbeBootstrapDNS(ctx); err != nil {
		return err
	}
	return g.ProbeBootstrapHTTP(ctx)
}

func controlErrorCode(err error) string {
	if err == nil {
		return "OK"
	}
	switch err.Error() {
	case "bootstrap_http_redirect", "bootstrap_http_timeout", "bootstrap_dns_required":
		return strings.ToUpper(err.Error())
	case "bootstrap_dns_tls_failed", "bootstrap_dns_transport_failed", "bootstrap_doh_http_failed", "bootstrap_doh_response_invalid", "bootstrap_dns_empty", "bootstrap_timeout", "bootstrap_unavailable", "bootstrap_auth_required", "bootstrap_auth_failed", "bootstrap_connect_failed":
		return strings.ToUpper(err.Error())
	case "bootstrap_dns_failed":
		return "BOOTSTRAP_DNS_FAILED"
	case "bootstrap_http_failed":
		return "BOOTSTRAP_HTTP_FAILED"
	case "bootstrap_http_mismatch":
		return "BOOTSTRAP_HTTP_MISMATCH"
	case "egress_closed":
		return "EGRESS_CLOSED"
	default:
		return "BOOTSTRAP_UNAVAILABLE"
	}
}
