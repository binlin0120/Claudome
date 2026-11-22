using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Claudome.Transport;

namespace ClaudeSetup;

public static class TransportTests
{
    private static void Assert(bool value,string message) {if(!value)throw new Exception(message);}
    private static BridgeSpec Spec(string platform="hyperv") => new(platform,"11111111-1111-1111-1111-111111111111","aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","22222222-2222-2222-2222-222222222222",200000,"02:11:22:33:44:55",Convert.ToBase64String(Enumerable.Range(0,32).Select(x=>(byte)x).ToArray()));
    private static byte[] Packet()
    {
        var bytes=new byte[20];bytes[0]=0x45;bytes[3]=20;bytes[8]=64;bytes[9]=17;
        new byte[]{198,19,250,10}.CopyTo(bytes,12);new byte[]{1,1,1,1}.CopyTo(bytes,16);return bytes;
    }
    private static async Task<bool> Authenticate(BridgeSpec serverSpec,BridgeSpec clientSpec)
    {
        var name="Claudome-protocol-fixture-"+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token),client.ConnectAsync(deadline.Token));
        var one=BridgeProtocol.AuthenticateServer(server,serverSpec,deadline.Token);var two=BridgeProtocol.AuthenticateClient(client,clientSpec,deadline.Token);
        var first=await Task.WhenAny(one,two);if(first.IsFaulted||first.IsCanceled){server.Dispose();client.Dispose();}
        try{await Task.WhenAll(one,two);return true;}catch(Exception ex) when(ex is IOException or InvalidDataException){return false;}
    }
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        foreach(var platform in new[]{"hyperv","workstation"}) await check("bridge-mutual-authentication-"+platform,async()=>Assert(await Authenticate(Spec(platform),Spec(platform)),"matching identity failed"));
        await check("bridge-rejects-wrong-vm-identity",async()=>Assert(!await Authenticate(Spec(),Spec() with{VmId="33333333-3333-3333-3333-333333333333"}),"other VM authenticated"));
        await check("bridge-rejects-wrong-workspace",async()=>Assert(!await Authenticate(Spec(),Spec() with{WorkspaceId="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}),"other workspace authenticated"));
        await check("bridge-rejects-wrong-key",async()=>Assert(!await Authenticate(Spec(),Spec() with{Key=Convert.ToBase64String(new byte[32])}),"invalid key authenticated"));
        await check("bridge-rejects-cross-provider",async()=>Assert(!await Authenticate(Spec(),Spec("workstation")),"other provider authenticated"));
        await check("hypervisor-socket-layout-roundtrip",()=>{
            var spec=Spec();var hv=new HyperVEndPoint(Guid.Parse(spec.VmId),Guid.Parse(spec.ServiceId));var data=hv.Serialize();var decoded=(HyperVEndPoint)hv.Create(data);
            Assert(data.Size==36 && (int)data.Family==34 && decoded.VmId==hv.VmId && decoded.ServiceId==hv.ServiceId,"Hyper-V layout corrupted");
            var vmci=new VmciEndPoint(28,2,200000);data=vmci.Serialize();var v=(VmciEndPoint)vmci.Create(data);Assert(data.Size==16&&v.ContextId==2&&v.Port==200000,"VMCI context/port byte order corrupted");return Task.CompletedTask;
        });
        await check("hyperv-host-listens-for-children-and-validates-kernel-peer-vm-id", () =>
        {
            var spec = Spec();
            var expected = Guid.Parse(spec.VmId);
            var service = Guid.Parse(spec.ServiceId);
            var listener = HypervisorSockets.HyperVHostListenEndpoint(service);
            Assert(listener.VmId == Guid.Parse("90db8b89-0d35-4f79-8ce9-49ea0ac8b7cd") && listener.ServiceId == service,
                "Hyper-V host listener did not use the children address and task service");
            byte[] Address(Guid id)
            {
                var endpoint = new HyperVEndPoint(id, service).Serialize();
                return Enumerable.Range(0, endpoint.Size).Select(i => endpoint[i]).ToArray();
            }
            var peer = Address(expected);
            Assert(HypervisorSockets.IsExpectedHyperVPeer(peer, expected), "Target VM was denied");
            Assert(!HypervisorSockets.IsExpectedHyperVPeer(Address(Guid.NewGuid()), expected) &&
                !HypervisorSockets.IsExpectedHyperVPeer(Address(Guid.Empty), expected) &&
                !HypervisorSockets.IsExpectedHyperVPeer(Address(listener.VmId), expected) &&
                !HypervisorSockets.IsExpectedHyperVPeer(peer.AsSpan(0, 20), expected),
                "A foreign, wildcard or malformed peer was accepted");
            peer[0] = (byte)AddressFamily.InterNetwork;
            Assert(!HypervisorSockets.IsExpectedHyperVPeer(peer, expected), "Peer from an unexpected address family was accepted");
            return Task.CompletedTask;
        });
        await check("bridge-bounded-packet-framing-and-source",async()=>{
            var packet=Packet();using var stream=new MemoryStream();await BridgeProtocol.WritePacket(stream,packet,CancellationToken.None);stream.Position=0;
            Assert((await BridgeProtocol.ReadPacket(stream,CancellationToken.None)).SequenceEqual(packet),"packet framing changed payload");
            using var oversized=new MemoryStream(new byte[]{0xff,0xff});var rejected=false;try{await BridgeProtocol.ReadPacket(oversized,CancellationToken.None);}catch(InvalidDataException){rejected=true;}Assert(rejected,"unbounded packet allocation accepted");
            packet[12]=10;rejected=false;try{BridgeProtocol.Ethernet(packet,Spec().GuestMac);}catch(InvalidDataException){rejected=true;}Assert(rejected,"spoofed guest source accepted");
        });
        await check("vmware-vmx-no-nics-no-sharing-and-unicode-roundtrip",()=>{
            var spec=Spec("workstation");var settings=WorkstationBoundary.Required(spec);settings["displayName"]="测试 | \"环境\"";settings["encryption.keySafe"]="vmware:key/list/(fixture|state)";
            var file=Path.Combine(root,"vmware-fixture.vmx");WorkstationBoundary.Write(file,settings);var parsed=WorkstationBoundary.Parse(File.ReadAllText(file));WorkstationBoundary.Check(parsed,spec);
            Assert(parsed["displayName"]==settings["displayName"]&&parsed["encryption.keySafe"]==settings["encryption.keySafe"],"VMX rewrite corrupted unrelated fields");
            Assert(WorkstationBoundary.Parse("name = \"|e6|b5|8b|e8|af|95\"\n")["name"]=="测试","encoded UTF-8 VMX text lost");
            parsed["ethernet9.present"]="TRUE";var rejected=false;try{WorkstationBoundary.Check(parsed,spec);}catch(SetupException){rejected=true;}Assert(rejected,"extra NIC was allowed");return Task.CompletedTask;
        });
        await check("guest-evidence-rejects-stale-or-other-environment-reports",()=>{
            var state=new SessionState{VmId=Spec().VmId,Options=new SetupOptions{WorkspaceId=Spec().WorkspaceId}};var runId=Guid.NewGuid().ToString("N");
            System.Text.Json.JsonDocument Evidence(string run,string vm,string workspace,int schema=2)=>System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new{schema,runId=run,vmId=vm,workspaceId=workspace}));
            using(var current=Evidence(runId,state.VmId,state.Options.WorkspaceId))GuestEvidence.RequireCurrent(current.RootElement,state,runId);
            foreach(var invalid in new[]{Evidence("old-run",state.VmId,state.Options.WorkspaceId),Evidence(runId,Guid.NewGuid().ToString(),state.Options.WorkspaceId),Evidence(runId,state.VmId,"other-workspace"),Evidence(runId,state.VmId,state.Options.WorkspaceId,1)}){
                using(invalid){var rejected=false;try{GuestEvidence.RequireCurrent(invalid.RootElement,state,runId);}catch(SetupException){rejected=true;}Assert(rejected,"stale or foreign report accepted");}
            }return Task.CompletedTask;
        });
        await check("guest-config-report-retries-transfer-and-uses-current-result-over-exit-code", async () =>
        {
            var state = new SessionState { VmId = Spec().VmId, Options = new() { Root = Path.Combine(root, "fresh-guest-report"), WorkspaceId = Spec().WorkspaceId } };
            Directory.CreateDirectory(state.Options.Private);
            var runId = Guid.NewGuid().ToString("N");
            var stored = Paths.Under(state.Options.Private, "guest-result.json");
            File.WriteAllText(stored, "previous fixture");
            var attempts = 0;
            using var result = await GuestEvidence.CollectConfiguration(state, runId, new SetupException("fixture launcher exit"), (file, _) =>
            {
                Assert(file != stored && File.ReadAllText(stored) == "previous fixture", "a transfer overwrote the previous report");
                if (++attempts == 1)
                {
                    File.WriteAllText(file, "{partial");
                    throw new SetupException("fixture transfer interruption");
                }
                Assert(!File.Exists(file), "partial transfer reused");
                File.WriteAllText(file, JsonSerializer.Serialize(new
                {
                    schema = 2, runId, vmId = state.VmId, workspaceId = state.Options.WorkspaceId, code = "GUEST_ELEVATION_CANCELLED"
                }));
                return Task.CompletedTask;
            }, TimeSpan.Zero);
            Assert(attempts == 2 && result.RootElement.GetProperty("code").GetString() == "GUEST_ELEVATION_CANCELLED" &&
                File.ReadAllText(stored).Contains(runId), "current failure report or retry was lost");
        });
        await check("guest-config-report-missing-or-stale-keeps-the-previous-report-and-diagnostics", async () =>
        {
            var state = new SessionState { VmId = Spec().VmId, Options = new() { Root = Path.Combine(root, "stale-guest-report"), WorkspaceId = Spec().WorkspaceId } };
            Directory.CreateDirectory(state.Options.Private);
            var stored = Paths.Under(state.Options.Private, "guest-result.json");
            File.WriteAllText(stored, "previous fixture");
            var runId = Guid.NewGuid().ToString("N");
            try
            {
                using var ignored = await GuestEvidence.CollectConfiguration(state, runId, new SetupException("fixture launcher cause"),
                    (_, _) => throw new SetupException("fixture file copy failed"), TimeSpan.Zero);
                throw new Exception("missing result accepted");
            }
            catch (SetupException ex)
            {
                Assert(ex.Message.Contains("GUEST_REPORT_UNAVAILABLE") && ex.Message.Contains("fixture launcher cause"),
                    "sanitized launch context was discarded");
            }
            try
            {
                using var ignored = await GuestEvidence.CollectConfiguration(state, runId, null, (file, _) =>
                {
                    File.WriteAllText(file, JsonSerializer.Serialize(new
                    {
                        schema = 2, runId = "previous-run", vmId = state.VmId, workspaceId = state.Options.WorkspaceId
                    }));
                    return Task.CompletedTask;
                }, TimeSpan.Zero);
                throw new Exception("stale result accepted");
            }
            catch (SetupException ex) { Assert(ex.Message.Contains("旧结果"), "wrong stale report explanation"); }
            Assert(File.ReadAllText(stored) == "previous fixture" && !Directory.GetFiles(state.Options.Private, "*.incoming").Any(),
                "a failed read removed the last valid result or left a partial incoming file");
        });
        await check("guest-input-unreadable-reports-diagnostic-without-replacing-accepted-evidence", async () =>
        {
            var state = new SessionState { VmId = Spec().VmId, Options = new() { Root = Path.Combine(root, "guest-input-unreadable"), WorkspaceId = Spec().WorkspaceId } };
            Directory.CreateDirectory(state.Options.Private);
            var stored = Paths.Under(state.Options.Private, "guest-result.json");
            File.WriteAllText(stored, "accepted fixture");
            var runId = Guid.NewGuid().ToString("N");
            foreach (var currentRun in new[] { true, false })
            {
                try
                {
                    using var ignored = await GuestEvidence.CollectConfiguration(state, runId, null, (file, _) =>
                    {
                        File.WriteAllText(file, JsonSerializer.Serialize(new
                        {
                            schema = 2, runId = currentRun ? runId : "previous-run", vmId = "", workspaceId = "",
                            code = "GUEST_INPUT_UNREADABLE", arbitrary = "synthetic-private-detail"
                        }));
                        return Task.CompletedTask;
                    }, TimeSpan.Zero);
                    throw new Exception("a diagnostic report was accepted as task evidence");
                }
                catch (SetupException ex)
                {
                    Assert(ex.Message.Contains("GUEST_INPUT_UNREADABLE") == currentRun, "diagnostic was lost or applied to an old run");
                    Assert(!ex.Message.Contains("synthetic-private-detail"), "arbitrary guest contents entered the public diagnostic");
                }
            }
            Assert(File.ReadAllText(stored) == "accepted fixture" && !Directory.GetFiles(state.Options.Private, "*.incoming").Any(),
                "a diagnostic replaced the accepted result or left a partial incoming file");
        });
        await check("vmware-security-is-read-back-not-confirmation-only",()=>{
            var settings=WorkstationBoundary.Required(Spec("workstation"));settings["firmware"]="efi";settings["uefi.secureBoot.enabled"]="TRUE";
            Assert(!WorkstationBoundary.SecurityReady(settings),"missing encryption/TPM passed");settings["vtpm.present"]="TRUE";Assert(!WorkstationBoundary.SecurityReady(settings),"unencrypted TPM passed");
            settings["encryption.keySafe"]="fixture-only";Assert(WorkstationBoundary.SecurityReady(settings),"complete settings rejected");settings["uefi.secureBoot.enabled"]="FALSE";Assert(!WorkstationBoundary.SecurityReady(settings),"disabled secure boot passed");return Task.CompletedTask;
        });
        await check("vmware-offline-still-rejects-clipboard-and-usb",()=>{
            foreach(var setting in new[]{"isolation.tools.copy.disable","usb.present","isolation.tools.hgfs.disable"}) {
                var values=WorkstationBoundary.Required(Spec("workstation")); values[setting]=setting=="usb.present"?"TRUE":"FALSE";
                var rejected=false;try{WorkstationBoundary.CheckOffline(values);}catch(SetupException){rejected=true;}Assert(rejected,"offline bypass accepted: "+setting);
            }return Task.CompletedTask;
        });
        await check("host-runtime-reuse-verifies-existing-files-without-overwrite",()=>{
            var archive=Path.Combine(root,"host-runtime-fixture.zip");var destination=Path.Combine(root,"host-runtime-fixture");
            using(var zip=System.IO.Compression.ZipFile.Open(archive,System.IO.Compression.ZipArchiveMode.Create)){using var text=new StreamWriter(zip.CreateEntry("nested/fixture.dll").Open());text.Write("fixture bytes");}
            HostRuntime.ExtractVerified(archive,destination);var file=Path.Combine(destination,"nested/fixture.dll");using(var held=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read))HostRuntime.ExtractVerified(archive,destination);
            File.WriteAllText(file,"tampered");var rejected=false;try{HostRuntime.ExtractVerified(archive,destination);}catch(SetupException){rejected=true;}Assert(rejected&&File.ReadAllText(file)=="tampered","changed loaded files were overwritten or accepted");return Task.CompletedTask;
        });
        await check("bridge-return-packets-reject-other-guest-and-source",()=>{
            var packet=Packet();new byte[]{1,1,1,1}.CopyTo(packet,12);new byte[]{198,19,250,10}.CopyTo(packet,16);
            var frame=new byte[34];Convert.FromHexString(Spec().GuestMac.Replace(":","")).CopyTo(frame,0);new byte[]{2,0x43,0x45,0,0,1}.CopyTo(frame,6);frame[12]=8;packet.CopyTo(frame,14);
            Assert(BridgeProtocol.IPv4FromEthernet(frame,Spec().GuestMac).SequenceEqual(packet),"valid return rejected");
            foreach(var index in new[]{6,33}) {var invalid=frame.ToArray();invalid[index]^=1;var rejected=false;try{BridgeProtocol.IPv4FromEthernet(invalid,Spec().GuestMac);}catch(InvalidDataException){rejected=true;}Assert(rejected,"wrong return identity accepted");}return Task.CompletedTask;
        });
        await check("vmware-vmx-duplicate-settings-rejected",()=>{
            var rejected=false;try{WorkstationBoundary.Parse("ethernet0.present = \"FALSE\"\nethernet0.present = \"TRUE\"\n");}catch(SetupException){rejected=true;}Assert(rejected,"duplicate NIC setting accepted");return Task.CompletedTask;
        });
        await check("bus-clash-physical-interface-is-explicit",()=>{
            var node=NetworkImport.Parse("socks5://node.example:1080#Fixture").Nodes.Single();using var config=System.Text.Json.JsonDocument.Parse(NetworkImport.CoreConfig(node,["1.1.1.1"],"ClaudomeBridge"));
            Assert(config.RootElement.GetProperty("interface-name").GetString()=="ClaudomeBridge"&&!config.RootElement.GetProperty("tun").GetProperty("auto-detect-interface").GetBoolean(),"Clash can bind back to its own TUN");return Task.CompletedTask;
        });
        await check("bus-relay-loopback-conflict-reports-stage-and-winsock-code", () =>
        {
            var spec = Spec();
            using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ExclusiveAddressUse = true
            };
            reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
            var binding = new GatewayBinding(spec.VmId, port, port + 1, spec.GuestMac);
            var diagnosed = false;
            try { _ = new BusPacketRelay(spec, binding); }
            catch (SetupException error)
            {
                diagnosed = error.Message.Contains("宿主回环 UDP") &&
                    (error.Message.Contains("SOCKET_10048") || error.Message.Contains("SOCKET_10013")) &&
                    !error.Message.Contains(port.ToString());
            }
            Assert(diagnosed, "Loopback socket collision did not report safe stage and Winsock code");
            var native = new SocketException((int)SocketError.AddressFamilyNotSupported);
            Assert(Redaction.SafeError(native).Contains("SOCKET_10047"), "Fallback hid the Winsock status");
            return Task.CompletedTask;
        });
        await check("bus-packet-relay-authenticates-and-carries-only-framed-ipv4",async()=>{
            using var node=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));using var reservation=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            var binding=new GatewayBinding(Spec().VmId,((IPEndPoint)reservation.Client.LocalEndPoint!).Port,((IPEndPoint)node.Client.LocalEndPoint!).Port,Spec().GuestMac);reservation.Dispose();
            var listener=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);listener.Bind(new IPEndPoint(IPAddress.Loopback,0));listener.Listen(1);var target=(IPEndPoint)listener.LocalEndPoint!;
            await using var relay=new BusPacketRelay(Spec(),binding,listener);using var client=new TcpClient();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));await client.ConnectAsync(target,deadline.Token);using var stream=client.GetStream();
            Assert(!HypervisorSockets.IsExpectedHyperVPeer(client.Client, Guid.Parse(Spec().VmId)),
                "WinSock TCP peer was admitted to the Hyper-V identity check");
            await BridgeProtocol.AuthenticateClient(stream,Spec(),deadline.Token);await BridgeProtocol.WritePacket(stream,Packet(),deadline.Token);
            var frame=await node.ReceiveAsync(deadline.Token);Assert(frame.RemoteEndPoint.Port==binding.GuestPort&&frame.Buffer.AsSpan(14).SequenceEqual(Packet()),"guest packet escaped selected internal relay");
            var response=Packet();new byte[]{1,1,1,1}.CopyTo(response,12);new byte[]{198,19,250,10}.CopyTo(response,16);
            var ethernet=new byte[14+response.Length];Convert.FromHexString(binding.GuestMac.Replace(":","")).CopyTo(ethernet,0);new byte[]{2,0x43,0x45,0,0,1}.CopyTo(ethernet,6);ethernet[12]=8;response.CopyTo(ethernet,14);
            await node.SendAsync(ethernet,frame.RemoteEndPoint,deadline.Token);var received=await BridgeProtocol.ReadPacket(stream,deadline.Token);Assert(received.SequenceEqual(response)&&relay.IsConnected,"authenticated return path failed");
        });
    }
}
