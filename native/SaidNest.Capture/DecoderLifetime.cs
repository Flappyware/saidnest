using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SaidNest.Capture;

// The handle is unnamed and not inheritable. Parent exit closes it even after a hard crash.
sealed class DecoderLifetime:IDisposable
{
    sealed class JobHandle:SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle():base(true) { }
        protected override bool ReleaseHandle()=>CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits {public long ProcessTime,JobTime;public uint Flags;public nuint MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public nuint Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)] struct IoCounters {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {public BasicLimits Basic;public IoCounters Io;public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern JobHandle CreateJobObjectW(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(JobHandle job,int informationClass,ref ExtendedLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(JobHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    readonly JobHandle job;
    public DecoderLifetime()
    {
        job=CreateJobObjectW(0,null);
        if(job.IsInvalid)throw new IOException("Decoder lifetime protection could not be created.",new Win32Exception(Marshal.GetLastWin32Error()));
        var limits=new ExtendedLimits {Basic=new BasicLimits {Flags=0x2000}}; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())){int error=Marshal.GetLastWin32Error();job.Dispose();throw new IOException("Decoder lifetime protection could not be configured.",new Win32Exception(error));}
    }
    public void Attach(Process process)
    {
        if(AssignProcessToJobObject(job,process.SafeHandle))return;
        int error=Marshal.GetLastWin32Error();
        if(!process.HasExited){process.Kill(true);process.WaitForExit();}
        throw new IOException("The decoder could not be attached to its parent lifetime; it was stopped before receiving audio.",new Win32Exception(error));
    }
    public void Dispose()=>job.Dispose();
}
