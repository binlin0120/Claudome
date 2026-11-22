package main

import (
	"context"
	"errors"
	"github.com/miekg/dns"
	"io"
	"net"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func readyProbeGate(t *testing.T) *Gate {
	t.Helper()
	p := fixturePolicy()
	p.Bootstrap = &BootstrapProxy{Port: 40001, Username: strings.Repeat("a", 32), Password: strings.Repeat("b", 64)}
	g, e := newGate(p, safeNetworks, func(context.Context, string, string) (net.Conn, error) {
		t.Error("probe escaped proxy")
		return nil, errClosed
	})
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(g.Close)
	if e = g.Renew(p.Fingerprint(), 30); e != nil {
		t.Fatal(e)
	}
	e = g.probeBootstrapDNSUsing(context.Background(), func(c context.Context, q *dns.Msg) (*dns.Msg, error) {
		a := dnsFixtureResponse(q)
		a.Answer = append(a.Answer, &dns.A{Hdr: *a.Answer[0].Header(), A: net.ParseIP("149.112.112.112").To4()})
		return a, nil
	})
	if e != nil {
		t.Fatal(e)
	}
	return g
}
func TestBootstrapHTTPTriesOtherCurrentDNSAddresses(t *testing.T) {
	g := readyProbeGate(t)
	var calls atomic.Int32
	e := g.probeBootstrapHTTPUsing(context.Background(), func(c context.Context, address string) error {
		calls.Add(1)
		if address == "9.9.9.9:80" {
			return errors.New("bootstrap_http_failed")
		}
		return nil
	})
	if e != nil || calls.Load() == 0 {
		t.Fatal("usable alternate address ignored", e)
	}
}
func TestBootstrapHTTPDoesNotFollowPortalOrWrongContent(t *testing.T) {
	for _, test := range []struct {
		status     int
		body, code string
	}{{302, "", "BOOTSTRAP_HTTP_REDIRECT"}, {200, "login portal", "BOOTSTRAP_HTTP_MISMATCH"}, {200, "Microsoft Connect Test", "OK"}} {
		client := &http.Client{Transport: dnsRoundTrip(func(r *http.Request) (*http.Response, error) {
			if r.URL.String() != "http://www.msftconnecttest.com/connecttest.txt" || r.Host != "www.msftconnecttest.com" {
				t.Error("incorrect Windows probe Host/path")
			}
			return &http.Response{StatusCode: test.status, Header: http.Header{}, Body: io.NopCloser(strings.NewReader(test.body))}, nil
		})}
		if code := controlErrorCode(exchangeBootstrapWeb(context.Background(), client)); code != test.code {
			t.Fatal("portal/response misclassified", code, test.code)
		}
	}
}
func TestBootstrapHTTPHonorsDeadlineAndClosedLease(t *testing.T) {
	g := readyProbeGate(t)
	c, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	e := g.probeBootstrapHTTPUsing(c, func(c context.Context, a string) error { <-c.Done(); return errors.New("bootstrap_http_timeout") })
	if controlErrorCode(e) != "BOOTSTRAP_HTTP_TIMEOUT" {
		t.Fatal("HTTP timeout still generic", e)
	}
	g.Close()
	if g.ProbeBootstrapHTTP(context.Background()) == nil {
		t.Fatal("stale DNS result bypassed closed lease")
	}
}
func TestBootstrapProbeDoesNotRetainPrivateOrOldDNSAddresses(t *testing.T) {
	g := readyProbeGate(t)
	e := g.probeBootstrapDNSUsing(context.Background(), func(c context.Context, q *dns.Msg) (*dns.Msg, error) {
		a := dnsFixtureResponse(q)
		a.Answer[0].(*dns.A).A = net.ParseIP("127.0.0.1").To4()
		return a, nil
	})
	if controlErrorCode(e) != "BOOTSTRAP_DNS_EMPTY" {
		t.Fatal("private probe address accepted", e)
	}
	if g.ProbeBootstrapHTTP(context.Background()) == nil {
		t.Fatal("failed new DNS left old probe results usable")
	}
}

func TestBootstrapGuestDNSPrefersVerifiedCurrentAddressWithoutExtendingTTL(t *testing.T) {
	g := readyProbeGate(t)
	if e := g.probeBootstrapHTTPUsing(context.Background(), func(c context.Context, destination string) error {
		if destination == "149.112.112.112:80" {
			return nil
		}
		return errors.New("bootstrap_http_failed")
	}); e != nil {
		t.Fatal(e)
	}
	q := new(dns.Msg)
	q.SetQuestion("www.msftconnecttest.com.", dns.TypeA)
	q.Id = 1234
	answer := g.bootstrapDNS(context.Background(), q)
	if answer.Id != 1234 || len(answer.Answer) != 2 || answer.Answer[0].(*dns.A).A.String() != "149.112.112.112" || answer.Answer[0].Header().Ttl > 30 {
		t.Fatal("guest did not receive verified short-lived address order")
	}
	g.mu.Lock()
	g.probeCreated = time.Now().Add(-2 * time.Second)
	for _, rr := range g.probeAnswer.Answer {
		rr.Header().Ttl = 5
	}
	g.mu.Unlock()
	aged := g.cachedBootstrapProbeDNS(q)
	if aged == nil || aged.Answer[0].Header().Ttl > 3 {
		t.Fatal("cached reply did not account for elapsed time since upstream resolution")
	}
	g.mu.Lock()
	g.probeCreated = time.Now().Add(-6 * time.Second)
	g.mu.Unlock()
	if g.cachedBootstrapProbeDNS(q) != nil {
		t.Fatal("cached probe address outlived upstream TTL")
	}
	g.FinishBootstrap()
	if g.cachedBootstrapProbeDNS(q) != nil {
		t.Fatal("retired bootstrap still served cached DNS")
	}
}

func TestBootstrapWebClientNeverFollowsPortalRedirect(t *testing.T) {
	g := readyProbeGate(t)
	client := g.bootstrapWebHTTP("9.9.9.9:80")
	var calls atomic.Int32
	client.Transport = dnsRoundTrip(func(r *http.Request) (*http.Response, error) {
		calls.Add(1)
		return &http.Response{StatusCode: 302, Header: http.Header{"Location": []string{"http://192.168.1.1/login"}}, Body: io.NopCloser(strings.NewReader(""))}, nil
	})
	if code := controlErrorCode(exchangeBootstrapWeb(context.Background(), client)); code != "BOOTSTRAP_HTTP_REDIRECT" || calls.Load() != 1 {
		t.Fatal("portal redirect followed or hidden", code, calls.Load())
	}
}
