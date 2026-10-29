using System.Security.Cryptography;
using System.Text.Json;

namespace SaidNest.Capture;

sealed record RefinementPolicy(bool Enabled=true,bool QueueNewRecordings=true,string Model="large-v3",int IdleSeconds=120,double MaxCpuBusyFraction=.25,bool RequireAcPower=true,double MaxGpuBusyFraction=.10,int MinGpuFreeMiB=6144,int MinRunningGpuFreeMiB=1024);
sealed record RefinementJob(int Version,string Id,string SessionId,string Model,string State,string Notice,DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc,string SourceManifestHash,string? ParentRevisionId,string RevisionId,EngineInstall Install,int CompletedWindows=0,double ProcessedSeconds=0,double TotalSeconds=0);
sealed record RefinementWindow(string JobId,string SourceManifestHash,string ModelSha256,string Track,int Index,double Start,double Seconds,string AudioSha256,TrackTranscript Transcript);
sealed record QueueIssue(string RecordKey,string? JobId,string Code,string Notice);
sealed record QueueScan(RefinementJob[] Jobs,QueueIssue[] Issues);
sealed class RefinementPreparation
{
    public string? JobId;
    public SessionInfo? Session;
    public Dictionary<string,AudioWindows>? Indexes;
    public EngineInstall? Install;
    public long ModelLength,ModelModified;
    public void Clear(){JobId=null;Session=null;Indexes=null;Install=null;}
}

