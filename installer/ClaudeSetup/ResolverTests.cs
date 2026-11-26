using System.Net;
using System.Net.Http;

namespace ClaudeSetup;

public static class ResolverTests
{
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;return action(request,token);}}
    private static HttpResponseMessage Good()=>new(HttpStatusCode.OK){Content=new StringContent("{\"Status\":0,\"Answer\":[{\"type\":5,\"data\":\"alias.example\"},{\"type\":1,\"data\":\"8.8.8.8\"},{\"type\":1,\"data\":\"1.1.1.1\"},{\"type\":1,\"data\":\"8.8.8.8\"}]}")};
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("resolver-first-provider-HTTP-failure-uses-same-host-on-backup",async()=> {
            var names=new List<string>();var handler=new Handler((request,_)=>{names.Add(request.RequestUri!.Query);return Task.FromResult(request.RequestUri.Host=="cloudflare-dns.com"?new HttpResponseMessage(HttpStatusCode.ServiceUnavailable):Good());});
            using var client=new HttpClient(handler);var addresses=await NodeEndpointResolver.Resolve("same-host.example",CancellationToken.None,client);
            Assert(handler.Calls==2&&names.All(q=>q.Contains("name=same-host.example"))&&addresses.SequenceEqual(new[]{"1.1.1.1","8.8.8.8"}),"changed selected endpoint or lost fallback");
        });
        await check("resolver-all-failures-are-bounded-and-do-not-expose-host",async()=> {
            var handler=new Handler((request,_)=>Task.FromResult(new HttpResponseMessage(request.RequestUri!.Host=="cloudflare-dns.com"?HttpStatusCode.Forbidden:HttpStatusCode.BadGateway)));
            using var client=new HttpClient(handler);
            try{await NodeEndpointResolver.Resolve("private-fixture.example",CancellationToken.None,client);throw new Exception("Expected DNS failure");}
            catch(SetupException error){Assert(handler.Calls==2&&error.Message.Contains("Cloudflare:HTTP_403")&&error.Message.Contains("Google:HTTP_502")&&!error.Message.Contains("private-fixture"),"provider diagnostics missing or private host escaped");}
        });
        await check("resolver-private-address-cannot-be-bypassed-through-backup",async()=> {
            var handler=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"10.0.0.1\"}]}")}));
            using var client=new HttpClient(handler);
            try{await NodeEndpointResolver.Resolve("fixture.example",CancellationToken.None,client);throw new Exception("Expected private address rejection");}
            catch(SetupException){Assert(handler.Calls==1,"private address bypassed");}
        });
        await check("resolver-IP-literal-does-not-call-any-HTTP-service",async()=> {
            var handler=new Handler((_,_)=>throw new Exception("No HTTP allowed"));using var client=new HttpClient(handler);
            var result=await NodeEndpointResolver.Resolve("1.1.1.1",CancellationToken.None,client);Assert(result.Single()=="1.1.1.1"&&handler.Calls==0,"literal unexpectedly resolved through HTTP");
        });
        await check("resolver-user-cancellation-does-not-start-or-retry-request",async()=> {
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();var handler=new Handler((_,_)=>Task.FromResult(Good()));using var client=new HttpClient(handler);
            try{await NodeEndpointResolver.Resolve("fixture.example",cancellation.Token,client);throw new Exception("Expected cancellation");}catch(OperationCanceledException){Assert(handler.Calls==0,"request made after cancellation");}
        });
        await check("resolver-invalid-JSON-can-use-backup-without-rewriting-node",async()=> {
            var handler=new Handler((request,_)=>Task.FromResult(request.RequestUri!.Host=="cloudflare-dns.com"?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("not-json-private-fixture")}:Good()));using var client=new HttpClient(handler);
            var result=await NodeEndpointResolver.Resolve("fixture.example",CancellationToken.None,client);Assert(result.Length==2&&handler.Calls==2,"invalid provider JSON blocked backup");
        });
        await check("resolver-provider-timeout-is-bounded-and-separate-from-cancel",async()=> {
            var handler=new Handler(async(request,token)=>{if(request.RequestUri!.Host=="cloudflare-dns.com")await Task.Delay(Timeout.Infinite,token);return Good();});using var client=new HttpClient(handler);
            var result=await NodeEndpointResolver.Resolve("fixture.example",CancellationToken.None,client,TimeSpan.FromMilliseconds(30));Assert(result.Length==2&&handler.Calls==2,"provider timeout lost fallback");
        });
        await check("resolver-HTTP-exception-status-is-safe-and-specific",()=> {
            var failure=NodeEndpointResolver.HttpFailure(new HttpRequestException("PRIVATE_FIXTURE",null,HttpStatusCode.Unauthorized));
            Assert(failure=="HTTP_401"&&!failure.Contains("PRIVATE_FIXTURE"),"HTTP exception status hidden or native message escaped");return Task.CompletedTask;
        });
        await check("resolver-malformed-host-is-rejected-before-HTTP",async()=> {
            var handler=new Handler((_,_)=>throw new Exception("No HTTP allowed"));using var client=new HttpClient(handler);
            try{await NodeEndpointResolver.Resolve("bad.example:443",CancellationToken.None,client);throw new Exception("Expected malformed server rejection");}catch(SetupException){Assert(handler.Calls==0,"malformed hostname sent to DNS service");}
        });
        await check("hyperv-resolver-rejects-fakeip-from-system-dns-and-fails-over-to-reachable-direct-doh", async () => {
            var calls = new List<string>();
            var handler = new Handler((request, _) => {
                calls.Add(request.RequestUri!.Host);
                var ip = request.RequestUri.Host == "dns.alidns.com" ? "1.1.1.1" : "8.8.8.8";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"" + ip + "\"}]}")
                });
            });
            using var client = new HttpClient(handler);
            var tcpCalls = new List<string>();
            var report = await NodeEndpointResolver.ResolveDetailedAsync(
                "node.example",
                443,
                EndpointResolutionStrategy.HyperVStandalone,
                CancellationToken.None,
                client,
                TimeSpan.FromMilliseconds(200),
                systemDnsLookup: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.55") }),
                tcpProbe: (addrs, _, _) => {
                    tcpCalls.AddRange(addrs);
                    // AliDNS IP 1.1.1.1 fails TCP -> must fail over to DNSPod 8.8.8.8
                    return Task.FromResult(addrs.Contains("8.8.8.8")
                        ? new TcpReachabilityResult(true, "8.8.8.8", 22, "物理网卡 WLAN")
                        : new TcpReachabilityResult(false, null, null, "宿主出站路径待核对"));
                },
                hostNetworkOverride: new HostNetworkContext(true, false, [], "检测到宿主 HTTP 系统代理"));
            Assert(report.Addresses.SequenceEqual(["8.8.8.8"]) &&
                report.SelectedSource == "直连 DoH (DNSPod)" &&
                calls.Contains("dns.alidns.com") && calls.Contains("doh.pub") &&
                tcpCalls.Contains("1.1.1.1") && tcpCalls.Contains("8.8.8.8") &&
                report.Attempts.Any(a => a.Source == "宿主机系统 DNS" && a.Outcome.Contains("Fake-IP")),
                "HyperVStandalone resolver did not reject Fake-IP and fail over across direct DoH providers");
        });
        await check("hyperv-resolver-prefers-system-dns-when-public-and-reachable", async () => {
            var handler = new Handler((_, _) => throw new Exception("DoH should not be called when system DNS is valid and reachable"));
            using var client = new HttpClient(handler);
            var report = await NodeEndpointResolver.ResolveDetailedAsync(
                "node.example",
                443,
                EndpointResolutionStrategy.HyperVStandalone,
                CancellationToken.None,
                client,
                systemDnsLookup: (_, _) => Task.FromResult(new[] { IPAddress.Parse("9.9.9.9") }),
                tcpProbe: (addrs, _, _) => Task.FromResult(new TcpReachabilityResult(true, addrs[0], 14, "虚拟 TUN/代理接口 Meta")),
                hostNetworkOverride: new HostNetworkContext(false, true, ["Meta"], "检测到宿主 TUN（Meta）"));
            Assert(handler.Calls == 0 && report.Addresses.SequenceEqual(["9.9.9.9"]) &&
                report.SelectedSource == "宿主机系统 DNS" &&
                report.HostNetwork.HostTunnelDetected,
                "Reachable system DNS skipped or TUN context missing");
        });
        await check("resolver-non-object-missing-status-and-truncated-doh-fallback-cleanly", async () => {
            var payloads = new[] { "[]", "\"ok\"", "{\"Answer\":[{\"type\":1,\"data\":\"1.1.1.1\"}]}", "{\"Status\":0,\"TC\":true,\"Answer\":[{\"type\":1,\"data\":\"1.1.1.1\"}]}" };
            foreach (var bad in payloads)
            {
                var handler = new Handler((request, _) =>
                    Task.FromResult(request.RequestUri!.Host == "cloudflare-dns.com"
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bad) }
                        : Good()));
                using var client = new HttpClient(handler);
                var addresses = await NodeEndpointResolver.Resolve("shape-fixture.example", CancellationToken.None, client);
                Assert(handler.Calls == 2 && addresses.SequenceEqual(["1.1.1.1", "8.8.8.8"]), $"Bad DoH shape '{bad}' aborted backup provider");
            }
            var hvCalls = new List<string>();
            var hvHandler = new Handler((request, _) => {
                hvCalls.Add(request.RequestUri!.Host);
                var body = request.RequestUri.Host == "dns.alidns.com" ? "[]" : "{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"9.9.9.9\"}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            });
            using var hvClient = new HttpClient(hvHandler);
            var hvPlan = await NodeEndpointResolver.ResolveCandidateGroupsAsync("shape-hv.example", 443, EndpointResolutionStrategy.HyperVStandalone,
                CancellationToken.None, hvClient,
                systemDnsLookup: (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
                tcpProbe: (a, _, _) => Task.FromResult(new TcpReachabilityResult(true, a[0], 18, "物理网卡 WLAN")),
                stopOnFirstTcpReachable: true);
            Assert(hvCalls.Contains("dns.alidns.com") && hvCalls.Contains("doh.pub") && hvPlan.Groups[0].Addresses.SequenceEqual(["9.9.9.9"]),
                "HyperVStandalone DoH array response [] aborted backup chain");
        });
        await check("hyperv-tcp-ok-but-handshake-failed-advances-to-candidate-b-and-apply-pins-verified-endpoints", async () => {
            var plan = new EndpointCandidatePlan(
                [
                    new("宿主机系统 DNS", ["1.1.1.1"], new TcpReachabilityResult(true, "1.1.1.1", 15, "物理网卡 WLAN")),
                    new("直连 DoH (AliDNS)", ["8.8.8.8"], new TcpReachabilityResult(true, "8.8.8.8", 24, "物理网卡 WLAN"))
                ],
                [],
                new HostNetworkContext(false, false, [], "未检测到宿主 TUN 或 HTTP 系统代理"));
            var tried = new List<string>();
            var probeResult = await NodeExitProbe.ExecuteAcrossCandidatePlanAsync(plan, report => {
                tried.Add(report.Addresses[0]);
                if (report.Addresses[0] == "1.1.1.1")
                    throw new NodeProbeException("TCP 已通，但代理协议握手失败（PROXY_TLS_HANDSHAKE）");
                return Task.FromResult(new NodeProbeResult("203.0.113.10", "KR", "Asia/Seoul", "Korea Standard Time", 90, true,
                    "物理网卡 WLAN", report.Summary, "TCP 24ms · 握手成功", "api.ip.sb", report.HostNetwork.Summary,
                    report.Addresses, report.Addresses));
            }, CancellationToken.None);
            Assert(tried.SequenceEqual(["1.1.1.1", "8.8.8.8"]) &&
                probeResult.VerifiedFirstHopAddresses!.SequenceEqual(["8.8.8.8"]) &&
                probeResult.ResolutionSummary.Contains("协议/出口失败"),
                "Candidate A TCP-ok handshake-fail did not advance to Candidate B");

            // Fixture B: After probe succeeds on 8.8.8.8, DNS changes to 9.9.9.9 -> Apply with pinned endpoints keeps verified 8.8.8.8
            var proxy = new ProxyNode("N", "trojan", "node.example", 443, "{}");
            var resolverCalls = 0;
            var pinned = new Deployment.PinnedRouteEndpoints(probeResult.VerifiedFirstHopAddresses!, probeResult.VerifiedExitAddresses!, probeResult.ResolutionSummary);
            var bound = await Deployment.ResolveOrBindEndpointsAsync(proxy, proxy, false, EndpointResolutionStrategy.HyperVStandalone, pinned, CancellationToken.None,
                resolver: (_, _, _, _) => {
                    resolverCalls++;
                    return Task.FromResult(new EndpointResolutionReport(["9.9.9.9"], "DNS变更", "DNS变更", [], plan.HostNetwork, null));
                });
            Assert(resolverCalls == 0 && bound.FirstHopAddresses.SequenceEqual(["8.8.8.8"]),
                "Apply re-resolved DNS instead of pinning probe-verified endpoints");
        });
        await check("hyperv-deploy-resume-daily-entry-advances-candidate-group-and-distinguishes-connected-endpoint", async () => {
            var opts = new SetupOptions { PlatformId = "hyperv", Root = System.IO.Path.GetTempPath() };
            var finalProxy = new ProxyNode("Exit", "trojan", "exit.example", 443, "{}");
            var upstreamProxy = new ProxyNode("Up", "trojan", "up.example", 443, "{}");
            var plan = new EndpointCandidatePlan(
                [
                    new("宿主机系统 DNS", ["1.1.1.1", "1.0.0.1"], new TcpReachabilityResult(true, "1.1.1.1", 12, "物理网卡 WLAN")),
                    new("直连 DoH (AliDNS)", ["8.8.8.8", "8.8.4.4"], new TcpReachabilityResult(true, "8.8.8.8", 21, "物理网卡 WLAN"))
                ],
                [],
                new HostNetworkContext(true, true, ["Meta Tunnel (TUN)"], "检测到宿主 TUN（Meta Tunnel (TUN)）与 HTTP 系统代理"));
            var tried = new List<string>();
            var bound = await Deployment.ResolveOrBindEndpointsAsync(
                finalProxy, finalProxy, hasUpstream: false,
                EndpointResolutionStrategy.HyperVStandalone, pinnedEndpoints: null, CancellationToken.None,
                options: opts, upstreamNode: null,
                candidateVerifier: (_, _, _, _, ct) => NodeExitProbe.ExecuteAcrossCandidatePlanAsync(plan, report => {
                    tried.Add(string.Join(",", report.Addresses));
                    if (report.Addresses.Contains("1.1.1.1"))
                        throw new NodeProbeException("TCP 已通，但协议握手失败（PROXY_TLS_HANDSHAKE）");
                    return Task.FromResult(new NodeProbeResult("203.0.113.10", "KR", "Asia/Seoul", "Korea Standard Time", 88, true,
                        "物理网卡 WLAN", report.Summary, "TCP 21 ms · 协议握手成功", "api.ip.sb · 88 ms", report.HostNetwork.Summary,
                        report.Addresses, report.Addresses, VerifiedConnectedEndpoint: null, PreProbeEndpoint: "8.8.8.8:443"));
                }, ct));
            Assert(tried.Count == 2 &&
                bound.FirstHopAddresses.SequenceEqual(["8.8.4.4", "8.8.8.8"]) &&
                bound.ExitAddresses.SequenceEqual(["8.8.4.4", "8.8.8.8"]) &&
                bound.Summary.Contains("候选组 2 个") &&
                bound.Summary.Contains("TCP 预探测 8.8.8.8:443") &&
                !bound.Summary.Contains("实连端点 8.8.8.8:443") &&
                bound.Summary.Contains("203.0.113.10/KR") &&
                bound.Summary.Contains("协议/出口失败"),
                "HyperV deploy/resume/daily entry failed to advance candidate group or separate pre-probe IP from actual proxy peer");

            // Double-hop with single-IP bound first hop reports bound connected endpoint accurately
            var doubleHop = await Deployment.ResolveOrBindEndpointsAsync(
                upstreamProxy, finalProxy, hasUpstream: true,
                EndpointResolutionStrategy.HyperVStandalone, pinnedEndpoints: null, CancellationToken.None,
                options: opts, upstreamNode: upstreamProxy,
                candidateVerifier: (_, _, _, _, _) => Task.FromResult(new NodeProbeResult("203.0.113.20", "JP", "Asia/Tokyo", "Tokyo Standard Time", 95, true,
                    "物理网卡 WLAN", "采用 直连 DoH (AliDNS)", "TCP 20 ms · 握手成功", "api.ip.sb · 95 ms", plan.HostNetwork.Summary,
                    ["8.8.8.8"], ["9.9.9.9"], VerifiedConnectedEndpoint: "8.8.8.8:443", PreProbeEndpoint: "8.8.8.8:443")));
            Assert(doubleHop.FirstHopAddresses.SequenceEqual(["8.8.8.8"]) &&
                doubleHop.ExitAddresses.SequenceEqual(["9.9.9.9"]) &&
                doubleHop.Summary.Contains("已绑定实连端点 8.8.8.8:443"),
                "HyperV double-hop failed to preserve distinct first-hop and exit endpoint groups");

            // Private/non-public endpoint returned by candidate verifier must still be rejected by IsolationPolicy
            var rejectedPrivate = false;
            try
            {
                await Deployment.ResolveOrBindEndpointsAsync(
                    finalProxy, finalProxy, hasUpstream: false,
                    EndpointResolutionStrategy.HyperVStandalone, pinnedEndpoints: null, CancellationToken.None,
                    options: opts, upstreamNode: null,
                    candidateVerifier: (_, _, _, _, _) => Task.FromResult(new NodeProbeResult("203.0.113.10", "KR", "Asia/Seoul", "Korea Standard Time", 88, true,
                        "物理网卡 WLAN", "stub", "stub", "stub", "stub", ["127.0.0.1"], ["127.0.0.1"], "127.0.0.1:443")));
            }
            catch (SetupException) { rejectedPrivate = true; }
            Assert(rejectedPrivate, "Private IPv4 candidate bypassed IsolationPolicy.ValidateEndpoints");

            // Standard/macOS default strategy stays on resolver path and does not invoke HyperV candidate verifier
            var defaultVerifierCalls = 0;
            var defaultResolverCalls = 0;
            var defaultBound = await Deployment.ResolveOrBindEndpointsAsync(
                finalProxy, finalProxy, hasUpstream: false,
                EndpointResolutionStrategy.Default, pinnedEndpoints: null, CancellationToken.None,
                options: opts, upstreamNode: null,
                resolver: (_, _, _, _) => {
                    defaultResolverCalls++;
                    return Task.FromResult(new EndpointResolutionReport(["9.9.9.9"], "标准版默认解析", "标准版默认解析", [], plan.HostNetwork, null));
                },
                candidateVerifier: (_, _, _, _, _) => {
                    defaultVerifierCalls++;
                    throw new InvalidOperationException("Default strategy must not use HyperVStandalone verifier");
                });
            Assert(defaultResolverCalls == 1 && defaultVerifierCalls == 0 && defaultBound.FirstHopAddresses.SequenceEqual(["9.9.9.9"]),
                "Default strategy unexpectedly entered HyperVStandalone candidate verification");
        });
        await check("hyperv-single-endpoint-binding-distinguishes-preprobe-from-actual-proxy-peer", async () => {
            // Same candidate group contains two IPs [1.1.1.1, 1.0.0.1]; TCP pre-probe hit 1.1.1.1,
            // but proxy protocol handshake fails on 1.1.1.1 and succeeds ONLY on 1.0.0.1.
            var hostNet = new HostNetworkContext(true, false, ["Meta"], "检测到宿主 TUN（Meta）");
            var sameGroupPlan = new EndpointCandidatePlan(
                [new("宿主机系统 DNS", ["1.1.1.1", "1.0.0.1"], new TcpReachabilityResult(true, "1.1.1.1", 10, "虚拟 TUN/代理接口 Meta"))],
                [],
                hostNet);
            var expanded = NodeExitProbe.ExpandSingleEndpointPlan(sameGroupPlan);
            Assert(expanded.Groups.Length == 2 &&
                expanded.Groups[0].Addresses.SequenceEqual(["1.1.1.1"]) &&
                expanded.Groups[1].Addresses.SequenceEqual(["1.0.0.1"]),
                "ExpandSingleEndpointPlan did not split same-group IPs into single-IP bound candidates in pre-probe-first order");

            var triedSingleIps = new List<string>();
            var proxy = new ProxyNode("SameGroup", "trojan", "node.example", 443, "{}");
            var opts = new SetupOptions { PlatformId = "hyperv", Root = System.IO.Path.GetTempPath() };
            var bound = await Deployment.ResolveOrBindEndpointsAsync(
                proxy, proxy, hasUpstream: false,
                EndpointResolutionStrategy.HyperVStandalone, pinnedEndpoints: null, CancellationToken.None,
                options: opts, upstreamNode: null,
                candidateVerifier: (_, _, _, _, ct) => NodeExitProbe.ExecuteAcrossCandidatePlanAsync(expanded, report => {
                    Assert(report.Addresses.Length == 1, "Single-IP expanded candidate must bind exactly one IP");
                    var boundIp = report.Addresses[0];
                    triedSingleIps.Add(boundIp);
                    if (boundIp == "1.1.1.1")
                        throw new NodeProbeException("TCP 预探测命中 1.1.1.1，但代理协议握手失败");
                    return Task.FromResult(new NodeProbeResult("203.0.113.55", "SG", "Asia/Singapore", "Singapore Standard Time", 66, true,
                        "物理网卡 WLAN", report.Summary, $"单点绑定代理对端 {boundIp}:443", "api.ip.sb · 66 ms", hostNet.Summary,
                        report.Addresses, report.Addresses,
                        VerifiedConnectedEndpoint: $"{boundIp}:443",
                        PreProbeEndpoint: $"{report.FirstHopReachability!.ConnectedIp}:443"));
                }, ct));
            Assert(triedSingleIps.SequenceEqual(["1.1.1.1", "1.0.0.1"]) &&
                bound.FirstHopAddresses.SequenceEqual(["1.0.0.1"]) &&
                bound.Summary.Contains("已绑定实连端点 1.0.0.1:443") &&
                bound.Summary.Contains("TCP 预探测 1.1.1.1:443") &&
                !bound.Summary.Contains("已绑定实连端点 1.1.1.1:443"),
                "Same-group two-IP sample misattributed TCP pre-probe IP 1.1.1.1 as the actual proxy connected endpoint");

            // Even if a multi-IP PinnedRouteEndpoints passes a pre-probe ConnectedEndpoint, it MUST NOT be labeled as bound proxy peer.
            var multiPinned = new Deployment.PinnedRouteEndpoints(["1.1.1.1", "1.0.0.1"], ["1.1.1.1", "1.0.0.1"], "多IP样本",
                ConnectedEndpoint: "1.1.1.1:443", PreProbeEndpoint: "1.1.1.1:443");
            var multiBound = await Deployment.ResolveOrBindEndpointsAsync(
                proxy, proxy, hasUpstream: false,
                EndpointResolutionStrategy.HyperVStandalone, multiPinned, CancellationToken.None);
            Assert(!multiBound.Summary.Contains("已绑定实连端点") && multiBound.Summary.Contains("TCP 预探测 1.1.1.1:443"),
                "Multi-IP pinned group falsely labeled pre-probe IP as bound connected endpoint");
        });
        await check("hyperv-geo-api-429-timeout-degrades-to-pending-while-real-protocol-failure-blocks-and-active-binding-reused", async () => {
            Uri[] geoUrls = [new("https://geo-a.example/ip"), new("https://geo-b.example/ip"), new("https://geo-c.example/ip")];
            Uri[] protoUrls = [new("https://proto.example/generate_204")];

            // Case 1: All 3rd-party geo endpoints return HTTP 429 -> allowUnverifiedExit=true succeeds as ExitPendingVerification
            // while allowUnverifiedExit=false (interactive UI lookup) still throws NodeProbeException.
            using var client429 = new HttpClient(new StubHandler(req => Task.FromResult(
                new HttpResponseMessage(req.RequestUri!.Host == "proto.example" ? HttpStatusCode.NoContent : HttpStatusCode.TooManyRequests))));
            var res429 = await NodeExitProbe.ProbeEndpoints(client429, geoUrls, new NodeProbeCooldown(), CancellationToken.None,
                allowUnverifiedExit: true, protocolProbeEndpoints: protoUrls, requestTimeout: TimeSpan.FromSeconds(2));
            Assert(!res429.ExitVerified && res429.ExitIp == "" &&
                res429.TargetSummary.Contains("出口待核验") && (res429.ExitPendingReason?.Contains("429") == true),
                "All-429 geo API failure blocked recovery instead of degrading to pending exit verification");
            var strict429Threw = false;
            try
            {
                await NodeExitProbe.ProbeEndpoints(client429, geoUrls, new NodeProbeCooldown(), CancellationToken.None,
                    allowUnverifiedExit: false, protocolProbeEndpoints: protoUrls, requestTimeout: TimeSpan.FromSeconds(2));
            }
            catch (NodeProbeException) { strict429Threw = true; }
            Assert(strict429Threw, "Interactive strict geo probe should still throw NodeProbeException on all-429");

            // Case 2: All 3rd-party geo endpoints time out, but non-geo protocol probe succeeds -> degrades to ExitPendingVerification
            // and ResolveOrBindEndpointsAsync preserves validated public IP + reports 出口待核验.
            using var clientGeoTimeout = new HttpClient(new StubHandler(async (req, ct) => {
                if (req.RequestUri!.Host == "proto.example")
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            var resTimeout = await NodeExitProbe.ProbeEndpoints(clientGeoTimeout, geoUrls, new NodeProbeCooldown(), CancellationToken.None,
                allowUnverifiedExit: true, protocolProbeEndpoints: protoUrls, requestTimeout: TimeSpan.FromMilliseconds(100));
            Assert(!resTimeout.ExitVerified && resTimeout.TargetSummary.Contains("出口待核验") &&
                (resTimeout.ExitPendingReason?.Contains("超时") == true),
                "Geo target timeout with healthy proxy protocol failed to degrade to pending exit verification");

            var proxyNode = NetworkImport.Parse("socks5://node.example:1080#NodeA").Nodes.Single();
            var opts = new SetupOptions { PlatformId = "hyperv", Root = System.IO.Path.GetTempPath() };
            var boundPending = await Deployment.ResolveOrBindEndpointsAsync(
                proxyNode, proxyNode, hasUpstream: false,
                EndpointResolutionStrategy.HyperVStandalone, pinnedEndpoints: null, CancellationToken.None,
                options: opts, upstreamNode: null,
                candidateVerifier: (_, _, _, _, _) => Task.FromResult(resTimeout with
                {
                    ResolutionSummary = "候选组 1/1（直连 DoH · 1 个公网 IPv4）",
                    HostNetworkSummary = "未检测到宿主 TUN",
                    VerifiedFirstHopAddresses = ["8.8.8.8"],
                    VerifiedExitAddresses = ["8.8.8.8"],
                    VerifiedConnectedEndpoint = "8.8.8.8:1080",
                    PreProbeEndpoint = "8.8.8.8:1080"
                }));
            Assert(boundPending.FirstHopAddresses.SequenceEqual(["8.8.8.8"]) &&
                boundPending.Summary.Contains("已绑定实连端点 8.8.8.8:1080") &&
                boundPending.Summary.Contains("出口待核验"),
                "ResolveOrBindEndpointsAsync did not preserve bound endpoint and pending-exit label when geo APIs unavailable");

            // Case 3: Real node protocol failure (both geo and protocol probe fail transport/timeout) MUST still block!
            using var clientProtoFail = new HttpClient(new StubHandler(_ => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("proxy TLS handshake failed"))));
            var realFailureBlocked = false;
            try
            {
                await NodeExitProbe.ProbeEndpoints(clientProtoFail, geoUrls, new NodeProbeCooldown(), CancellationToken.None,
                    allowUnverifiedExit: true, protocolProbeEndpoints: protoUrls, requestTimeout: TimeSpan.FromMilliseconds(200));
            }
            catch (NodeProbeException) { realFailureBlocked = true; }
            Assert(realFailureBlocked, "Real node protocol failure was not blocked when allowUnverifiedExit=true");

            // Case 4: Existing task with active trusted binding matching saved CoreConfigSha256 reuses it directly
            // without calling external geo APIs, while route change returns null and private IP still blocks.
            var activeConfig = NetworkImport.CoreConfig(proxyNode, ["8.8.8.8"], "ClaudomeBridge", null, ["8.8.8.8"]);
            var activeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(activeConfig))).ToLowerInvariant();
            var st = new SessionState { VmId = Guid.NewGuid().ToString("D"), CoreConfigSha256 = activeHash, Options = opts };
            var reused = Deployment.TryReuseActiveTrustedBinding(st, proxyNode, null, ["8.8.8.8"], ["8.8.8.8"]);
            Assert(reused is { } hit && hit.FirstHopAddresses.SequenceEqual(["8.8.8.8"]) && hit.Summary.Contains("优先复核当前受控连接"),
                "Existing task did not reuse matching active trusted binding");
            var otherNode = NetworkImport.Parse("socks5://other.example:1080#NodeB").Nodes.Single();
            Assert(Deployment.TryReuseActiveTrustedBinding(st, otherNode, null, ["8.8.8.8"], ["8.8.8.8"]) is null,
                "Changed route unexpectedly reused previous task's active binding");
            var privateBlocked = false;
            try { _ = Deployment.TryReuseActiveTrustedBinding(st, proxyNode, null, ["127.0.0.1"], ["127.0.0.1"]); }
            catch (SetupException) { privateBlocked = true; }
            Assert(privateBlocked, "Private IP in active binding bypassed IsolationPolicy");
        });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback;
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) => this.callback = (r, _) => callback(r);
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) => this.callback = callback;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
