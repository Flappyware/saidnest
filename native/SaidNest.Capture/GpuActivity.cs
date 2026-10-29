using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SaidNest.Capture;

sealed record GpuSample(double? ForeignBusyFraction,double? TotalBusyFraction,long? FreeMemoryMiB,string Notice,bool OwnDecoderActiveOrRecent=false);
sealed record GpuEngine(string Instance,double Percent);

// Only decoder processes started by this worker are excluded. Never infer ownership by name.
static class GpuOwnership
{
    static readonly ConcurrentDictionary<int,long> owned=[];
    sealed class Registration(int pid):IDisposable
    {
        public void Dispose()=>owned[pid]=Environment.TickCount64+2000;
    }
    public static IDisposable Register(int pid){foreach(var entry in owned)if(entry.Value<Environment.TickCount64)owned.TryRemove(entry.Key,out _);owned[pid]=long.MaxValue;return new Registration(pid);}
    public static bool Contains(int pid)
    {
        if(!owned.TryGetValue(pid,out long until))return false;
        if(until>=Environment.TickCount64)return true;
        owned.TryRemove(pid,out _);return false;
    }
    public static bool Recent=>owned.Values.Any(until=>until>=Environment.TickCount64);
}

sealed partial class GpuActivity:IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct Utilization {public uint Gpu,Memory;}
    [StructLayout(LayoutKind.Sequential)] struct Memory {public ulong Total,Free,Used;}
    [StructLayout(LayoutKind.Explicit,Size=16)] struct CounterValue { [FieldOffset(0)]public uint Status;[FieldOffset(8)]public double Value; }
    [StructLayout(LayoutKind.Sequential)] struct CounterItem {public nint Name;public CounterValue Value;}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Count(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Handle(uint index,out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UtilizationQuery(nint device,out Utilization value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int MemoryQuery(nint device,out Memory value);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source,nuint data,out nint query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(nint query,string path,nuint data,out nint counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(nint counter,uint format,ref uint size,out uint count,nint buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(nint query);
    [GeneratedRegex(@"^pid_(\d+)_(luid_.*_phys_\d+_eng_\d+)_engtype_",RegexOptions.IgnoreCase)]private static partial Regex Instance();
    nint library,device,query,counter;
    Shutdown? shutdown;
    UtilizationQuery? utilization;
    MemoryQuery? memory;
    long sampledAt=long.MinValue,retryAt;
    int collections;
    GpuSample last=new(null,null,null,"GPU telemetry is warming up.");
    T Export<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,name));
    bool InitializeTelemetry()
    {
        try {
            // Load only the driver-installed system DLL, never a workspace DLL of the same name.
            library=NativeLibrary.Load(Path.Combine(Environment.SystemDirectory,"nvml.dll"));
            if(Export<Initialize>("nvmlInit_v2")()!=0)throw new InvalidOperationException();
            shutdown=Export<Shutdown>("nvmlShutdown");
            if(Export<Count>("nvmlDeviceGetCount_v2")(out uint count)!=0 || count!=1){last=new(null,null,null,"GPU monitoring requires one NVIDIA GPU; multiple-device mapping is not configured.");Dispose();return false;}
            if(Export<Handle>("nvmlDeviceGetHandleByIndex_v2")(0,out device)!=0)throw new InvalidOperationException();
            utilization=Export<UtilizationQuery>("nvmlDeviceGetUtilizationRates");memory=Export<MemoryQuery>("nvmlDeviceGetMemoryInfo");
            if(PdhOpenQueryW(null,0,out query)!=0 || PdhAddEnglishCounterW(query,@"\GPU Engine(*)\Utilization Percentage",0,out counter)!=0)throw new InvalidOperationException();
            collections=0;return true;
        }catch(Exception ex)when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException) {
            Dispose();last=new(null,null,null,"GPU telemetry is unavailable; refinement waits for reliable readings.");return false;
        }
    }
    public GpuSample Read()
    {
        long now=Environment.TickCount64;
        if(sampledAt!=long.MinValue && now-sampledAt<1000)return last with {OwnDecoderActiveOrRecent=GpuOwnership.Recent};
        sampledAt=now;
        if(library==0) {
            if(now<retryAt)return last;
            retryAt=now+30000;if(!InitializeTelemetry())return last;
        }
        if(memory!(device,out var ram)!=0 || utilization!(device,out var load)!=0 || PdhCollectQueryData(query)!=0) {
            Dispose();return last=new(null,null,null,"GPU telemetry was lost; refinement is deferred.");
        }
        collections++;
        var engines=ReadEngines();
        double? foreign=collections>=2 && engines!=null?ForeignBusy(engines,GpuOwnership.Contains):null;
        return last=new(foreign,load.Gpu/100.0,(long)(ram.Free/(1024*1024)),foreign==null?"GPU telemetry is warming up or incomplete.":"Windows per-process GPU engines and NVIDIA free-memory telemetry.",GpuOwnership.Recent);
    }
    GpuEngine[]? ReadEngines()
    {
        const uint MoreData=0x800007D2,format=0x200|0x8000;
        uint size=0;
        uint status=PdhGetFormattedCounterArrayW(counter,format,ref size,out uint count,0);
        if(status==0 && count==0)return [];
        if(status!=MoreData || size==0 || size>8*1024*1024)return null;
        nint buffer=Marshal.AllocHGlobal((int)size);
        try {
            if(PdhGetFormattedCounterArrayW(counter,format,ref size,out count,buffer)!=0)return null;
            int stride=Marshal.SizeOf<CounterItem>();if((long)count*stride>size)return null;
            var values=new List<GpuEngine>();
            for(int i=0;i<count;i++) {
                var item=Marshal.PtrToStructure<CounterItem>(buffer+i*stride);
                if(item.Value.Status is 0 or 1 && double.IsFinite(item.Value.Value) && item.Value.Value>=0 && Marshal.PtrToStringUni(item.Name) is {} name)values.Add(new(name,item.Value.Value));
            }
            return count>0 && values.Count==0?null:values.ToArray();
        }finally{Marshal.FreeHGlobal(buffer);}
    }
    public static double? ForeignBusy(IEnumerable<GpuEngine> engines,Func<int,bool> isOwned)
    {
        var totals=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
        foreach(var engine in engines) {
            var match=Instance().Match(engine.Instance);
            if(!match.Success || !int.TryParse(match.Groups[1].Value,out int pid) || !double.IsFinite(engine.Percent) || engine.Percent<0)return null;
            if(isOwned(pid))continue;
            string key=match.Groups[2].Value;totals[key]=totals.GetValueOrDefault(key)+engine.Percent;
        }
        return Math.Clamp(totals.Values.DefaultIfEmpty(0).Max()/100,0,1);
    }
    public void Dispose()
    {
        if(query!=0){PdhCloseQuery(query);query=0;counter=0;}
        shutdown?.Invoke();shutdown=null;
        if(library!=0){NativeLibrary.Free(library);library=0;}
        memory=null;utilization=null;device=0;
    }
    public void Suspend(){Dispose();sampledAt=long.MinValue;retryAt=0;last=new(null,null,null,"GPU monitoring waits until basic idle conditions are met.");}
}