static class RefinementQueue
{
    const string Folder="refinement-queue";
    static string JobPath(string id) { if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Invalid queue job.");return $"{Folder}/job-{id}.snq"; }
    public static RefinementPolicy Policy(Vault vault)=>File.Exists(Path.Combine(vault.Root,Folder,"policy.snq"))?vault.Read($"{Folder}/policy.snq").Metadata.Deserialize<RefinementPolicy>(Vault.Json)!:new();
    public static void SetPolicy(Vault vault,RefinementPolicy policy)
    {
        if(policy.IdleSeconds is < 10 or > 86400 || !double.IsFinite(policy.MaxCpuBusyFraction) || policy.MaxCpuBusyFraction is < .01 or > 1 || !ModelProfiles.Names.Contains(policy.Model,StringComparer.Ordinal) || !double.IsFinite(policy.MaxGpuBusyFraction) || policy.MaxGpuBusyFraction is < 0 or > 1 || policy.MinGpuFreeMiB is <1024 or >131072 || policy.MinRunningGpuFreeMiB<256 || policy.MinRunningGpuFreeMiB>policy.MinGpuFreeMiB)throw new ArgumentException("Invalid refinement policy.");
        using var gate=Lock(vault);vault.Replace($"{Folder}/policy.snq",policy);
    }
    static FileStream Lock(Vault vault)
    {
        // Mutations are short; no model, archive scan or audio decode runs under this gate.
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(true) { var gate=InferencePriority.TryAcquire(vault,"refinement-queue.lock"); if(gate!=null)return gate; if(DateTime.UtcNow>=deadline)throw new IOException("Queue is busy.");Thread.Sleep(10); }
    }
    internal static bool RecordError(Exception ex)=>ex is IOException or InvalidDataException or CryptographicException or JsonException or ArgumentException or UnauthorizedAccessException or NotSupportedException;
    public static QueueScan Scan(Vault vault)
    {
        var jobs=new List<RefinementJob>();var issues=new List<QueueIssue>();
        try {
            if(!Directory.Exists(Path.Combine(vault.Root,Folder)))return new([],[]);
            foreach(string path in Directory.GetFiles(Path.Combine(vault.Root,Folder),"job-*.snq")) {
                string name=Path.GetFileName(path),id=Path.GetFileNameWithoutExtension(path)[4..];
                string key=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
                try {jobs.Add(Read(vault,id));}
                catch(Exception ex)when(RecordError(ex)){issues.Add(new(key,Guid.TryParseExact(id,"N",out _)?id:null,"unreadable-record","A queue record could not be verified. It is retained in place and excluded from processing; healthy records remain available."));}
            }
        }catch(Exception ex)when(RecordError(ex)){issues.Add(new("queue-directory",null,"queue-unavailable","Queue records could not be listed. Recording data is retained; check local storage access."));}
        return new(jobs.OrderBy(j=>j.CreatedUtc).ThenBy(j=>j.Id,StringComparer.Ordinal).ToArray(),issues.ToArray());
    }
    public static RefinementJob[] Jobs(Vault vault)=>Scan(vault).Jobs;
    public static RefinementJob Read(Vault vault,string id)
    {
        var job=vault.Read(JobPath(id)).Metadata.Deserialize<RefinementJob>(Vault.Json) ?? throw new InvalidDataException("Missing queue job.");
        if(job.Version!=1 || job.Id!=id || !Guid.TryParseExact(job.SessionId,"N",out _) || !Guid.TryParseExact(job.RevisionId,"N",out _) || (job.ParentRevisionId!=null && !Guid.TryParseExact(job.ParentRevisionId,"N",out _)) || !ModelProfiles.Names.Contains(job.Model,StringComparer.Ordinal) || job.State is not ("queued" or "deferred" or "running" or "paused" or "failed" or "completed" or "cancelled") || string.IsNullOrEmpty(job.SourceManifestHash) || job.Install==null || string.IsNullOrWhiteSpace(job.Install.Executable) || string.IsNullOrWhiteSpace(job.Install.Model) || string.IsNullOrWhiteSpace(job.Install.ModelSha256) || job.CompletedWindows<0 || !double.IsFinite(job.ProcessedSeconds) || !double.IsFinite(job.TotalSeconds) || job.ProcessedSeconds<0 || job.TotalSeconds<0 || job.CreatedUtc==default || job.UpdatedUtc==default)throw new InvalidDataException("Invalid queue record metadata.");
        return job;
    }
    static RefinementJob? TryRead(Vault vault,string id){try{return Read(vault,id);}catch(Exception ex)when(RecordError(ex)){return null;}}
    static void Save(Vault vault,RefinementJob job)=>vault.Replace(JobPath(job.Id),job);
    public static object Summary(RefinementJob job)=>new {job.Id,job.SessionId,job.Model,job.State,job.Notice,job.CreatedUtc,job.UpdatedUtc,job.CompletedWindows,job.ProcessedSeconds,job.TotalSeconds,job.RevisionId};
    public static RefinementJob Enqueue(Vault vault,string sessionId,string defaultInstall,string model)
    {
        if(Archive.Inspect(vault,sessionId).State!="complete")throw new InvalidDataException("Queue only complete verified recordings.");
        string profile=ModelProfiles.Install(defaultInstall,model);
        var install=JsonSerializer.Deserialize<EngineInstall>(File.ReadAllBytes(profile),Vault.Json) ?? throw new InvalidDataException("Missing model profile.");
        string hash=vault.Read($"sessions/{sessionId}/final.snm").Hash;
        var parent=Transcription.Latest(vault,sessionId);
        using var gate=Lock(vault);
        var existing=Jobs(vault).FirstOrDefault(j=>j.SessionId==sessionId && j.SourceManifestHash==hash && j.Install==install && j.State!="cancelled");
        if(existing!=null)return existing;
        var job=new RefinementJob(1,Guid.NewGuid().ToString("N"),sessionId,model,"queued","Waiting for idle time and available resources.",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,hash,parent?.Id,Guid.NewGuid().ToString("N"),install);
        Save(vault,job);return job;
    }
    public static RefinementJob Control(Vault vault,string id,string action)
    {
        using var gate=Lock(vault);var job=Read(vault,id);
        if(job.State is "completed" or "cancelled")return job;
        string state=action switch {"pause"=>"paused","resume"=>"queued","cancel"=>"cancelled",_=>throw new ArgumentException("Select pause, resume, or cancel.")};
        job=job with {State=state,Notice=state=="queued"?"Waiting for idle time; saved windows will be reused.":$"Refinement {state}. Saved audio and transcript versions are retained.",UpdatedUtc=DateTimeOffset.UtcNow};Save(vault,job);
        vault.Replace($"sessions/{job.SessionId}/refinement.snj",new RefinementStatus(state,job.Model,job.ProcessedSeconds,job.TotalSeconds,DateTimeOffset.UtcNow,job.Notice));return job;
    }
    public static string? Blocked(Vault vault,RefinementPolicy policy,IdleSample sample,bool admission=true)
    {
        if(!policy.Enabled)return "Automatic refinement is disabled.";
        if(InferencePriority.ForegroundActive(vault))return "Recording or foreground transcription has priority.";
        if(sample.InputIdleSeconds is not {} idle || idle<policy.IdleSeconds)return "Waiting for the computer to be idle.";
        if(policy.RequireAcPower && sample.OnAcPower!=true)return "Waiting for AC power.";
        if(admission && (sample.CpuBusyFraction is not {} cpu || cpu>policy.MaxCpuBusyFraction))return "Waiting for CPU headroom.";
        if(sample.Gpu?.ForeignBusyFraction is not {} gpu || !double.IsFinite(gpu) || gpu is <0 or >1 || sample.Gpu.TotalBusyFraction is not {} totalGpu || !double.IsFinite(totalGpu) || totalGpu is <0 or >1 || sample.Gpu.FreeMemoryMiB is not {} free || free<0)return "Waiting for reliable GPU activity and memory readings.";
        if(gpu>policy.MaxGpuBusyFraction || (admission && !sample.Gpu.OwnDecoderActiveOrRecent && totalGpu>policy.MaxGpuBusyFraction))return "Another application is using the GPU; refinement is deferred.";
        if(free<(admission?policy.MinGpuFreeMiB:policy.MinRunningGpuFreeMiB))return "Waiting for GPU memory headroom.";
        return null;
    }
    static string WindowPath(RefinementJob job,string track,int index)=>$"{Folder}/{job.Id}/{track}-{index:D8}.snq";
    static RefinementWindow? Window(Vault vault,RefinementJob job,string track,int index)
    {
        string path=WindowPath(job,track,index);
        if(!File.Exists(Path.Combine(vault.Root,path)))return null;
        var window=vault.Read(path).Metadata.Deserialize<RefinementWindow>(Vault.Json) ?? throw new InvalidDataException("Invalid refinement checkpoint.");
        if(window.JobId!=job.Id || window.Track!=track || window.Index!=index || window.SourceManifestHash!=job.SourceManifestHash || window.ModelSha256!=job.Install.ModelSha256)throw new InvalidDataException("Checkpoint provenance mismatch.");
        return window;
    }
    static bool Status(Vault vault,RefinementJob job,string state,string notice,int windows,double processed,double total)
    {
        using var gate=Lock(vault);var current=TryRead(vault,job.Id);
        if(current==null || current.State is "paused" or "cancelled")return false;
        job=current with {State=state,Notice=notice,UpdatedUtc=DateTimeOffset.UtcNow,CompletedWindows=windows,ProcessedSeconds=processed,TotalSeconds=total};Save(vault,job);
        vault.Replace($"sessions/{job.SessionId}/refinement.snj",new RefinementStatus(state,job.Model,processed,total,DateTimeOffset.UtcNow,notice));
        return true;
    }
    public static async Task<bool> Step(Vault vault,Func<IdleSample> environment,CancellationToken token=default,Func<RenderedAudio,string,CancellationToken,Task<TrackTranscript>>? testDecoder=null,RefinementPreparation? preparation=null,QueueWorkerReporter? health=null)
    {
        using var worker=InferencePriority.TryAcquire(vault,"refinement-worker.lock");if(worker==null)return false;
        var scan=Scan(vault);health?.QueueState(scan);
        var job=scan.Jobs.FirstOrDefault(j=>j.State is "queued" or "deferred" or "running");if(job==null){health?.Activity("idle","No queued work is ready.");return false;}
        var reason=Blocked(vault,Policy(vault),environment());
        if(reason!=null) {health?.Activity("waiting",reason); if(job.State!="deferred" || job.Notice!=reason)Status(vault,job,"deferred",reason,job.CompletedWindows,job.ProcessedSeconds,job.TotalSeconds);return false; }
        using var inference=InferencePriority.TryAcquire(vault);
        if(inference==null){health?.Activity("waiting","Waiting for another local model job.");Status(vault,job,"deferred","Waiting for another local model job.",job.CompletedWindows,job.ProcessedSeconds,job.TotalSeconds);return false;}
        using var windowCancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
        using var monitorStop=new CancellationTokenSource();
        string? yieldReason=null;
        var monitor=Task.Run(async()=> {
            try { while(true) {
                await Task.Delay(200,monitorStop.Token);
                var current=Read(vault,job.Id);
                yieldReason=current.State is "paused" or "cancelled"?"Refinement was deferred by the owner.":Blocked(vault,Policy(vault),environment(),admission:false);
                if(yieldReason!=null){windowCancellation.Cancel();return;}
            }}catch(OperationCanceledException)when(monitorStop.IsCancellationRequested) { }
            catch { yieldReason="Resource status could not be read; refinement is deferred.";windowCancellation.Cancel(); }
        });
        try {
            if(!Status(vault,job,"running","Preparing the next resumable local refinement window.",job.CompletedWindows,job.ProcessedSeconds,job.TotalSeconds))return false;
            health?.Activity("running","Processing an encrypted refinement window.",job.Id);
            // Verify once per uninterrupted idle stint. Render still authenticates each used chunk.
            preparation??=new();
            if(vault.Read($"sessions/{job.SessionId}/final.snm").Hash!=job.SourceManifestHash)throw new InvalidDataException("Source changed after enqueue.");
            if(preparation.JobId!=job.Id) {
                preparation.Clear();
                if(Archive.Inspect(vault,job.SessionId,windowCancellation.Token).State!="complete")throw new InvalidDataException("Source failed verification.");
                var source=Archive.ReadSession(vault,job.SessionId);
                var sourceIndexes=source.Tracks.ToDictionary(t=>t.Id,t=>new AudioWindows(vault,source,t));foreach(var index in sourceIndexes.Values)index.Poll(windowCancellation.Token);
                preparation.Session=source;preparation.Indexes=sourceIndexes;preparation.JobId=job.Id;
            }
            windowCancellation.Token.ThrowIfCancellationRequested();
            var session=preparation.Session!;
            var indexes=preparation.Indexes!;
            double total=indexes.Values.Sum(i=>i.Available),processed=0;int completed=0;
            (string Track,int Index,double Start,double Seconds)? next=null;
            foreach(var track in session.Tracks)for(int n=0;n*30<indexes[track.Id].Available-.02;n++) {
                double start=n*30,seconds=Math.Min(30,indexes[track.Id].Available-start);
                if(Window(vault,job,track.Id,n)!=null){completed++;processed+=seconds;}
                else next??=(track.Id,n,start,seconds);
            }
            if(next is {} work) {
                // Snapshot pins the runtime/model/settings for the whole job, including after restart.
                var modelInfo=new FileInfo(job.Install.Model);
                if(testDecoder==null && (preparation.Install==null || modelInfo.Length!=preparation.ModelLength || modelInfo.LastWriteTimeUtc.Ticks!=preparation.ModelModified)) {
                    preparation.Install=await LoadJob(job,windowCancellation.Token);preparation.ModelLength=modelInfo.Length;preparation.ModelModified=modelInfo.LastWriteTimeUtc.Ticks;
                }
                var install=testDecoder==null?preparation.Install!:job.Install;
                var audio=indexes[work.Track].Render(work.Start,work.Seconds);
                string audioHash=Convert.ToHexString(SHA256.HashData(audio.Wav)).ToLowerInvariant();
                try {
                    var decoded=testDecoder==null?await Transcription.Decode(install,audio,work.Track,windowCancellation.Token,background:true):await testDecoder(audio,work.Track,windowCancellation.Token);
                    windowCancellation.Token.ThrowIfCancellationRequested();
                    var checkpoint=new RefinementWindow(job.Id,job.SourceManifestHash,install.ModelSha256,work.Track,work.Index,work.Start,work.Seconds,audioHash,decoded);
                    using(var gate=Lock(vault)) {
                        if(Read(vault,job.Id).State is "paused" or "cancelled")return false;
                        vault.Write(WindowPath(job,work.Track,work.Index),checkpoint);
                    }
                }finally {CryptographicOperations.ZeroMemory(audio.Wav);}
                Status(vault,job,"queued","Window saved encrypted; remaining work will run when idle.",completed+1,processed+work.Seconds,total);
                health?.Progress();
                return true;
            }
            windowCancellation.Token.ThrowIfCancellationRequested();
            var tracks=session.Tracks.Select(track=> {
                var parts=Enumerable.Range(0,(int)Math.Ceiling((indexes[track.Id].Available-.02)/30)).Select(n=>Window(vault,job,track.Id,n)!).ToArray();
                var segments=parts.SelectMany(p=>p.Transcript.Segments.Select(s=>s with {Start=s.Start+p.Start,End=s.End+p.Start})).ToArray();
                return new TrackTranscript(track.Id,segments.Length>0?"transcribed":"no-text",indexes[track.Id].Available,"Opportunistic local refinement. Review wording against the audio.",segments,parts.Sum(p=>p.Transcript.RejectedSegments));
            }).ToArray();
            string revisionPath=$"sessions/{job.SessionId}/transcript-{job.CreatedUtc:yyyyMMddTHHmmssfffffffZ}-{job.RevisionId}.snt";
            using(var gate=Lock(vault)) {
                if(Read(vault,job.Id).State is "paused" or "cancelled")return false;
                if(!File.Exists(Path.Combine(vault.Root,revisionPath)))vault.Write(revisionPath,new TranscriptRevision(1,job.RevisionId,job.SessionId,DateTimeOffset.UtcNow,$"whisper.cpp {job.Install.RuntimeBuild} CUDA",job.Install.ModelSha256,job.Install.ModelRevision,"en",tracks,"refined",job.Model,job.ParentRevisionId,30,job.SourceManifestHash,job.Install.BeamSize,job.Install.BestOf));
                Transcription.SelectIfCurrent(vault,job.SessionId,job.ParentRevisionId,job.RevisionId);
                Save(vault,job with {State="completed",Notice="Refinement saved as a new encrypted version. Earlier versions are retained.",UpdatedUtc=DateTimeOffset.UtcNow,CompletedWindows=completed,ProcessedSeconds=total,TotalSeconds=total});
                vault.Replace($"sessions/{job.SessionId}/refinement.snj",new RefinementStatus("complete",job.Model,total,total,DateTimeOffset.UtcNow,"Queued refinement saved. Earlier versions are retained."));
            }
            health?.Progress();
            return true;
        }catch(OperationCanceledException)when(windowCancellation.IsCancellationRequested) {
            var saved=TryRead(vault,job.Id);
            if(saved==null){IsolatedStatus(vault,job);health?.Activity("waiting","A damaged in-progress queue record was isolated.");return false;}
            health?.Activity("waiting",yieldReason ?? "Worker is stopping; saved work is retained.");
            Status(vault,job,"deferred",yieldReason ?? "Worker stopped; saved windows will resume next time.",saved.CompletedWindows,saved.ProcessedSeconds,saved.TotalSeconds);return false;
        }catch(Exception ex)when(ex is IOException or InvalidDataException or CryptographicException or JsonException or InvalidOperationException or ArgumentException) {
            var saved=TryRead(vault,job.Id);
            if(saved==null){IsolatedStatus(vault,job);health?.Activity("waiting","A damaged in-progress queue record was isolated.");return false;}
            health?.JobFailure();
            Status(vault,job,"failed","Refinement could not finish. Original audio, live text, and saved windows are retained; check the model/source before resuming.",saved.CompletedWindows,saved.ProcessedSeconds,saved.TotalSeconds);return false;
        }finally {monitorStop.Cancel();await monitor;}
    }
    static void IsolatedStatus(Vault vault,RefinementJob trustedJob)
    {
        // Called only while this step still owns inference. Preserve the damaged job bytes;
        // update its separate derived display state so it cannot appear to run forever.
        vault.Replace($"sessions/{trustedJob.SessionId}/refinement.snj",new RefinementStatus("isolated",trustedJob.Model,trustedJob.ProcessedSeconds,trustedJob.TotalSeconds,DateTimeOffset.UtcNow,"The queue record became unreadable and was isolated. Earlier text and audio are retained."));
    }
    static async Task<EngineInstall> LoadJob(RefinementJob job,CancellationToken token)
    {
        var install=job.Install;
        if(!File.Exists(install.Executable) || install.BeamSize is <1 or >5 || install.BestOf is <1 or >5)throw new InvalidDataException("Invalid queued model runtime.");
        await using var model=File.OpenRead(install.Model);
        if(!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(model,token)),install.ModelSha256,StringComparison.OrdinalIgnoreCase))throw new CryptographicException("Queued model checksum mismatch.");return install;
    }
    public static async Task Run(Vault vault,CancellationToken token,Func<CancellationToken,Task<bool>>? testCycle=null)
    {
        FileStream? acquiredHost=null;
        while(acquiredHost==null && !token.IsCancellationRequested) {
            acquiredHost=InferencePriority.TryAcquire(vault,"refinement-host.lock");
            if(acquiredHost==null){try{await Task.Delay(2000,token);}catch(OperationCanceledException)when(token.IsCancellationRequested){return;}}
        }
        if(acquiredHost==null)return;
        using var host=acquiredHost;
        await using var health=new QueueWorkerReporter(vault);
        using var environment=new IdleEnvironment();
        IdleSample Resources() {
            var sample=environment.Read(includeGpu:false);var policy=Policy(vault);
            if(policy.Enabled && sample.InputIdleSeconds>=policy.IdleSeconds && (!policy.RequireAcPower || sample.OnAcPower==true) && !InferencePriority.ForegroundActive(vault))return sample with {Gpu=environment.ReadGpu()};
            // Avoid waking a laptop's discrete GPU while the owner is active or on battery.
            environment.ReleaseGpu();return sample;
        }
        var preparation=new RefinementPreparation();
        while(!token.IsCancellationRequested) {
            try {bool advanced=testCycle==null?await Step(vault,Resources,token,preparation:preparation,health:health):await testCycle(token);health.CycleFinished();if(!advanced)preparation.Clear();await Task.Delay(advanced?100:2000,token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested) {break;}
            catch(Exception ex)when(ex is not OutOfMemoryException and not AccessViolationException){preparation.Clear();health.Fault();try{await Task.Delay(5000,token);}catch(OperationCanceledException)when(token.IsCancellationRequested){break;}}
        }
    }
}
