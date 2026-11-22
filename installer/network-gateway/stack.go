package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync"
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
	"gvisor.dev/gvisor/pkg/waiter"
)

const gatewayIP = "198.19.250.1"
const guestIP = "198.19.250.10"

var gatewayMAC = net.HardwareAddr{2, 0x43, 0x45, 0, 0, 1}

type PacketNetwork struct {
	ctx      context.Context
	stack    *stack.Stack
	link     *channel.Endpoint
	gate     *Gate
	mac      net.HardwareAddr
	emit     func([]byte) error
	sendLock sync.Mutex
	cancel   context.CancelFunc
	done     chan struct{}
	slots    chan struct{}
}

func newPacketNetwork(gate *Gate, emit func([]byte) error) (*PacketNetwork, error) {
	ctx, cancel := context.WithCancel(context.Background())
	n := &PacketNetwork{ctx: ctx, gate: gate, emit: emit, cancel: cancel, done: make(chan struct{}), slots: make(chan struct{}, 256)}
	n.mac, _ = net.ParseMAC(gate.policy.GuestMAC)
	n.link = channel.New(4096, 1514, tcpip.LinkAddress(gatewayMAC))
	n.stack = stack.New(stack.Options{NetworkProtocols: []stack.NetworkProtocolFactory{ipv4.NewProtocol, arp.NewProtocol}, TransportProtocols: []stack.TransportProtocolFactory{tcp.NewProtocol}})
	sack := tcpip.TCPSACKEnabled(true)
	_ = n.stack.SetTransportProtocolOption(tcp.ProtocolNumber, &sack)
	moderate := tcpip.TCPModerateReceiveBufferOption(true)
	_ = n.stack.SetTransportProtocolOption(tcp.ProtocolNumber, &moderate)
	rxBuf := tcpip.TCPReceiveBufferSizeRangeOption{Min: 4096, Default: 1 << 20, Max: 4 << 20}
	_ = n.stack.SetTransportProtocolOption(tcp.ProtocolNumber, &rxBuf)
	txBuf := tcpip.TCPSendBufferSizeRangeOption{Min: 4096, Default: 1 << 20, Max: 4 << 20}
	_ = n.stack.SetTransportProtocolOption(tcp.ProtocolNumber, &txBuf)
	if err := n.stack.CreateNIC(1, ethernet.New(n.link)); err != nil {
		cancel()
		return nil, errors.New("stack_nic_failed")
	}
	if err := n.stack.AddProtocolAddress(1, tcpip.ProtocolAddress{Protocol: ipv4.ProtocolNumber, AddressWithPrefix: tcpip.AddrFrom4Slice(net.ParseIP(gatewayIP).To4()).WithPrefix()}, stack.AddressProperties{}); err != nil {
		cancel()
		n.stack.Close()
		return nil, errors.New("stack_address_failed")
	}
	if err := n.stack.AddStaticNeighbor(1, ipv4.ProtocolNumber, tcpip.AddrFrom4Slice(net.ParseIP(guestIP).To4()), tcpip.LinkAddress(n.mac)); err != nil {
		cancel()
		n.stack.Close()
		return nil, errors.New("stack_neighbor_failed")
	}
	n.stack.SetSpoofing(1, true)
	n.stack.SetPromiscuousMode(1, true)
	n.stack.SetRouteTable([]tcpip.Route{{Destination: header.IPv4EmptySubnet, NIC: 1}})
	forward := tcp.NewForwarder(n.stack, 1<<20, 512, n.forwardTCP)
	n.stack.SetTransportProtocolHandler(tcp.ProtocolNumber, forward.HandlePacket)
	go func() {
		defer close(n.done)
		for {
			packet := n.link.ReadContext(ctx)
			if packet == nil {
				return
			}
			view := packet.ToView()
			frame := append([]byte(nil), view.AsSlice()...)
			view.Release()
			packet.DecRef()
			if n.send(frame) != nil {
				gate.Close()
			}
		}
	}()
	return n, nil
}
func (n *PacketNetwork) send(frame []byte) error {
	n.sendLock.Lock()
	defer n.sendLock.Unlock()
	return n.emit(frame)
}
func (n *PacketNetwork) Close() {
	n.gate.Close()
	n.cancel()
	n.stack.Close()
	n.link.Close()
	<-n.done
	n.stack.Wait()
}
func (n *PacketNetwork) forwardTCP(request *tcp.ForwarderRequest) {
	id := request.ID()
	address, ok := netip.AddrFromSlice(id.LocalAddress.AsSlice())
	if !ok || !n.gate.Allows("tcp", netip.AddrPortFrom(address, id.LocalPort)) {
		request.Complete(true)
		return
	}
	select {
	case n.slots <- struct{}{}:
	default:
		request.Complete(true)
		return
	}
	go func() {
		defer func() { <-n.slots }()
		remote, err := n.gate.Dial(context.Background(), net.JoinHostPort(address.String(), strconv.Itoa(int(id.LocalPort))))
		if err != nil {
			request.Complete(true)
			return
		}
		defer remote.Close()
		var queue waiter.Queue
		endpoint, tcpErr := request.CreateEndpoint(&queue)
		if tcpErr != nil {
			request.Complete(true)
			return
		}
		request.Complete(false)
		local := gonet.NewTCPConn(&queue, endpoint)
		defer local.Close()
		if tc, ok := remote.(interface{ SetNoDelay(bool) error }); ok {
			_ = tc.SetNoDelay(true)
		}
		done := make(chan struct{})
		go func() {
			defer close(done)
			buf := make([]byte, 128*1024)
			_, err := io.CopyBuffer(remote, local, buf)
			if err != nil {
				remote.Close()
				return
			}
			if half, ok := remote.(interface{ CloseWrite() error }); !ok || half.CloseWrite() != nil {
				remote.Close()
			}
		}()
		rxCopyBuf := make([]byte, 128*1024)
		_, _ = io.CopyBuffer(local, remote, rxCopyBuf)
		local.Close()
		remote.Close()
		<-done
	}()
}

