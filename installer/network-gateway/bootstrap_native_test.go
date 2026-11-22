package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/gopacket"
	"github.com/google/gopacket/layers"
	"github.com/miekg/dns"
)

// Explicit fixture-only run. It combines the shipped gateway code, actual
// Mihomo, authenticated SOCKS, Trojan TLS, DoH TLS, multi-address HTTP and the
// virtual Ethernet TCP transport. No public or customer socket is opened.
func TestNativeCompleteBootstrapAndGuestEthernet(t *testing.T) {
	core := os.Getenv("CLAUDOME_NATIVE_CORE")
	nodeFile := os.Getenv("CLAUDOME_NATIVE_NODE")
	if core == "" || nodeFile == "" {
		t.Skip("requires verified native core and synthetic production-parser fixture")
	}
	raw, err := os.ReadFile(nodeFile)
	if err != nil {
		t.Fatal(err)
	}
	var node map[string]any
	if json.Unmarshal(raw, &node) != nil || node["password"] != "fictional-trojan-password" || node["sni"] != "trojan-fixture.invalid" || node["server"] != "node.example" {
		t.Fatal("not the isolated synthetic parser fixture")
	}
	var dnsCalls, httpCalls, rejectedAddress atomic.Int32
	web := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		httpCalls.Add(1)
		time.Sleep(1800 * time.Millisecond)
		if r.Host != "www.msftconnecttest.com" || r.URL.Path != "/connecttest.txt" {
			w.WriteHeader(400)
			return
		}
		w.Write([]byte("Microsoft Connect Test"))
	}))
	defer web.Close()
	doh := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		data, _ := io.ReadAll(r.Body)
		var q dns.Msg
		if q.Unpack(data) != nil || q.Id != 0 {
			w.WriteHeader(400)
			return
		}
		dnsCalls.Add(1)
		time.Sleep(1800 * time.Millisecond)
		a := dnsFixtureResponse(&q)
		a.Answer = append(a.Answer, &dns.A{Hdr: *a.Answer[0].Header(), A: net.ParseIP("149.112.112.112").To4()})
		wire, _ := a.Pack()
		w.Header().Set("Content-Type", "application/dns-message")
		w.Write(wire)
	}))
	defer doh.Close()
	tlsListener, err := tls.Listen("tcp4", "127.0.0.1:0", &tls.Config{Certificates: doh.TLS.Certificates, GetConfigForClient: func(c *tls.ClientHelloInfo) (*tls.Config, error) {
		if c.ServerName != "trojan-fixture.invalid" {
			return nil, errors.New("fixture_sni_rejected")
		}
		return nil, nil
	}})
	if err != nil {
		t.Fatal(err)
	}
	defer tlsListener.Close()
	go func() {
		for {
			c, e := tlsListener.Accept()
			if e != nil {
				return
			}
			go func(c net.Conn) {
				defer c.Close()
				c.SetDeadline(time.Now().Add(15 * time.Second))
				auth := make([]byte, 58)
				if _, e := io.ReadFull(c, auth); e != nil {
					return
				}
				hash := sha256.Sum224([]byte("fictional-trojan-password"))
				if string(auth) != hex.EncodeToString(hash[:])+"\r\n" {
					return
				}
				var head [10]byte
				if _, e := io.ReadFull(c, head[:]); e != nil || head[0] != 1 || head[1] != 1 || head[8] != '\r' || head[9] != '\n' {
					return
				}
				destination := net.JoinHostPort(net.IP(head[2:6]).String(), strconv.Itoa(int(head[6])<<8|int(head[7])))
				var target string
				switch destination {
				case "8.8.8.8:443":
					target = doh.Listener.Addr().String()
				case "149.112.112.112:80":
					target = web.Listener.Addr().String()
				case "9.9.9.9:80":
					rejectedAddress.Add(1)
					return
				default:
					return
				}
				upstream, e := net.DialTimeout("tcp4", target, time.Second)
				if e != nil {
					return
				}
				defer upstream.Close()
				go io.Copy(upstream, c)
				io.Copy(c, upstream)
			}(c)
		}
	}()
	reservation, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	proxyPort := reservation.Addr().(*net.TCPAddr).Port
	reservation.Close()
	node["name"] = "SelectedNode"
	node["server"] = "127.0.0.1"
	node["port"] = tlsListener.Addr().(*net.TCPAddr).Port
	node["skip-cert-verify"] = true // fixture certificate only
	username, password := strings.Repeat("a", 32), strings.Repeat("b", 64)
	config := map[string]any{"mode": "rule", "socks-port": proxyPort, "bind-address": "127.0.0.1", "allow-lan": false, "authentication": []string{username + ":" + password}, "skip-auth-prefixes": []string{"0.0.0.0/32"}, "tun": map[string]any{"enable": false}, "dns": map[string]any{"enable": false}, "log-level": "silent", "ipv6": false, "proxies": []any{node}, "proxy-groups": []any{map[string]any{"name": "Claude", "type": "select", "proxies": []string{"SelectedNode"}}}, "rules": []string{"MATCH,Claude"}}
	configuration, _ := json.Marshal(config)
	ctx, cancel := context.WithTimeout(context.Background(), 25*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, core, "-d", filepath.Join(t.TempDir(), "core"), "-f", "-")
	cmd.Stdin = bytes.NewReader(configuration)
	cmd.Stdout = io.Discard
	cmd.Stderr = io.Discard
	for _, entry := range os.Environ() {
		key, _, _ := strings.Cut(entry, "=")
		if !strings.HasPrefix(strings.ToUpper(key), "CLASH_") && !strings.HasPrefix(strings.ToUpper(key), "MIHOMO_") {
			cmd.Env = append(cmd.Env, entry)
		}
	}
	if err = cmd.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { cmd.Process.Kill(); cmd.Wait() }()
	ready := false
	for i := 0; i < 60; i++ {
		c, e := net.DialTimeout("tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(proxyPort)), 100*time.Millisecond)
		if e == nil {
			c.Close()
			ready = true
			break
		}
		time.Sleep(50 * time.Millisecond)
	}
	if !ready {
		t.Fatal("native core did not start")
	}
	p := fixturePolicy()
	p.Bootstrap = &BootstrapProxy{Port: proxyPort, Username: username, Password: password}
	var direct atomic.Int32
	gate, err := newGate(p, safeNetworks, func(context.Context, string, string) (net.Conn, error) { direct.Add(1); return nil, errClosed })
	if err != nil {
		t.Fatal(err)
	}
	defer gate.Close()
	if err = gate.Renew(p.Fingerprint(), 3); err != nil {
		t.Fatal(err)
	}
	endpoint := bootstrapResolver{doh.Certificate().DNSNames[0], "8.8.8.8:443"}
	pool := x509.NewCertPool()
	pool.AddCert(doh.Certificate())
	started := time.Now()
	err = gate.probeBootstrapDNSUsing(ctx, func(c context.Context, q *dns.Msg) (*dns.Msg, error) {
		client := gate.bootstrapHTTP(endpoint)
		defer client.CloseIdleConnections()
		client.Transport.(*http.Transport).TLSClientConfig = &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS12}
		return exchangeBootstrapDNS(c, q, endpoint, client)
	})
	if err != nil {
		t.Fatal("native DNS phase", err)
	}
	if err = gate.Renew(p.Fingerprint(), 3); err != nil {
		t.Fatal(err)
	}
	if err = gate.ProbeBootstrapHTTP(ctx); err != nil {
		t.Fatal("native multi-address HTTP phase", err)
	}
	if time.Since(started) < 3*time.Second || !gate.bootstrapAvailable() {
		t.Fatal("split probe did not demonstrate renewal across combined slow stages")
	}
	if err = gate.Renew(p.Fingerprint(), 3); err != nil {
		t.Fatal(err)
	}
	// A virtual guest's UDP DNS question receives the current tested address,
	// followed by a full Ethernet TCP request through the same native core.
	dnsFrames := make(chan []byte, 8)
	packetDNS, e := newPacketNetwork(gate, func(frame []byte) error {
		select {
		case dnsFrames <- append([]byte(nil), frame...):
		default:
		}
		return nil
	})
	if e != nil {
		t.Fatal(e)
	}
	question := new(dns.Msg)
	question.SetQuestion("www.msftconnecttest.com.", dns.TypeA)
	question.Id = 2345
	wire, _ := question.Pack()
	packetDNS.Input(udpFrame(t, p, guestIP, gatewayIP, 32000, 53, wire))
	select {
	case frame := <-dnsFrames:
		packet := gopacket.NewPacket(frame, layers.LayerTypeEthernet, gopacket.Default)
		layer := packet.Layer(layers.LayerTypeUDP)
		if layer == nil {
			t.Fatal("guest DNS response was not UDP")
		}
		var reply dns.Msg
		if reply.Unpack(layer.(*layers.UDP).Payload) != nil || reply.Id != 2345 || len(reply.Answer) == 0 || reply.Answer[0].(*dns.A).A.String() != "149.112.112.112" {
			t.Fatal("virtual guest DNS did not prefer verified working address")
		}
	case <-time.After(time.Second):
		t.Fatal("virtual guest DNS did not answer")
	}
	packetDNS.Close()
	if err = gate.Renew(p.Fingerprint(), 3); err != nil {
		t.Fatal(err)
	}
	dial := syntheticGuestDialer(t, gate)
	connection, err := dial("149.112.112.112", 80)
	if err != nil {
		t.Fatal("virtual guest TCP dial", err)
	}
	defer connection.Close()
	connection.SetDeadline(time.Now().Add(4 * time.Second))
	connection.Write([]byte("GET /connecttest.txt HTTP/1.1\r\nHost: www.msftconnecttest.com\r\nConnection: close\r\n\r\n"))
	response, err := io.ReadAll(connection)
	if err != nil || !bytes.Contains(response, []byte("Microsoft Connect Test")) {
		t.Fatal("virtual guest Ethernet to native proxy payload failed", err)
	}
	if direct.Load() != 0 || dnsCalls.Load() == 0 || httpCalls.Load() < 2 || rejectedAddress.Load() == 0 {
		t.Fatal("native data-path coverage incomplete")
	}
	gate.FinishBootstrap()
	if c, e := dial("149.112.112.112", 80); e == nil {
		c.Close()
		t.Fatal("guest bootstrap survived retirement")
	}
}
