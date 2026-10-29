using System.Text.Json;

namespace SaidNest.Capture;

static class ReliabilityTests
{
    static void Check(bool value,string notice){if(!value)throw new Exception("Reliability check failed: "+notice);}
    public static async Task Run(string parent)
    {
        string root=Path.Combine(parent,"reliability-"+Guid.NewGuid().ToString("N"));
        using var vault=Vault.Create(root,"Synthetic reliability recovery passphrase");
        string id=await IntegrationTests.Fixture(vault);
        string profile=Path.Combine(root,"install.json");
        File.WriteAllText(Path.Combine(root,"install-large-v3.json"),JsonSerializer.Serialize(new EngineInstall("fixture","fixture","fixture","fixture","fixture","large-v3"),Vault.Json));
        var healthy=RefinementQueue.Enqueue(vault,id,profile,"large-v3");
        string bad=Guid.NewGuid().ToString("N"),wrong=Guid.NewGuid().ToString("N");
        string damaged=Path.Combine(root,"refinement-queue",$"job-{bad}.snq");byte[] bytes=[1,2,3,4,5];File.WriteAllBytes(damaged,bytes);
        vault.Write($"refinement-queue/job-{wrong}.snq",healthy with {Id=wrong,Version=99});
        var scan=RefinementQueue.Scan(vault);Check(scan.Jobs.Length==1 && scan.Issues.Length==2,"ciphertext and schema errors are isolated per record");
        var ready=new IdleSample(300,.01,true,new(.01,.01,20000,"Synthetic resource readings"));
        for(int i=0;i<3;i++)Check(await RefinementQueue.Step(vault,()=>ready,testDecoder:(audio,track,_)=>Task.FromResult(new TrackTranscript(track,"transcribed",audio.DurationSeconds,"Synthetic reliability decoder",[new(track,0,audio.DurationSeconds,"Synthetic isolated job words")]))),"healthy job continues beside isolated records");
        Check(RefinementQueue.Read(vault,healthy.Id).State=="completed" && File.ReadAllBytes(damaged).SequenceEqual(bytes),"corrupt bytes are retained and healthy completion commits");
        await using(var reporter=new QueueWorkerReporter(vault)) {
            reporter.QueueState(RefinementQueue.Scan(vault));reporter.Activity("waiting","Synthetic reliability health fixture.");reporter.Progress();
            var health=QueueWorkerHealth.Read(vault);Check(health.State=="degraded" && health.Worker!.IsolatedRecords==2 && health.Worker.LastProgressUtc!=null,"health exposes corruption and last successful progress");
            var now=DateTimeOffset.UtcNow;var raw=health.Worker!;
            Check(QueueWorkerHealth.Evaluate(raw with {HeartbeatUtc=now.AddSeconds(-20)},now,true).State=="unresponsive","stale heartbeat cannot look healthy");
            Check(QueueWorkerHealth.Evaluate(raw with {Activity="running",HeartbeatUtc=now,CycleStartedUtc=now.AddMinutes(-4)},now,true).State=="stalled","live heartbeat does not conceal a stalled window");
            Check(QueueWorkerHealth.Evaluate(raw,now,false).State=="stopped","dead or reused process does not look healthy");
        }
        Check(QueueWorkerHealth.Read(vault).State=="stopped","graceful worker exit is persisted");
        var priorProgress=QueueWorkerHealth.Read(vault).Worker!.LastProgressUtc;
        await using(var restartedReporter=new QueueWorkerReporter(vault))Check(QueueWorkerHealth.Read(vault).Worker!.LastProgressUtc==priorProgress,"last successful progress survives a new worker instance");
        File.WriteAllBytes(Path.Combine(root,QueueWorkerHealth.Path),[1,2,3]);Check(QueueWorkerHealth.Read(vault).State=="unavailable","damaged health metadata is reported without blocking the service");
        using var runVault=Vault.Create(Path.Combine(parent,"worker-"+Guid.NewGuid().ToString("N")),"Synthetic worker recovery passphrase");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));int cycles=0;
        var run=RefinementQueue.Run(runVault,stop.Token,_=>{if(Interlocked.Increment(ref cycles)==1)throw new InvalidOperationException("Synthetic fault must never become a raw health message");return Task.FromResult(false);});
        async Task Until(Func<bool> condition){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!condition())await Task.Delay(20,deadline.Token);}
        await Until(()=>QueueWorkerHealth.Read(runVault).State=="faulted");
        string firstInstance=QueueWorkerHealth.Read(runVault).Worker!.InstanceId;
        using var standbyStop=new CancellationTokenSource(TimeSpan.FromSeconds(15));int standbyCycles=0;
        var standby=RefinementQueue.Run(runVault,standbyStop.Token,_=>{Interlocked.Increment(ref standbyCycles);return Task.FromResult(false);});
        var first=QueueWorkerHealth.Read(runVault).Worker!.HeartbeatUtc;await Task.Delay(2200);
        Check(QueueWorkerHealth.Read(runVault).Worker!.HeartbeatUtc>first,"heartbeat continues while the worker is in error backoff");
        Check(standbyCycles==0 && QueueWorkerHealth.Read(runVault).Worker!.InstanceId==firstInstance,"standby worker does not overwrite the current owner's heartbeat");
        await Until(()=>cycles>=2 && QueueWorkerHealth.Read(runVault).State=="healthy");
        Check(QueueWorkerHealth.Read(runVault).Worker!.LastErrorCode=="worker-cycle-failed","recovery preserves a safe last error code");
        stop.Cancel();await run;
        await Until(()=>standbyCycles>0 && QueueWorkerHealth.Read(runVault).Worker?.InstanceId!=firstInstance);
        standbyStop.Cancel();await standby;Check(QueueWorkerHealth.Read(runVault).State=="stopped","standby takes over after owner exit and cancels cleanly");
        var install=healthy.Install with {ModelRevision="in-progress-corruption-fixture"};
        File.WriteAllText(Path.Combine(root,"install-large-v3.json"),JsonSerializer.Serialize(install,Vault.Json));
        var inProgress=RefinementQueue.Enqueue(vault,id,profile,"large-v3");
        File.WriteAllText(Path.Combine(root,"install-large-v3.json"),JsonSerializer.Serialize(install with {ModelRevision="healthy-after-corruption-fixture"},Vault.Json));
        var nextJob=RefinementQueue.Enqueue(vault,id,profile,"large-v3");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted=RefinementQueue.Step(vault,()=>ready,testDecoder:async(audio,track,token)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return new TrackTranscript(track,"no-text",audio.DurationSeconds,"Unused synthetic decoder",[]);});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string activePath=Path.Combine(root,"refinement-queue",$"job-{inProgress.Id}.snq");File.WriteAllBytes(activePath,[7,8,9]);
        Check(!await interrupted.WaitAsync(TimeSpan.FromSeconds(5)) && Transcription.ReadRefinement(vault,id)!.State=="isolated","in-progress record damage cancels work and clears a false running display");
        for(int n=0;n<3;n++)Check(await RefinementQueue.Step(vault,()=>ready,testDecoder:(audio,track,_)=>Task.FromResult(new TrackTranscript(track,"no-text",audio.DurationSeconds,"Healthy continuation fixture",[]))),"next healthy job runs after live record damage");
        Check(RefinementQueue.Read(vault,nextJob.Id).State=="completed" && File.ReadAllBytes(activePath).SequenceEqual(new byte[]{7,8,9}),"in-progress damage remains preserved and cannot block the following job");
        Console.WriteLine("Passed: corrupt-record isolation, preserved evidence, healthy-job continuation, heartbeat/progress reporting, stale/stalled/dead states, error backoff and worker recovery.");
        await DecoderCrashTests.Run(parent);
    }
}
