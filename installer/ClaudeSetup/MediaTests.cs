using System.IO;

namespace ClaudeSetup;

public static class MediaTests
{
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        static void Assert(bool value,string message){if(!value)throw new Exception(message);}
        var image=new WindowsImageChoice(6,"Windows 11 Pro","x64","10.0.26100.1","Client","Professional");
        await check("media-detects-supported-windows11-client-x64",()=>{Assert(image.Supported,"valid Windows 11 rejected");return Task.CompletedTask;});
        foreach(var (name,invalid) in new[]{("windows10",image with{Name="Windows 10 Pro",Version="10.0.19045.1"}),("renamed-windows10",image with{Version="10.0.19045.1"}),("arm64",image with{Architecture="arm64"}),("server",image with{InstallationType="Server"}),("linux",image with{Name="Ubuntu 24.04"})})
            await check("media-rejects-unsupported-"+name,()=>{Assert(!invalid.Supported,"unsupported image entered Windows 11 workflow");return Task.CompletedTask;});
        await check("media-proof-binds-file-digest-and-selected-index",()=>{
            var o=new SetupOptions{Root=Path.Combine(root,"media-proof"),IsoPath=Path.Combine(root,"Windows.iso"),WindowsImageIndex=6};
            var media=new WindowsMediaIdentity(o.IsoPath,new string('a',64),2L<<30,[image]);media.Validate();JsonFile.Write(Paths.Under(o.Private,"windows-media-identity.json"),media);
            WindowsMedia.RequireSelected(o,media.Sha256);
            foreach(var change in new[]{"digest","index","file"}){
                var rejected=false;o.WindowsImageIndex=change=="index"?1:6;o.IsoPath=change=="file"?Path.Combine(root,"Other.iso"):media.IsoPath;
                try{WindowsMedia.RequireSelected(o,change=="digest"?new string('b',64):media.Sha256);}catch(HandoffException){rejected=true;}
                Assert(rejected,"image selection or file change retained approval: "+change);
            }return Task.CompletedTask;
        });
        await check("media-rejects-duplicate-index-or-mixed-unsupported-proof",()=>{
            foreach(var images in new[]{new List<WindowsImageChoice>{image,image},new(){image,image with{Index=7,Architecture="arm64"}}}){
                var rejected=false;try{new WindowsMediaIdentity(Path.Combine(root,"Windows.iso"),new string('a',64),2L<<30,images).Validate();}catch(SetupException){rejected=true;}Assert(rejected,"ambiguous image proof accepted");
            }return Task.CompletedTask;
        });
        await check("media-blocks-home-china-core-country-specific-on-hyperv-standalone", () =>
        {
            var homeChina = new WindowsImageChoice(1, "Windows 11 家庭中文版", "x64", "10.0.26100.1", "Client", "CoreCountrySpecific");
            var pro = image;
            var o = new SetupOptions { Root = Path.Combine(root, "media-home-china"), IsoPath = Path.Combine(root, "Windows.iso"), WindowsImageIndex = 1 };
            var media = new WindowsMediaIdentity(o.IsoPath, new string('d', 64), 2L << 30, [homeChina, pro]);
            media.Validate();
            JsonFile.Write(Paths.Under(o.Private, "windows-media-identity.json"), media);
            if (ProductProfile.IsHyperVStandalone)
            {
                Assert(homeChina.IsHomeChinaBlocked && homeChina.Label.Contains("家庭中文版不支持"), "CoreCountrySpecific flag/label missing");
                var blocked = false;
                try { WindowsMedia.RequireSelected(o, media.Sha256); } catch (HandoffException ex) { blocked = ex.Action.Id == "windows-media" && ex.Action.Title.Contains("家庭中文版"); }
                Assert(blocked, "CoreCountrySpecific was allowed in Hyper-V standalone");
            }
            o.WindowsImageIndex = 6;
            WindowsMedia.RequireSelected(o, media.Sha256);
            return Task.CompletedTask;
        });
        await check("media-discovers-candidate-isos-safely", () =>
        {
            var dir = Path.Combine(root, "iso-scan-fixture");
            Directory.CreateDirectory(dir);
            var fakeIso = Path.Combine(dir, "Win11_24H2_Chinese_Simplified_x64.iso");
            var tinyIso = Path.Combine(dir, "tiny.iso");
            File.WriteAllBytes(fakeIso, new byte[4096]);
            File.WriteAllBytes(tinyIso, new byte[100]);
            var found = WindowsMedia.DiscoverCandidateIsos([dir], maxResults: 5, minBytes: 1024);
            Assert(found.Count == 1 && string.Equals(found[0].IsoPath, fakeIso, StringComparison.OrdinalIgnoreCase), "candidate ISO discovery missed or included tiny file");
            return Task.CompletedTask;
        });
    }
}
