package main

import (
	"context"
	"encoding/binary"
	"io"
	"net"
	"net/netip"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/miekg/dns"
)

func bootstrapFixture(t *testing.T, rejectAuth bool) (*Gate, *atomic.Int32) {
	t.Helper()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { listener.Close() })
	proxy := &BootstrapProxy{Port: listener.Addr().(*net.TCPAddr).Port, Username: strings.Repeat("a", 32), Password: strings.Repeat("b", 64)}
	direct := new(atomic.Int32)
	p := fixturePolicy()
	p.Bootstrap = proxy
	g, err := newGate(p, safeNetworks, func(context.Context, string, string) (net.Conn, error) { direct.Add(1); return nil, errClosed })
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(g.Close)
	if err = g.Renew(p.Fingerprint(), 30); err != nil {
		t.Fatal(err)
	}
	go func() {
		connection, err := listener.Accept()
		if err != nil {
			return
		}
		defer connection.Close()
		connection.SetDeadline(time.Now().Add(5 * time.Second))
		var header [3]byte
		if _, err = io.ReadFull(connection, header[:]); err != nil || header != [3]byte{5, 1, 2} {
			t.Error("client did not require authentication")
			return
		}
		if rejectAuth {
			connection.Write([]byte{5, 0})
			return
		}
		connection.Write([]byte{5, 2})
		var auth [2]byte
		if _, err = io.ReadFull(connection, auth[:]); err != nil || auth[0] != 1 {
			return
		}
		user := make([]byte, int(auth[1]))
		if _, err = io.ReadFull(connection, user); err != nil {
			return
		}
		var size [1]byte
		if _, err = io.ReadFull(connection, size[:]); err != nil {
			return
		}
		password := make([]byte, int(size[0]))
		if _, err = io.ReadFull(connection, password); err != nil {
			return
		}
		if string(user) != proxy.Username || string(password) != proxy.Password {
			t.Error("isolated proxy auth did not match")
			return
		}
		connection.Write([]byte{1, 0})
		var request [10]byte
		if _, err = io.ReadFull(connection, request[:]); err != nil {
			return
		}
		if request[0] != 5 || request[1] != 1 || request[3] != 1 || net.IP(request[4:8]).String() != "8.8.8.8" || binary.BigEndian.Uint16(request[8:]) != 443 {
			t.Error("bootstrap destination was rewritten")
			return
		}
		connection.Write([]byte{5, 0, 0, 1, 0, 0, 0, 0, 0, 0})
		connection.Write([]byte("fixture"))
		io.Copy(io.Discard, connection)
	}()
	return g, direct
}

func TestBootstrapUnpinnedDestinationsUseAuthenticatedProxyOnly(t *testing.T) {
	gate, direct := bootstrapFixture(t, false)
	connection, err := gate.Dial(context.Background(), "8.8.8.8:443")
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close()
	value := make([]byte, 7)
	if _, err = io.ReadFull(connection, value); err != nil || string(value) != "fixture" {
		t.Fatal("proxy data path failed", err)
	}
	if direct.Load() != 0 {
		t.Fatal("bootstrap used a public host socket")
	}
	gate.FinishBootstrap()
	if _, err = connection.Read(value); err == nil {
		t.Fatal("existing bootstrap stream survived completion")
	}
	if gate.Allows("tcp", netip.MustParseAddrPort("8.8.8.8:443")) {
		t.Fatal("bootstrap destination remains allowed")
	}
	if !gate.Allows("tcp", netip.MustParseAddrPort("1.1.1.1:443")) {
		t.Fatal("selected node pin was revoked")
	}
}
func TestBootstrapCannotDowngradeProxyAuthentication(t *testing.T) {
	gate, direct := bootstrapFixture(t, true)
	if c, err := gate.Dial(context.Background(), "8.8.8.8:443"); err == nil {
		c.Close()
		t.Fatal("unauthenticated listener accepted")
	}
	if direct.Load() != 0 {
		t.Fatal("failed proxy fell back to direct")
	}
}
func TestBootstrapStillRejectsHostPrivateReservedUDPAndOtherPorts(t *testing.T) {
	gate, direct := bootstrapFixture(t, false)
	for _, destination := range []string{"127.0.0.1:80", "10.0.0.1:443", "169.254.169.254:80", "192.168.1.1:443", "100.64.1.1:443", "224.0.0.1:80", "[::1]:443", "[::ffff:8.8.8.8]:443", "8.8.8.8:22", "8.8.8.8:53"} {
		if _, err := gate.Dial(context.Background(), destination); err == nil {
			t.Fatal("unsafe bootstrap destination allowed", destination)
		}
	}
	if gate.Allows("udp", netip.MustParseAddrPort("8.8.8.8:443")) || direct.Load() != 0 {
		t.Fatal("bootstrap widened OS socket policy")
	}
	gate.Close()
	if gate.bootstrapAvailable() {
		t.Fatal("expired lease retained bootstrap")
	}
}
func TestBootstrapPolicyRejectsUnboundOrWeakProxyCredentials(t *testing.T) {
	for _, proxy := range []*BootstrapProxy{{Port: 80, Username: strings.Repeat("a", 32), Password: strings.Repeat("b", 64)}, {Port: 49152, Username: strings.Repeat("a", 32), Password: strings.Repeat("b", 64)}, {Port: 40000, Username: "guest", Password: "secret"}, {Port: 40000, Username: strings.Repeat("a", 32), Password: strings.Repeat("b", 63) + "\n"}} {
		policy := fixturePolicy()
		policy.Bootstrap = proxy
		if policy.Validate() == nil {
			t.Fatal("invalid bootstrap binding accepted")
		}
	}
}
func TestBootstrapDNSClosedAndIPv6QueriesDoNotUseHostDialer(t *testing.T) {
	gate, direct := bootstrapFixture(t, false)
	query := new(dns.Msg)
	query.SetQuestion("fixture.example.", dns.TypeAAAA)
	reply := gate.bootstrapDNS(context.Background(), query)
	if reply.Rcode != dns.RcodeSuccess || len(reply.Answer) != 0 || direct.Load() != 0 {
		t.Fatal("IPv6 DNS escaped")
	}
	gate.FinishBootstrap()
	query.SetQuestion("fixture.example.", dns.TypeA)
	reply = gate.bootstrapDNS(context.Background(), query)
	if reply.Rcode != dns.RcodeServerFailure || direct.Load() != 0 {
		t.Fatal("disabled bootstrap used host DNS")
	}
}
