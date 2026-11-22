package main

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"github.com/miekg/dns"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

type dnsRoundTrip func(*http.Request) (*http.Response, error)

func (f dnsRoundTrip) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func dnsFixtureQuery() *dns.Msg {
	q := new(dns.Msg)
	q.SetQuestion("Fixture.Example.", dns.TypeA)
	q.Id = 4567
	return q
}
func dnsFixtureResponse(q *dns.Msg) *dns.Msg {
	a := new(dns.Msg)
	a.SetReply(q)
	a.Answer = []dns.RR{&dns.A{Hdr: dns.RR_Header{Name: q.Question[0].Name, Rrtype: dns.TypeA, Class: dns.ClassINET, Ttl: 30}, A: net.ParseIP("9.9.9.9").To4()}}
	return a
}

func TestBootstrapDNSFallbackDoesNotEquateOneProviderFailureWithBadNode(t *testing.T) {
	var calls atomic.Int32
	answer, err := resolveBootstrapDNS(context.Background(), dnsFixtureQuery(), bootstrapResolvers, func(ctx context.Context, q *dns.Msg, e bootstrapResolver) (*dns.Msg, error) {
		calls.Add(1)
		if e.name == "cloudflare-dns.com" {
			return nil, errors.New("bootstrap_doh_http_failed")
		}
		return dnsFixtureResponse(q), nil
	})
	if err != nil || len(answer.Answer) != 1 || calls.Load() == 0 {
		t.Fatal("independent resolver fallback failed", err)
	}
}
func TestBootstrapDNSDeadlineAndSafeFailureCodes(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	_, err := resolveBootstrapDNS(ctx, dnsFixtureQuery(), bootstrapResolvers, func(c context.Context, q *dns.Msg, e bootstrapResolver) (*dns.Msg, error) {
		<-c.Done()
		return nil, c.Err()
	})
	if controlErrorCode(err) != "BOOTSTRAP_TIMEOUT" {
		t.Fatal("timeout hidden by generic DNS error", err)
	}
	for _, code := range []string{"bootstrap_auth_required", "bootstrap_connect_failed", "bootstrap_dns_tls_failed", "bootstrap_doh_http_failed", "bootstrap_doh_response_invalid"} {
		if controlErrorCode(errors.New(bootstrapFailure(errors.New(code)))) != strings.ToUpper(code) {
			t.Fatal("lost safe stage", code)
		}
	}
	if strings.Contains(controlErrorCode(errors.New("secret.example/password")), "secret") {
		t.Fatal("raw diagnostic leaked")
	}
}
func TestBootstrapDNSWireNormalizesIdAndValidatesQuestion(t *testing.T) {
	query := dnsFixtureQuery()
	for _, invalid := range []bool{false, true} {
		client := &http.Client{Transport: dnsRoundTrip(func(r *http.Request) (*http.Response, error) {
			if r.URL.Host != "dns.google" || r.URL.Scheme != "https" || r.Method != "POST" || r.Header.Get("Content-Type") != "application/dns-message" {
				t.Error("invalid HTTPS DNS request")
			}
			raw, _ := io.ReadAll(r.Body)
			var q dns.Msg
			if q.Unpack(raw) != nil || q.Id != 0 {
				t.Error("DoH ID was not normalized")
			}
			a := dnsFixtureResponse(&q)
			a.Question[0].Name = strings.ToLower(a.Question[0].Name)
			if invalid {
				a.Question[0].Name = "foreign.example."
			}
			data, _ := a.Pack()
			return &http.Response{StatusCode: 200, Header: http.Header{"Content-Type": []string{"application/dns-message"}}, Body: io.NopCloser(bytes.NewReader(data))}, nil
		})}
		answer, err := exchangeBootstrapDNS(context.Background(), query, bootstrapResolvers[1], client)
		if invalid {
			if err == nil {
				t.Fatal("foreign DNS question accepted")
			}
		} else if err != nil || answer.Id != query.Id || answer.Question[0] != query.Question[0] {
			t.Fatal("valid transaction rejected", err)
		}
	}
	if query.Id != 4567 {
		t.Fatal("original guest query mutated")
	}
}

