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
    }
}
