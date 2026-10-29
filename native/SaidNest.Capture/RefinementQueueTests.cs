using System.Text.Json;

namespace SaidNest.Capture;

static class RefinementQueueTests
{
    static void Check(bool condition,string message){if(!condition)throw new Exception("Queue check failed: "+message);}
    public static async Task Run(string root)
    {
        string folder=Path.Combine(root,"queue-"+Guid.NewGuid().ToString("N"));
        using var vault=Vault.Create(folder,"Synthetic queue recovery passphrase");
        var session=new SessionInfo(1,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,ChunkPump.QpcNow(),System.Diagnostics.Stopwatch.Frequency,"recording","fixture",[new("mic","fixture","Synthetic input",48000,1,32,"ieee-float")]);
        var pump=new ChunkPump(vault,session);
        using var feedDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for(int second=0;second<61;second++) {
            Check(pump.Offer("mic",new byte[192000],second*48000,session.StartQpc100ns+second*10_000_000L,0),"fixture accepts generated audio");
            // Keep the durable fixture below the recorder's bounded queue budget.
            while(pump.QueuedBytes>1024*1024)await Task.Delay(5,feedDeadline.Token);
        }
        await pump.Finish("synthetic queue fixture",true);
        string profile=Path.Combine(folder,"install.json");
        File.WriteAllText(profile,JsonSerializer.Serialize(new EngineInstall("fixture","fixture","fixture","fixture","fixture","large-v3"),Vault.Json));
        // Install() maps large-v3 relative to the tiny profile location.
        File.Copy(profile,Path.Combine(folder,"install-large-v3.json"));
        var job=RefinementQueue.Enqueue(vault,session.Id,profile,"large-v3");
        Check(RefinementQueue.Enqueue(vault,session.Id,profile,"large-v3").Id==job.Id,"enqueue is idempotent");
        IdleSample sample=new(0,.01,true);
        int decoded=0;
        Task<TrackTranscript> Decode(RenderedAudio audio,string track,CancellationToken token) {token.ThrowIfCancellationRequested();decoded++;return Task.FromResult(new TrackTranscript(track,"transcribed",audio.DurationSeconds,"Synthetic queued decoder",[new(track,0,audio.DurationSeconds,"Synthetic checkpoint words")]));}
        Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode) && decoded==0,"active user defers work");
        sample=new(300,.9,true);Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode),"busy CPU defers admission");
        sample=new(300,.01,false);Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode),"battery defers work");
        sample=new(300,null,true);Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode),"unknown resource status fails closed");
        sample=new(300,.01,true);
        using(var priority=new ForegroundIntent(vault))Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode),"foreground intent blocks admission");
        using(var lease=InferencePriority.TryAcquire(vault,"refinement-worker.lock"))Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode),"only one queue worker runs");
        Check(await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode) && decoded==1,"first window checkpoints");
        Check(RefinementQueue.Read(vault,job.Id).ProcessedSeconds==30,"durable checkpoint progress");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<TrackTranscript> Slow(RenderedAudio audio,string track,CancellationToken token) {entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return await Decode(audio,track,token);}
        var busy=RefinementQueue.Step(vault,()=>sample,testDecoder:Slow);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newSession=session with {Id=Guid.NewGuid().ToString("N"),StartQpc100ns=ChunkPump.QpcNow()};
        var newCapture=new ChunkPump(vault,newSession);
        var live=new LiveTranscription(vault,newSession,"fixture",Decode);live.Start();
        for(int n=0;n<4;n++)Check(newCapture.Offer("mic",new byte[192000],n*48000,newSession.StartQpc100ns+n*10_000_000L,0),"new capture accepts audio immediately");
        Check(!await busy.WaitAsync(TimeSpan.FromSeconds(5)) && RefinementQueue.Read(vault,job.Id).CompletedWindows==1,"recording/live cancels unfinished background window");
        using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while(!File.Exists(Path.Combine(vault.SessionPath(newSession.Id),"live-mic-00000000-draft.snt")))await Task.Delay(20,deadline.Token);
        await newCapture.Finish("new capture preempts refinement",true);await live.Finish();
        Check(live.Status.State=="complete" && Archive.Inspect(vault,newSession.Id).VerifiedFrames==192000,"live text succeeds and new recording loses no frames during preemption");
        decoded=1;
        vault.Replace($"refinement-queue/job-{job.Id}.snq",RefinementQueue.Read(vault,job.Id) with {State="running",CompletedWindows=0,ProcessedSeconds=0});
        // Reopen from disk with no worker context: completed windows must not be decoded again.
        using(var reopened=Vault.Open(folder))Check(await RefinementQueue.Step(reopened,()=>sample,testDecoder:Decode) && decoded==2,"restart resumes the next window");
        RefinementQueue.Control(vault,job.Id,"pause");Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode),"owner pause is durable");
        RefinementQueue.Control(vault,job.Id,"resume");
        entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var returningUser=RefinementQueue.Step(vault,()=>sample,testDecoder:Slow);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sample=new(0,.01,true);Check(!await returningUser.WaitAsync(TimeSpan.FromSeconds(5)) && decoded==2,"user input cancels unfinished window");
        sample=new(300,.01,true);
        Check(await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode) && decoded==3,"tail window resumes without duplicate words");
        var ownerVersion=new TranscriptRevision(1,Guid.NewGuid().ToString("N"),session.Id,DateTimeOffset.UtcNow,"synthetic owner choice","fixture","fixture","en",[],Kind:"live",ModelName:"tiny.en");
        vault.Write($"sessions/{session.Id}/transcript-{ownerVersion.CreatedUtc:yyyyMMddTHHmmssfffffffZ}-{ownerVersion.Id}.snt",ownerVersion);Transcription.Select(vault,session.Id,ownerVersion.Id);
        Check(await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode),"completed checkpoints assemble a revision");
        var finished=RefinementQueue.Read(vault,job.Id);
        Check(finished.State=="completed" && finished.CompletedWindows==3 && finished.ProcessedSeconds==61,"job completion survives reload");
        var revision=Transcription.Revisions(vault,session.Id).Single(r=>r.Id==job.RevisionId);
        Check(Transcription.Latest(vault,session.Id)!.Id==ownerVersion.Id,"background completion preserves a newer owner selection");
        Check(revision.Id==job.RevisionId && revision.Tracks.Single().Segments.Select(s=>s.Start).SequenceEqual(new double[]{0,30,60}),"checkpoint order and times");
        Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode) && Transcription.Revisions(vault,session.Id).Length==2,"completion is idempotent");
        vault.Replace($"refinement-queue/job-{job.Id}.snq",finished with {State="running",CompletedWindows=0,ProcessedSeconds=0});
        Check(await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode) && decoded==3 && Transcription.Revisions(vault,session.Id).Length==2,"interruption after revision write commits no duplicate revision");
        Check(Directory.GetFiles(Path.Combine(folder,"refinement-queue",job.Id),"*.snq").All(path=>!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("Synthetic checkpoint words",StringComparison.Ordinal)),"checkpoint words are encrypted");
        var beforeFailure=Transcription.Latest(vault,session.Id)!.Id;
        File.WriteAllText(Path.Combine(folder,"install-large-v3.json"),JsonSerializer.Serialize(job.Install with {ModelRevision="second synthetic model revision"},Vault.Json));
        var failing=RefinementQueue.Enqueue(vault,session.Id,profile,"large-v3");
        Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:(_,_,_)=>throw new IOException("Synthetic model failure")),"model failure stops only its job");
        Check(RefinementQueue.Read(vault,failing.Id).State=="failed" && Transcription.Latest(vault,session.Id)!.Id==beforeFailure && Archive.Inspect(vault,session.Id).State=="complete","failed job preserves previous text and raw audio");
        RefinementQueue.Control(vault,failing.Id,"resume");RefinementQueue.Control(vault,failing.Id,"cancel");
        Check(!await RefinementQueue.Step(vault,()=>sample,testDecoder:Decode),"cancelled jobs never run");
        Console.WriteLine("Passed: idle/CPU/power admission, foreground preemption, input cancellation, encrypted checkpoints, restart resume, pause controls, singleton work, idempotent enqueue and completion.");
    }
    public static async Task CudaSmoke(string root,string profile)
    {
        using var vault=Vault.Create(Path.Combine(root,"cuda-queue-"+Guid.NewGuid().ToString("N")),"Synthetic CUDA queue recovery passphrase");
        string session=await IntegrationTests.Fixture(vault);
        var job=RefinementQueue.Enqueue(vault,session,profile,"large-v3");
        var preparation=new RefinementPreparation();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while(RefinementQueue.Read(vault,job.Id).State!="completed") {
            Check(await RefinementQueue.Step(vault,()=>new IdleSample(300,0,true),deadline.Token,preparation:preparation),"CUDA synthetic queue advances");
        }
        var finished=RefinementQueue.Read(vault,job.Id);
        Check(finished.CompletedWindows==2 && Transcription.Revisions(vault,session).Any(r=>r.Id==finished.RevisionId),"CUDA checkpoints and revision commit");
        Console.WriteLine(JsonSerializer.Serialize(new {test="Synthetic generated tone / silence only; controlled idle inputs, not real idle qualification",model=finished.Model,finished.State,finished.CompletedWindows,finished.ProcessedSeconds,plaintextAudioWritten=false},Vault.Json));
    }
}
