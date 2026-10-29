namespace SaidNest.Capture;

// OS-held intent survives neither process exit nor a crash. Stale filenames are harmless.
sealed class ForegroundIntent : IDisposable
{
    readonly FileStream lease;
    readonly string path;
    public ForegroundIntent(Vault vault)
    {
        path=Path.Combine(vault.Root,$"foreground-{Guid.NewGuid():N}.lock");
        lease=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
    }
    public void Dispose() { lease.Dispose(); try { File.Delete(path); } catch(IOException) { } }
}

static class InferencePriority
{
    public static FileStream? TryAcquire(Vault vault,string name="inference.lock")
    {
        try { return new FileStream(Path.Combine(vault.Root,name),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch(IOException) { return null; }
    }
    public static async Task<FileStream> Acquire(Vault vault,CancellationToken token)
    {
        while(true) { token.ThrowIfCancellationRequested(); var lease=TryAcquire(vault); if(lease!=null)return lease; await Task.Delay(50,token); }
    }
    public static bool ForegroundActive(Vault vault)
    {
        foreach(string path in Directory.EnumerateFiles(vault.Root,"foreground-*.lock")) {
            try { using var probe=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None); }
            catch(FileNotFoundException) { }
            catch(IOException) { return true; }
        }
        // Also recognize recorders from before priority intents were introduced.
        return Directory.EnumerateDirectories(Path.Combine(vault.Root,"sessions"))
            .Any(path=>Guid.TryParseExact(Path.GetFileName(path),"N",out _) && Archive.IsRecording(vault,Path.GetFileName(path)));
    }
}
