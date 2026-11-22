package main

import (
	"bytes"
	"context"
	"io"
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/gopacket"
	"github.com/google/gopacket/layers"
	"github.com/insomniacslk/dhcp/dhcpv4"
	"github.com/miekg/dns"
	"gvisor.dev/gvisor/pkg/buffer"
	"gvisor.dev/gvisor/pkg/tcpip"
	"gvisor.dev/gvisor/pkg/tcpip/adapters/gonet"
	"gvisor.dev/gvisor/pkg/tcpip/header"
	"gvisor.dev/gvisor/pkg/tcpip/link/channel"
	"gvisor.dev/gvisor/pkg/tcpip/link/ethernet"
	"gvisor.dev/gvisor/pkg/tcpip/network/arp"
	"gvisor.dev/gvisor/pkg/tcpip/network/ipv4"
	"gvisor.dev/gvisor/pkg/tcpip/stack"
	"gvisor.dev/gvisor/pkg/tcpip/transport/tcp"
)

// Real guest/gateway TCP stacks exchange Ethernet frames entirely in memory.
// The only OS dialer is replaced with an echo pipe; no public sockets are used.
func TestEthernetTCPAllowedEchoAndRevocation(t *testing.T) {
	var calls atomic.Int32
	g := openedGate(t, func(_ context.Context, network, dst string) (net.Conn, error) {
		calls.Add(1)
		if network != "tcp" || dst != "1.1.1.1:443" {
			t.Errorf("unexpected OS dial: %s %s", network, dst)
		}
		a, b := net.Pipe()
		go func() { defer b.Close(); io.Copy(b, b) }()
		return a, nil
	})
	mac, _ := net.ParseMAC(g.policy.GuestMAC)
	link := channel.New(256, 1514, tcpip.LinkAddress(mac))
	guest := stack.New(stack.Options{NetworkProtocols: []stack.NetworkProtocolFactory{ipv4.NewProtocol, arp.NewProtocol}, TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol}})
	if err := guest.CreateNIC(1, ethernet.New(link)); err != nil {
		t.Fatal(err)
	}
	if err := guest.AddProtocolAddress(1, tcpip.ProtocolAddress{Protocol: ipv4.ProtocolNumber, AddressWithPrefix: tcpip.AddressWithPrefix{Address: tcpip.AddrFrom4Slice(net.ParseIP(guestIP).To4()), PrefixLen: 24}}, stack.AddressProperties{}); err != nil {
		t.Fatal(err)
	}
	guest.SetRouteTable([]tcpip.Route{{Destination: header.IPv4EmptySubnet, Gateway: tcpip.AddrFrom4Slice(net.ParseIP(gatewayIP).To4()), NIC: 1}})
	network, err := newPacketNetwork(g, func(frame []byte) error {
		p := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(append([]byte(nil), frame...))})
		defer p.DecRef()
		link.InjectInbound(0, p)
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			p := link.ReadContext(ctx)
			if p == nil {
				return
			}
			v := p.ToView()
			frame := append([]byte(nil), v.AsSlice()...)
			v.Release()
			p.DecRef()
			network.Input(frame)
		}
	}()
	t.Cleanup(func() {
		g.Close()
		cancel()
		guest.Close()
		link.Close()
		<-done
		network.Close()
		guest.Wait()
	})
	dial := func(ip string, port uint16) (net.Conn, error) {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		return gonet.DialContextTCP(ctx, guest, tcpip.FullAddress{NIC: 1, Addr: tcpip.AddrFrom4Slice(net.ParseIP(ip).To4()), Port: port}, ipv4.ProtocolNumber)
	}
	c, err := dial("1.1.1.1", 443)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(2 * time.Second))
	want := []byte("isolated-ethernet-echo")
	if _, err = c.Write(want); err != nil {
		t.Fatal(err)
	}
	got := make([]byte, len(want))
	if _, err = io.ReadFull(c, got); err != nil || !bytes.Equal(want, got) {
		t.Fatalf("echo: %q %v", got, err)
	}
	for _, dst := range []string{"10.0.2.2", "192.168.1.1", "169.254.169.254", "8.8.8.8"} {
		if denied, err := dial(dst, 443); err == nil {
			denied.Close()
			t.Fatalf("denied TCP succeeded: %s", dst)
		}
	}
	if calls.Load() != 1 {
		t.Fatalf("blocked packets reached OS dialer: %d", calls.Load())
	}
	g.Close()
	c.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err = c.Read(make([]byte, 1)); err == nil {
		t.Fatal("stream survived revocation")
	}
}

