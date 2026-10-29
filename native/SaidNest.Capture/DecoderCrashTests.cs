using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace SaidNest.Capture;

static class DecoderCrashTests
{
    sealed record ChildIdentity(int Pid,long StartTicks);
    static ChildIdentity Identity(Process process)=>new(process.Id,process.StartTime.ToUniversalTime().Ticks);
    static Process Start(string command,string root,string? install=null)
    {
        var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(typeof(DecoderCrashTests).Assembly.Location);
        foreach(string arg in new[]{command,"--out",root})info.ArgumentList.Add(arg);
        if(install!=null){info.ArgumentList.Add("--install");info.ArgumentList.Add(Path.GetFullPath(install));}
        return Process.Start(info) ?? throw new IOException("Crash-test process did not start.");
    }
    static void Write(string root,string file,object value)=>Vault.AtomicWrite(Path.Combine(root,file),JsonSerializer.SerializeToUtf8Bytes(value,Vault.Json));
    public static async Task<int> Command(string command,string root,string? install)
    {
        root=Path.GetFullPath(root);Directory.CreateDirectory(root);
        if(command=="decoder-crash-test"){await Run(root,install);return 0;}
        if(command=="decoder-crash-parent") {
            if(install!=null) {
                var engine=await Transcription.Load(install);
                var audio=Tone();
                await Transcription.Decode(engine,audio,"mic",bypassQuietGuard:true,testGpuReady:pid=> {using var child=Process.GetProcessById(pid);Write(root,"ready.json",new[]{Identity(child)});});
                return 2; // Completion before the orchestrator's crash is not a successful crash test.
            }
            using var lifetime=new DecoderLifetime();using var childFixture=Start("decoder-crash-child",root);lifetime.Attach(childFixture);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(!File.Exists(Path.Combine(root,"grandchild.json")))await Task.Delay(10,deadline.Token);
            var grandchild=JsonSerializer.Deserialize<ChildIdentity>(File.ReadAllBytes(Path.Combine(root,"grandchild.json")),Vault.Json)!;
            Write(root,"ready.json",new[]{Identity(childFixture),grandchild});
            await Task.Delay(Timeout.Infinite);return 0;
        }
        if(command=="decoder-crash-child") {using var grandchild=Start("decoder-crash-grandchild",root);await Task.Delay(Timeout.Infinite);return 0;}
        if(command=="decoder-crash-grandchild") {using var self=Process.GetCurrentProcess();Write(root,"grandchild.json",Identity(self));await Task.Delay(Timeout.Infinite);return 0;}
        return 1;
    }
    public static async Task Run(string parent,string? install=null)
    {
        string root=Path.Combine(Path.GetFullPath(parent),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var host=Start("decoder-crash-parent",root,install);
        var output=host.StandardOutput.ReadToEndAsync();var error=host.StandardError.ReadToEndAsync();
        var children=new List<Process>();
        try {
            using var readyDeadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while(!File.Exists(Path.Combine(root,"ready.json"))) {
                if(host.HasExited)throw new Exception("Crash-test parent exited before its decoder was ready.");
                await Task.Delay(5,readyDeadline.Token);
            }
            var identities=JsonSerializer.Deserialize<ChildIdentity[]>(File.ReadAllBytes(Path.Combine(root,"ready.json")),Vault.Json)!;
            foreach(var identity in identities) {
                var child=Process.GetProcessById(identity.Pid);
                if(child.StartTime.ToUniversalTime().Ticks!=identity.StartTicks)throw new Exception("Crash-test child identity changed.");
                children.Add(child);
            }
            if(host.HasExited)throw new Exception("Decoder finished before the parent-only crash could be applied.");
            host.Kill(entireProcessTree:false); // Intentionally do not help Windows by killing the tree.
            await host.WaitForExitAsync();
            await Task.WhenAll(children.Select(child=>child.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(10));
            if(children.Any(child=>!child.HasExited))throw new Exception("A decoder descendant survived its parent.");
            await output;await error;
            Console.WriteLine(install==null?"Passed: parent-only forced crash closes the decoder job and terminates child plus grandchild without a tree kill.":"Passed: parent-only forced crash terminated the real local decoder after CUDA allocation. Generated tone only; no private audio or words were logged.");
        }finally {
            if(!host.HasExited){host.Kill(true);await host.WaitForExitAsync();}
            foreach(var child in children){try{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}finally{child.Dispose();}}
        }
    }
    static RenderedAudio Tone()
    {
        int frames=30*48000;byte[] wav=new byte[44+frames*2];
        "RIFF"u8.CopyTo(wav);BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4),wav.Length-8);"WAVEfmt "u8.CopyTo(wav.AsSpan(8));BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16),16);BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20),1);BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22),1);BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24),48000);BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28),96000);BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32),2);BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34),16);"data"u8.CopyTo(wav.AsSpan(36));BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40),frames*2);
        for(int n=0;n<frames;n++)BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44+n*2),(short)(1000*Math.Sin(n*.05)));
        return new(wav,30,-30,-33,0,0);
    }
}