// No shared switch, NAT translation, port-forwarding API or host DNS exists.
// DHCP and node-only DNS replies are synthesized locally without OS sockets.
func (n *PacketNetwork) Input(frame []byte) {
	if len(frame) < 14 || len(frame) > 1514 || !bytes.Equal(frame[6:12], n.mac) {
		return
	}
	typ := binary.BigEndian.Uint16(frame[12:14])
	if typ == uint16(layers.EthernetTypeARP) {
		if len(frame) < 42 || binary.BigEndian.Uint16(frame[20:22]) != 1 || !bytes.Equal(frame[38:42], net.ParseIP(gatewayIP).To4()) {
			return
		}
		// Let gVisor answer ARP for this synthetic gateway only.
	} else if typ == uint16(layers.EthernetTypeIPv4) {
		if len(frame) < 34 || frame[14]>>4 != 4 {
			return
		}
		ihl := int(frame[14]&15) * 4
		if ihl < 20 || len(frame) < 14+ihl {
			return
		}
		// Fragmentation and IP options are unnecessary for this fixed-MTU link.
		if ihl != 20 || binary.BigEndian.Uint16(frame[20:22])&0x3fff != 0 {
			return
		}
		if frame[23] == 17 {
			n.localUDP(frame)
			return
		}
		if frame[23] != 6 || !bytes.Equal(frame[26:30], net.ParseIP(guestIP).To4()) {
			return
		}
	} else {
		return
	} // IPv6, VLAN, multicast protocols and other Ethernet types stay closed.
	packet := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(append([]byte(nil), frame...))})
	defer packet.DecRef()
	n.link.InjectInbound(0, packet)
}
func (n *PacketNetwork) localUDP(frame []byte) {
	packet := gopacket.NewPacket(frame, layers.LayerTypeEthernet, gopacket.DecodeOptions{Lazy: false, NoCopy: true})
	ipLayer, udpLayer := packet.Layer(layers.LayerTypeIPv4), packet.Layer(layers.LayerTypeUDP)
	if ipLayer == nil || udpLayer == nil || packet.ErrorLayer() != nil {
		return
	}
	ip, udp := ipLayer.(*layers.IPv4), udpLayer.(*layers.UDP)
	if udp.SrcPort == 68 && udp.DstPort == 67 {
		n.dhcp(udp.Payload)
		return
	}
	if udp.DstPort == 53 && ip.DstIP.Equal(net.ParseIP(gatewayIP)) && ip.SrcIP.Equal(net.ParseIP(guestIP)) {
		var question dns.Msg
		if len(udp.Payload) > 4096 || question.Unpack(udp.Payload) != nil {
			return
		}
		if n.gate.bootstrapAvailable() && len(question.Question) == 1 && !stringsEqualDNS(question.Question[0].Name, n.gate.policy.NodeHost) {
			select {
			case n.slots <- struct{}{}:
			default:
				return
			}
			go func(query *dns.Msg, port uint16) {
				defer func() { <-n.slots }()
				reply := n.gate.bootstrapDNS(n.ctx, query)
				wire, err := reply.Pack()
				if err == nil && n.gate.bootstrapAvailable() {
					n.replyUDP(53, port, net.ParseIP(guestIP), n.mac, wire)
				}
			}(question.Copy(), uint16(udp.SrcPort))
			return
		}
		reply := n.nodeDNS(&question)
		wire, err := reply.Pack()
		if err == nil {
			n.replyUDP(53, uint16(udp.SrcPort), net.ParseIP(guestIP), n.mac, wire)
		}
	}
}
func (n *PacketNetwork) nodeDNS(query *dns.Msg) *dns.Msg {
	reply := new(dns.Msg)
	reply.SetReply(query)
	reply.RecursionAvailable = false
	if query.Response || query.Opcode != dns.OpcodeQuery || len(query.Question) != 1 {
		reply.Rcode = dns.RcodeRefused
		return reply
	}
	q := query.Question[0]
	if q.Qclass != dns.ClassINET || !stringsEqualDNS(q.Name, n.gate.policy.NodeHost) {
		reply.Rcode = dns.RcodeRefused
		return reply
	}
	if q.Qtype != dns.TypeA && q.Qtype != dns.TypeAAAA {
		reply.Rcode = dns.RcodeRefused
		return reply
	}
	if q.Qtype == dns.TypeAAAA {
		return reply
	}
	for _, ip := range n.gate.policy.EndpointAddresses {
		reply.Answer = append(reply.Answer, &dns.A{Hdr: dns.RR_Header{Name: q.Name, Rrtype: dns.TypeA, Class: dns.ClassINET, Ttl: 30}, A: net.ParseIP(ip).To4()})
	}
	return reply
}
func stringsEqualDNS(a, b string) bool {
	return strings.EqualFold(strings.TrimSuffix(a, "."), strings.TrimSuffix(b, ".")) && b != ""
}
func (n *PacketNetwork) dhcp(payload []byte) {
	if len(payload) > 1500 {
		return
	}
	request, err := dhcpv4.FromBytes(payload)
	if err != nil || !bytes.Equal(request.ClientHWAddr, n.mac) {
		return
	}
	kind := request.MessageType()
	if kind != dhcpv4.MessageTypeDiscover && kind != dhcpv4.MessageTypeRequest {
		return
	}
	offer := dhcpv4.MessageTypeOffer
	if kind == dhcpv4.MessageTypeRequest {
		offer = dhcpv4.MessageTypeAck
	}
	response, err := dhcpv4.NewReplyFromRequest(request, dhcpv4.WithMessageType(offer), dhcpv4.WithYourIP(net.ParseIP(guestIP)), dhcpv4.WithServerIP(net.ParseIP(gatewayIP)), dhcpv4.WithOption(dhcpv4.OptServerIdentifier(net.ParseIP(gatewayIP))), dhcpv4.WithOption(dhcpv4.OptRouter(net.ParseIP(gatewayIP))), dhcpv4.WithOption(dhcpv4.OptDNS(net.ParseIP(gatewayIP))), dhcpv4.WithOption(dhcpv4.OptSubnetMask(net.CIDRMask(24, 32))), dhcpv4.WithOption(dhcpv4.OptIPAddressLeaseTime(time.Hour)))
	if err == nil {
		n.replyUDP(67, 68, net.IPv4bcast, net.HardwareAddr{255, 255, 255, 255, 255, 255}, response.ToBytes())
	}
}
func (n *PacketNetwork) replyUDP(source, target uint16, ip net.IP, mac net.HardwareAddr, payload []byte) {
	eth := &layers.Ethernet{SrcMAC: gatewayMAC, DstMAC: mac, EthernetType: layers.EthernetTypeIPv4}
	network := &layers.IPv4{Version: 4, IHL: 5, TTL: 64, Protocol: layers.IPProtocolUDP, SrcIP: net.ParseIP(gatewayIP).To4(), DstIP: ip.To4()}
	udp := &layers.UDP{SrcPort: layers.UDPPort(source), DstPort: layers.UDPPort(target)}
	udp.SetNetworkLayerForChecksum(network)
	b := gopacket.NewSerializeBuffer()
	if gopacket.SerializeLayers(b, gopacket.SerializeOptions{FixLengths: true, ComputeChecksums: true}, eth, network, udp, gopacket.Payload(payload)) == nil {
		n.send(b.Bytes())
	}
}