// Actual TLS and SOCKS bytes, all on loopback. The fixed public destination is
// consumed by a synthetic authenticated proxy; no public socket is opened.
func TestBootstrapDNSUsesAuthenticatedProxyAndNamedTLS(t *testing.T) {
	var seen atomic.Int32
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.TLS == nil || r.TLS.ServerName == "" {
			t.Error("DNS request omitted named TLS")
		}
		raw, _ := io.ReadAll(r.Body)
		var q dns.Msg
		if q.Unpack(raw) != nil {
			w.WriteHeader(400)
			return
		}
		data, _ := dnsFixtureResponse(&q).Pack()
		w.Header().Set("Content-Type", "application/dns-message")
		w.Write(data)
		seen.Add(1)
	}))
	defer server.Close()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	p := fixturePolicy()
	p.Bootstrap = &BootstrapProxy{Port: listener.Addr().(*net.TCPAddr).Port, Username: strings.Repeat("a", 32), Password: strings.Repeat("b", 64)}
	var direct atomic.Int32
	gate, err := newGate(p, safeNetworks, func(context.Context, string, string) (net.Conn, error) { direct.Add(1); return nil, errClosed })
	if err != nil {
		t.Fatal(err)
	}
	defer gate.Close()
	if err = gate.Renew(p.Fingerprint(), 30); err != nil {
		t.Fatal(err)
	}
	go func() {
		connection, e := listener.Accept()
		if e != nil {
			return
		}
		defer connection.Close()
		connection.SetDeadline(time.Now().Add(4 * time.Second))
		var greeting [3]byte
		if _, e = io.ReadFull(connection, greeting[:]); e != nil || greeting != [3]byte{5, 1, 2} {
			return
		}
		connection.Write([]byte{5, 2})
		auth := make([]byte, 2+32+1+64)
		if _, e = io.ReadFull(connection, auth); e != nil || string(auth[2:34]) != p.Bootstrap.Username || string(auth[35:]) != p.Bootstrap.Password {
			return
		}
		connection.Write([]byte{1, 0})
		var destination [10]byte
		if _, e = io.ReadFull(connection, destination[:]); e != nil || !bytes.Equal(destination[:], []byte{5, 1, 0, 1, 8, 8, 8, 8, 1, 187}) {
			return
		}
		upstream, e := net.DialTimeout("tcp4", server.Listener.Addr().String(), time.Second)
		if e != nil {
			return
		}
		defer upstream.Close()
		connection.Write([]byte{5, 0, 0, 1, 0, 0, 0, 0, 0, 0})
		go io.Copy(upstream, connection)
		io.Copy(connection, upstream)
	}()
	endpoint := bootstrapResolver{server.Certificate().DNSNames[0], "8.8.8.8:443"}
	client := gate.bootstrapHTTP(endpoint)
	defer client.CloseIdleConnections()
	pool := x509.NewCertPool()
	pool.AddCert(server.Certificate())
	client.Transport.(*http.Transport).TLSClientConfig = &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS12}
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	answer, err := exchangeBootstrapDNS(ctx, dnsFixtureQuery(), endpoint, client)
	if err != nil || answer == nil || len(answer.Answer) != 1 || seen.Load() != 1 || direct.Load() != 0 {
		t.Fatal("guarded DNS TLS roundtrip failed", err)
	}
}

func TestBootstrapDNSCacheReusesWithinEpochAndClearsOnFinishOrClose(t *testing.T) {
	p := fixturePolicy()
	p.Bootstrap = &BootstrapProxy{Port: 49200, Username: strings.Repeat("a", 32), Password: strings.Repeat("b", 64)}
	gate, err := newGate(p, safeNetworks, noDial)
	if err != nil {
		t.Fatal(err)
	}
	defer gate.Close()
	if err = gate.Renew(p.Fingerprint(), 30); err != nil {
		t.Fatal(err)
	}
	q1 := dnsFixtureQuery()
	q1.Id = 1001
	gate.storeGeneralBootstrapDNS(q1, dnsFixtureResponse(q1))

	q2 := new(dns.Msg)
	q2.SetQuestion("fixture.example.", dns.TypeA)
	q2.Id = 2002
	cached := gate.cachedGeneralBootstrapDNS(q2)
	if cached == nil || cached.Id != 2002 || len(cached.Answer) != 1 {
		t.Fatal("expected cached general bootstrap DNS hit with updated transaction ID")
	}

	gate.FinishBootstrap()
	if gate.cachedGeneralBootstrapDNS(q2) != nil {
		t.Fatal("cached bootstrap DNS survived FinishBootstrap")
	}
}
