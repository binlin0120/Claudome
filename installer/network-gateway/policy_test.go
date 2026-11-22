package main

import (
	"context"
	"errors"
	"io"
	"net"
	"net/netip"
	"sync/atomic"
	"testing"
	"time"
)

func fixturePolicy() Policy {
	return Policy{VMID: "11111111-1111-1111-1111-111111111111", RunID: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", GuestMAC: "08:00:27:11:22:33", GuestPort: 49152, ListenPort: 49153, EndpointAddresses: []string{"1.1.1.1"}, EndpointPort: 443, NodeHost: "node.example"}
}
func safeNetworks() ([]netip.Prefix, error) {
	return []netip.Prefix{netip.MustParsePrefix("10.2.3.0/24"), netip.MustParsePrefix("127.0.0.0/8")}, nil
}
func fixtureGate(t *testing.T, dial func(context.Context, string, string) (net.Conn, error)) *Gate {
	t.Helper()
	g, err := newGate(fixturePolicy(), safeNetworks, dial)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(g.Close)
	return g
}
func openedGate(t *testing.T, dial func(context.Context, string, string) (net.Conn, error)) *Gate {
	t.Helper()
	g := fixtureGate(t, dial)
	if err := g.Renew(g.policy.Fingerprint(), 30); err != nil {
		t.Fatal(err)
	}
	return g
}
func noDial(context.Context, string, string) (net.Conn, error) {
	return nil, errors.New("must_not_dial")
}
func TestDefaultClosedAndPinnedSocketOnly(t *testing.T) {
	var calls atomic.Int32
	g := fixtureGate(t, func(context.Context, string, string) (net.Conn, error) {
		calls.Add(1)
		return nil, errors.New("fixture")
	})
	if _, err := g.Dial(context.Background(), "1.1.1.1:443"); err == nil {
		t.Fatal("default gate open")
	}
	if err := g.Renew(g.policy.Fingerprint(), 30); err != nil {
		t.Fatal(err)
	}
	for _, raw := range []string{"127.0.0.1:443", "10.0.2.2:443", "169.254.169.254:80", "192.168.1.1:445", "100.64.0.1:22", "8.8.8.8:443", "1.1.1.1:80", "[::1]:443", "[::ffff:1.1.1.1]:443"} {
		if _, err := g.Dial(context.Background(), raw); err == nil {
			t.Fatalf("allowed %s", raw)
		}
	}
	if calls.Load() != 0 {
		t.Fatal("denied packet reached host dialer")
	}
	if !g.Allows("tcp", netip.MustParseAddrPort("1.1.1.1:443")) || g.Allows("udp", netip.MustParseAddrPort("1.1.1.1:443")) {
		t.Fatal("transport policy wrong")
	}
}
func TestReservedEndpointsRejected(t *testing.T) {
	for _, raw := range []string{"0.0.0.0", "10.1.1.1", "127.0.0.1", "100.64.1.2", "169.254.169.254", "172.16.0.1", "192.168.0.1", "192.0.2.1", "198.18.1.1", "203.0.113.1", "224.0.0.1", "255.255.255.255", "::1", "::ffff:1.1.1.1", "64:ff9b::a00:1"} {
		t.Run(raw, func(t *testing.T) {
			p := fixturePolicy()
			p.EndpointAddresses = []string{raw}
			if p.Validate() == nil {
				t.Fatal("unsafe endpoint accepted")
			}
		})
	}
}
func TestHostAddressAndPublicLANSubnetDenied(t *testing.T) {
	g := fixtureGate(t, noDial)
	g.networks = func() ([]netip.Prefix, error) { return []netip.Prefix{netip.MustParsePrefix("1.1.1.0/24")}, nil }
	if g.Renew(g.policy.Fingerprint(), 30) == nil {
		t.Fatal("local public subnet accepted")
	}
}
func TestUnknownHostTopologyFailsClosed(t *testing.T) {
	g := fixtureGate(t, noDial)
	g.networks = func() ([]netip.Prefix, error) { return nil, errors.New("fixture") }
	if g.Renew(g.policy.Fingerprint(), 30) == nil {
		t.Fatal("failed interface query accepted")
	}
}
func TestForgedPolicyCannotRenew(t *testing.T) {
	g := fixtureGate(t, noDial)
	if g.Renew("stale-policy", 30) == nil || g.Renew(g.policy.Fingerprint(), 31) == nil {
		t.Fatal("invalid authorization accepted")
	}
}
func TestCloseRevokesExistingStream(t *testing.T) {
	local, remote := net.Pipe()
	defer remote.Close()
	g := openedGate(t, func(context.Context, string, string) (net.Conn, error) { return local, nil })
	c, err := g.Dial(context.Background(), "1.1.1.1:443")
	if err != nil {
		t.Fatal(err)
	}
	g.Close()
	if _, err := c.Write([]byte("blocked")); err == nil {
		t.Fatal("old stream survived")
	}
	remote.SetReadDeadline(time.Now().Add(time.Second))
	if _, err := remote.Read(make([]byte, 1)); err != io.EOF {
		t.Fatalf("peer was not closed: %v", err)
	}
}
func TestRevocationDuringDialCannotRegisterLateConnection(t *testing.T) {
	started, finish := make(chan struct{}), make(chan struct{})
	local, remote := net.Pipe()
	defer remote.Close()
	g := openedGate(t, func(context.Context, string, string) (net.Conn, error) { close(started); <-finish; return local, nil })
	result := make(chan error, 1)
	go func() { _, err := g.Dial(context.Background(), "1.1.1.1:443"); result <- err }()
	<-started
	g.Close()
	close(finish)
	if <-result == nil {
		t.Fatal("revoked dial registered")
	}
	remote.SetReadDeadline(time.Now().Add(time.Second))
	if _, err := remote.Read(make([]byte, 1)); err != io.EOF {
		t.Fatal("late connection not closed")
	}
}
func TestLeaseExpiryClosesStreams(t *testing.T) {
	local, remote := net.Pipe()
	defer remote.Close()
	g := fixtureGate(t, func(context.Context, string, string) (net.Conn, error) { return local, nil })
	if err := g.Renew(g.policy.Fingerprint(), 1); err != nil {
		t.Fatal(err)
	}
	if _, err := g.Dial(context.Background(), "1.1.1.1:443"); err != nil {
		t.Fatal(err)
	}
	remote.SetReadDeadline(time.Now().Add(3 * time.Second))
	if _, err := remote.Read(make([]byte, 1)); err != io.EOF {
		t.Fatalf("lease did not close: %v", err)
	}
}
func TestNodeFailureHasNoFallback(t *testing.T) {
	var attempts atomic.Int32
	g := openedGate(t, func(_ context.Context, network, address string) (net.Conn, error) {
		attempts.Add(1)
		if address != "1.1.1.1:443" || network != "tcp" {
			t.Fatal("fallback occurred")
		}
		return nil, errors.New("offline")
	})
	if _, err := g.Dial(context.Background(), "1.1.1.1:443"); err == nil {
		t.Fatal("failed node passed")
	}
	if attempts.Load() != 1 {
		t.Fatal("unexpected alternate connection")
	}
}
