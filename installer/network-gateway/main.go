package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"os/signal"
	"sync"
	"time"
)

type command struct {
	Operation   string `json:"operation"`
	Fingerprint string `json:"fingerprint"`
	Seconds     int    `json:"seconds"`
}
type ready struct {
	Ready       bool   `json:"ready"`
	VMID        string `json:"vmId"`
	RunID       string `json:"runId"`
	Fingerprint string `json:"fingerprint"`
	UDPPort     int    `json:"udpPort"`
	Version     int    `json:"version"`
}

func main() {
	if err := run(os.Stdin, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, "gateway_stopped")
		os.Exit(1)
	}
}
func run(input io.Reader, output io.Writer) error {
	scanner := bufio.NewScanner(input)
	scanner.Buffer(make([]byte, 4096), 8192)
	if !scanner.Scan() {
		return errors.New("policy_missing")
	}
	var p Policy
	if err := decodeStrict(scanner.Bytes(), &p); err != nil {
		return err
	}
	dialer := &net.Dialer{Timeout: 10 * time.Second, KeepAlive: 15 * time.Second}
	gate, err := newGate(p, hostNetworks, dialer.DialContext)
	if err != nil {
		return err
	}
	defer gate.Close()
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: p.ListenPort})
	if err != nil {
		return errors.New("gateway_bind_failed")
	}
	_ = conn.SetReadBuffer(4 << 20)
	_ = conn.SetWriteBuffer(4 << 20)
	defer conn.Close()
	peer := &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: p.GuestPort}
	network, err := newPacketNetwork(gate, func(frame []byte) error { _, err := conn.WriteToUDP(frame, peer); return err })
	if err != nil {
		return err
	}
	defer network.Close()
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	var write sync.Mutex
	reply := func(value any) error {
		write.Lock()
		defer write.Unlock()
		return json.NewEncoder(output).Encode(value)
	}
	if err := reply(ready{Ready: true, VMID: p.VMID, RunID: p.RunID, Fingerprint: p.Fingerprint(), UDPPort: conn.LocalAddr().(*net.UDPAddr).Port, Version: 1}); err != nil {
		return err
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		buffer := make([]byte, 65536)
		for {
			n, remote, err := conn.ReadFromUDP(buffer)
			if err != nil {
				return
			}
			if remote.Port == peer.Port && remote.IP.IsLoopback() {
				network.Input(buffer[:n])
			}
		}
	}()
	commands := make(chan []byte)
	go func() {
		defer close(commands)
		for scanner.Scan() {
			line := append([]byte(nil), scanner.Bytes()...)
			select {
			case commands <- line:
			case <-ctx.Done():
				return
			}
		}
	}()
	monitor := time.NewTicker(time.Second)
	defer monitor.Stop()
	defer func() { cancel(); conn.Close(); <-done }()
	for {
		select {
		case <-ctx.Done():
			return nil
		case line, ok := <-commands:
			if !ok {
				return nil
			}
			var cmd command
			if decodeStrict(line, &cmd) != nil {
				gate.Close()
				return errors.New("invalid_control")
			}
			switch cmd.Operation {
			case "renew":
				err = gate.Renew(cmd.Fingerprint, cmd.Seconds)
				if err != nil {
					gate.Close()
				}
			case "probe-bootstrap-dns", "probe-bootstrap-http":
				if cmd.Fingerprint != p.Fingerprint() {
					err = errClosed
				} else {
					probeContext, probeCancel := context.WithTimeout(ctx, 8*time.Second)
					if cmd.Operation == "probe-bootstrap-dns" {
						err = gate.ProbeBootstrapDNS(probeContext)
					} else {
						err = gate.ProbeBootstrapHTTP(probeContext)
					}
					probeCancel()
				}
			case "probe-bootstrap":
				if cmd.Fingerprint != p.Fingerprint() {
					err = errClosed
				} else {
					probeContext, probeCancel := context.WithTimeout(ctx, 10*time.Second)
					err = gate.ProbeBootstrap(probeContext)
					probeCancel()
				}
			case "finish-bootstrap":
				if cmd.Fingerprint != p.Fingerprint() {
					err = errClosed
				} else {
					gate.FinishBootstrap()
					err = nil
				}
			case "close":
				gate.Close()
				err = nil
			case "stop":
				return nil
			default:
				gate.Close()
				return errors.New("invalid_control")
			}
			if e := reply(map[string]any{"ok": err == nil, "vmId": p.VMID, "runId": p.RunID, "fingerprint": p.Fingerprint(), "operation": cmd.Operation, "code": controlErrorCode(err)}); e != nil {
				return e
			}
		case <-monitor.C:
			// A topology becoming unsafe revokes long-lived streams, too.
			gate.mu.Lock()
			safe := true
			for _, raw := range p.EndpointAddresses {
				a, _ := netip.ParseAddr(raw)
				if !gate.permitsLocked("tcp", netip.AddrPortFrom(a, p.EndpointPort)) {
					safe = false
					break
				}
			}
			gate.mu.Unlock()
			if !safe {
				gate.Close()
			}
		}
	}
}
func decodeStrict(data []byte, value any) error {
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(value); err != nil {
		return errors.New("invalid_control")
	}
	if decoder.Decode(new(any)) != io.EOF {
		return errors.New("invalid_control")
	}
	return nil
}
