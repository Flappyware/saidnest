using System.Diagnostics;
using System.Text.Json;

namespace SaidNest.Capture;

sealed record WorkerTelemetry(int Version,string InstanceId,int ProcessId,long ProcessStartTicks,string Activity,string Notice,DateTimeOffset HeartbeatUtc,DateTimeOffset? CycleStartedUtc=null,DateTimeOffset? LastProgressUtc=null,string? ActiveJobId=null,long ProgressEvents=0,int IsolatedRecords=0,int FailedJobs=0,int ConsecutiveErrors=0,string? LastErrorCode=null,DateTimeOffset? LastErrorUtc=null);
sealed record WorkerHealthView(string State,string Notice,WorkerTelemetry? Worker);

static class QueueWorkerHealth
{
    public const string Path="refinement-queue/worker-health.snq";
    public static WorkerHealthView Read(Vault vault,DateTimeOffset? at=null)
    {
        try {
            if(!File.Exists(System.IO.Path.Combine(vault.Root,Path)))return new("unavailable","No worker heartbeat has been recorded.",null);
            var data=vault.Read(Path).Metadata.Deserialize<WorkerTelemetry>(Vault.Json) ?? throw new InvalidDataException();
            if(data.Version!=1 || data.ProcessId<=0 || data.ProcessStartTicks<=0 || data.HeartbeatUtc==default || !Guid.TryParseExact(data.InstanceId,"N",out _) || data.Activity is not ("starting" or "idle" or "waiting" or "running" or "backoff" or "stopped") || string.IsNullOrWhiteSpace(data.Notice) || data.ProgressEvents<0 || data.IsolatedRecords<0 || data.FailedJobs<0 || data.ConsecutiveErrors<0)throw new InvalidDataException();
            return Evaluate(data,at ?? DateTimeOffset.UtcNow,Alive(data));
        }catch(Exception ex)when(RefinementQueue.RecordError(ex) || ex is InvalidOperationException){return new("unavailable","Worker health could not be verified. Audio and transcript versions are retained.",null);}
    }
    static bool Alive(WorkerTelemetry data)
    {
        try {using var process=Process.GetProcessById(data.ProcessId);return process.StartTime.ToUniversalTime().Ticks==data.ProcessStartTicks;}
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return false;}
    }
    public static WorkerHealthView Evaluate(WorkerTelemetry data,DateTimeOffset now,bool alive)
    {
        if(data.Activity=="stopped" || !alive)return new("stopped","The refinement worker is stopped. Saved jobs resume when a worker starts.",data);
        if(now-data.HeartbeatUtc>TimeSpan.FromSeconds(15) || data.HeartbeatUtc-now>TimeSpan.FromSeconds(15))return new("unresponsive","The worker heartbeat is stale or its clock changed; inspect the background service.",data);
        if(data.ConsecutiveErrors>0)return new("faulted","The worker encountered an error and is retrying with a delay. See its last error code.",data);
        if(data.Activity=="running" && data.CycleStartedUtc is {} started && now-started>TimeSpan.FromMinutes(3))return new("stalled","The current refinement window has not finished within three minutes. Completed windows are retained.",data);
        if(data.IsolatedRecords>0 || data.FailedJobs>0)return new("degraded","Some queue records or jobs need attention. Healthy queued jobs can continue.",data);
        return new("healthy",data.Notice,data);
    }
}

sealed class QueueWorkerReporter:IAsyncDisposable
{
    readonly Vault vault;
    readonly object gate=new();
    readonly CancellationTokenSource stop=new();
    readonly Task heartbeat;
    WorkerTelemetry current;
    public QueueWorkerReporter(Vault vault)
    {
        this.vault=vault;using var process=Process.GetCurrentProcess();
        var previous=QueueWorkerHealth.Read(vault).Worker;
        current=new(1,Guid.NewGuid().ToString("N"),Environment.ProcessId,process.StartTime.ToUniversalTime().Ticks,"starting","Starting the local refinement worker.",DateTimeOffset.UtcNow,LastProgressUtc:previous?.LastProgressUtc,ProgressEvents:previous?.ProgressEvents ?? 0,LastErrorCode:previous?.LastErrorCode,LastErrorUtc:previous?.LastErrorUtc);
        Publish();heartbeat=Task.Run(Pulse);
    }
    void Change(Func<WorkerTelemetry,WorkerTelemetry> update){lock(gate){current=update(current);PublishLocked();}}
    public void QueueState(QueueScan scan)=>Change(h=>h with {IsolatedRecords=scan.Issues.Length,FailedJobs=scan.Jobs.Count(j=>j.State=="failed")});
    public void Activity(string activity,string notice,string? id=null)=>Change(h=>h with {Activity=activity,Notice=notice,ActiveJobId=id,CycleStartedUtc=activity=="running"?DateTimeOffset.UtcNow:null});
    public void Progress()=>Change(h=>h with {LastProgressUtc=DateTimeOffset.UtcNow,ProgressEvents=h.ProgressEvents+1});
    public void JobFailure()=>Change(h=>h with {Activity="waiting",ActiveJobId=null,CycleStartedUtc=null,FailedJobs=h.FailedJobs+1,LastErrorCode="job-failed",LastErrorUtc=DateTimeOffset.UtcNow,Notice="A refinement job failed. Other healthy jobs remain eligible."});
    public void Fault()=>Change(h=>h with {Activity="backoff",ActiveJobId=null,CycleStartedUtc=null,ConsecutiveErrors=h.ConsecutiveErrors+1,LastErrorCode="worker-cycle-failed",LastErrorUtc=DateTimeOffset.UtcNow,Notice="The worker is retrying after an error."});
    public void CycleFinished()=>Change(h=>h with {ConsecutiveErrors=0,Activity=h.Activity is "running" or "backoff"?"waiting":h.Activity,Notice=h.Activity=="backoff"?"The worker recovered and is ready for eligible work.":h.Notice,ActiveJobId=null,CycleStartedUtc=null});
    void Publish(){lock(gate)PublishLocked();}
    void PublishLocked()
    {
        current=current with {HeartbeatUtc=DateTimeOffset.UtcNow};
        try {vault.Replace(QueueWorkerHealth.Path,current);}
        catch(Exception ex)when(RefinementQueue.RecordError(ex)){current=current with {LastErrorCode="health-write-failed",LastErrorUtc=DateTimeOffset.UtcNow};}
    }
    async Task Pulse()
    {
        try {while(true){await Task.Delay(2000,stop.Token);Publish();}}
        catch(OperationCanceledException)when(stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();await heartbeat;Activity("stopped","The refinement worker stopped; completed windows and pending jobs are retained.");stop.Dispose();
    }
}