func udpFrame(t *testing.T, p Policy, src, dst string, sport, dport uint16, payload []byte) []byte {
	t.Helper()
	mac, _ := net.ParseMAC(p.GuestMAC)
	eth := &layers.Ethernet{SrcMAC: mac, DstMAC: gatewayMAC, EthernetType: layers.EthernetTypeIPv4}
	ip := &layers.IPv4{Version: 4, IHL: 5, TTL: 64, Protocol: layers.IPProtocolUDP, SrcIP: net.ParseIP(src).To4(), DstIP: net.ParseIP(dst).To4()}
	udp := &layers.UDP{SrcPort: layers.UDPPort(sport), DstPort: layers.UDPPort(dport)}
	udp.SetNetworkLayerForChecksum(ip)
	b := gopacket.NewSerializeBuffer()
	if err := gopacket.SerializeLayers(b, gopacket.SerializeOptions{FixLengths: true, ComputeChecksums: true}, eth, ip, udp, gopacket.Payload(payload)); err != nil {
		t.Fatal(err)
	}
	return b.Bytes()
}
func TestPacketLocalDNSDHCPAndNoUDPEscape(t *testing.T) {
	g := fixtureGate(t, noDial)
	replies := make(chan []byte, 8)
	n, err := newPacketNetwork(g, func(b []byte) error { replies <- append([]byte(nil), b...); return nil })
	if err != nil {
		t.Fatal(err)
	}
	defer n.Close()
	reply := func() []byte {
		select {
		case b := <-replies:
			return b
		case <-time.After(time.Second):
			t.Fatal("local reply missing")
			return nil
		}
	}
	for _, name := range []string{"node.example.", "private-host.example."} {
		q := new(dns.Msg)
		q.SetQuestion(name, dns.TypeA)
		wire, _ := q.Pack()
		n.Input(udpFrame(t, g.policy, guestIP, gatewayIP, 42000, 53, wire))
		p := gopacket.NewPacket(reply(), layers.LayerTypeEthernet, gopacket.Default)
		udp := p.Layer(layers.LayerTypeUDP).(*layers.UDP)
		var answer dns.Msg
		if err := answer.Unpack(udp.Payload); err != nil {
			t.Fatal(err)
		}
		if name == "node.example." {
			if len(answer.Answer) != 1 || answer.Answer[0].(*dns.A).A.String() != "1.1.1.1" {
				t.Fatal("node DNS changed")
			}
		} else if answer.Rcode != dns.RcodeRefused {
			t.Fatal("arbitrary DNS accepted")
		}
	}
	mac, _ := net.ParseMAC(g.policy.GuestMAC)
	discover, err := dhcpv4.NewDiscovery(mac)
	if err != nil {
		t.Fatal(err)
	}
	n.Input(udpFrame(t, g.policy, "0.0.0.0", "255.255.255.255", 68, 67, discover.ToBytes()))
	p := gopacket.NewPacket(reply(), layers.LayerTypeEthernet, gopacket.Default)
	offer, err := dhcpv4.FromBytes(p.Layer(layers.LayerTypeUDP).(*layers.UDP).Payload)
	if err != nil || offer.YourIPAddr.String() != guestIP || offer.MessageType() != dhcpv4.MessageTypeOffer {
		t.Fatal("DHCP did not offer synthetic address")
	}
	n.Input(udpFrame(t, g.policy, guestIP, "1.1.1.1", 42000, 443, []byte("no QUIC")))
	n.Input(udpFrame(t, g.policy, guestIP, "8.8.8.8", 42000, 53, []byte("no external DNS")))
	spoof := udpFrame(t, g.policy, guestIP, gatewayIP, 42000, 53, []byte("bad"))
	spoof[6] ^= 2
	n.Input(spoof)
	ipv6 := make([]byte, 64)
	copy(ipv6[6:12], mac)
	ipv6[12] = 0x86
	ipv6[13] = 0xdd
	n.Input(ipv6)
	select {
	case <-replies:
		t.Fatal("unexpected reply to denied transport")
	case <-time.After(50 * time.Millisecond):
	}
}

func syntheticGuestDialer(t *testing.T, g *Gate) func(string, uint16) (net.Conn, error) {
	t.Helper()
	mac, _ := net.ParseMAC(g.policy.GuestMAC)
	link := channel.New(256, 1514, tcpip.LinkAddress(mac))
	guest := stack.New(stack.Options{NetworkProtocols: []stack.NetworkProtocolFactory{ipv4.NewProtocol, arp.NewProtocol}, TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol}})
	if err := guest.CreateNIC(1, ethernet.New(link)); err != nil {
		t.Fatal(err)
	}
	if err := guest.AddProtocolAddress(1, tcpip.ProtocolAddress{Protocol: ipv4.ProtocolNumber, AddressWithPrefix: tcpip.AddressWithPrefix{Address: tcpip.AddrFrom4Slice(net.ParseIP(guestIP).To4()), PrefixLen: 24}}, stack.AddressProperties{}); err != nil {
		t.Fatal(err)
	}
	guest.SetRouteTable([]tcpip.Route{{Destination: header.IPv4EmptySubnet, Gateway: tcpip.AddrFrom4Slice(net.ParseIP(gatewayIP).To4()), NIC: 1}})
	network, err := newPacketNetwork(g, func(frame []byte) error {
		p := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(append([]byte(nil), frame...))})
		defer p.DecRef()
		link.InjectInbound(0, p)
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			p := link.ReadContext(ctx)
			if p == nil {
				return
			}
			v := p.ToView()
			frame := append([]byte(nil), v.AsSlice()...)
			v.Release()
			p.DecRef()
			network.Input(frame)
		}
	}()
	t.Cleanup(func() {
		g.Close()
		cancel()
		guest.Close()
		link.Close()
		<-done
		network.Close()
		guest.Wait()
	})
	dial := func(ip string, port uint16) (net.Conn, error) {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		return gonet.DialContextTCP(ctx, guest, tcpip.FullAddress{NIC: 1, Addr: tcpip.AddrFrom4Slice(net.ParseIP(ip).To4()), Port: port}, ipv4.ProtocolNumber)
	}

	return dial
}
