namespace SaidNest.Capture;

static class ModelProfiles
{
    static readonly string[] Names=["tiny.en","small.en","large-v3-turbo","large-v3"];
    public static bool Busy(Vault vault)
    {
        string path=Path.Combine(vault.Root,"inference.lock");if(!File.Exists(path))return false;
        try {using var lease=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);return false;}
        catch(IOException){return true;}
    }
    public static string Install(string defaultInstall,string name)
    {
        if(!Names.Contains(name,StringComparer.Ordinal)) throw new ArgumentException("Unknown model profile.");
        string path=name=="tiny.en"?defaultInstall:Path.Combine(Path.GetDirectoryName(Path.GetFullPath(defaultInstall))!,"install-"+name+".json");
        if(!File.Exists(path)) throw new FileNotFoundException("Provision this model locally first.");
        return path;
    }
    public static object[] Available(string defaultInstall)=>Names.Where(name=>File.Exists(name=="tiny.en"?defaultInstall:Path.Combine(Path.GetDirectoryName(Path.GetFullPath(defaultInstall))!,"install-"+name+".json"))).Select(name=>(object)new {id=name,name}).ToArray();
}
