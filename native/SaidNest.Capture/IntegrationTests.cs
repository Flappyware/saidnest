using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SaidNest.Capture;

static class IntegrationTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Integration check failed: " + message); }
    public static async Task Run(string parent)
    {
        string root = Path.Combine(Path.GetFullPath(parent), Guid.NewGuid().ToString("N"));
        using var vault = Vault.Create(root, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        string id = await Fixture(vault);
        var audio = TimelineAudio.Render(vault, id, "mic");
        Check(Math.Abs(audio.DurationSeconds - 4) < 0.001, "pause must not compress the timeline");
        Check(audio.InsertedSilenceFrames == 96000, "two seconds of pause silence");
        Check(audio.Wav.AsSpan(44 + 96000, 192000).IndexOfAnyExcept((byte)0) < 0, "pause PCM bytes are silent");
        var diagnostic = Diagnostics.Analyze(vault, id);
        Check(diagnostic.Tracks[0].Issues.Any(issue => issue.Context == "manual-pause-resume"), "gap correlates to pause/resume");
        var parsed = Transcription.Parse("[00:00:00.000 --> 00:00:01.000] fixture words\n[00:00:01.000 --> 00:00:20.000] invalid tail\n[00:00:02.000 --> 00:00:03.000] [BLANK_AUDIO]\n", "mic", 4, out int rejected);
        Check(parsed.Length == 1 && rejected == 1, "non-speech markers and invalid timestamps are not committed");
        var windowIndex = new AudioWindows(vault, Archive.ReadSession(vault, id), Archive.ReadSession(vault, id).Tracks[0]); windowIndex.Poll();
        var windowAudio = windowIndex.Render(1, 2);
        Check(windowAudio.Wav.AsSpan(44).IndexOfAnyExcept((byte)0) < 0, "bounded window preserves pause silence");
        Check(windowIndex.Render(3, 1).PeakDbfs > -41, "window after pause includes resumed audio");
        await CheckLive(vault);
        await CheckLiveFailure(vault);
        await CheckQuality(vault,id);
        await RefinementQueueTests.Run(Path.GetFullPath(root));

        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string queueProfile=Path.Combine(root,"install.json");
        File.WriteAllText(queueProfile,JsonSerializer.Serialize(new EngineInstall("fixture","fixture","fixture","fixture","fixture","large-v3"),Vault.Json));
        File.Copy(queueProfile,Path.Combine(root,"install-large-v3.json"));
        var server = LocalServer.Create(vault, Path.GetFullPath("public"), queueProfile, port, fixture: true);
        await server.App.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(server.Origin) };
            Check((await client.GetAsync("/api/local/sessions")).StatusCode == HttpStatusCode.Unauthorized, "private API rejects missing token");
            Check((await client.GetAsync($"/api/local/sessions/{id}/audio/mic")).StatusCode == HttpStatusCode.Unauthorized, "audio rejects missing token");
            Check((await client.GetAsync($"/api/local/sessions/{id}/live")).StatusCode == HttpStatusCode.Unauthorized,"live progress rejects missing token");
            Check((await client.PostAsync($"/api/local/sessions/{id}/stop",null)).StatusCode == HttpStatusCode.Unauthorized,"recording controls reject missing token");
            Check((await client.GetAsync($"/api/local/sessions/{id}/evaluation")).StatusCode==HttpStatusCode.Unauthorized,"model hypotheses and references require authorization");
            Check((await client.GetAsync("/api/local/refinement-queue")).StatusCode==HttpStatusCode.Unauthorized,"queue metadata requires authorization");
            Check((await client.PostAsync($"/api/local/sessions/{id}/queue-refinement",null)).StatusCode==HttpStatusCode.Unauthorized,"queue mutations require authorization");
            Check((await client.PutAsync($"/api/local/sessions/{id}/evaluation/unknown",new StringContent("{}",System.Text.Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.Unauthorized,"reference writes reject missing token");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
            var sessions = await client.GetAsync("/api/local/sessions");
            Check(sessions.IsSuccessStatusCode, "authorized archive listing");
            Check(sessions.Headers.CacheControl?.NoStore == true, "private metadata is not cached");
            using var json = JsonDocument.Parse(await sessions.Content.ReadAsStringAsync());
            Check(json.RootElement.GetArrayLength() == 3, "fixture archive isolation");
            var playback = await client.GetAsync($"/api/local/sessions/{id}/audio/mic");
            Check(playback.IsSuccessStatusCode && playback.Content.Headers.ContentType?.MediaType == "audio/wav", "authenticated playback bytes");
            Check((await playback.Content.ReadAsByteArrayAsync()).Length == audio.Wav.Length, "playback response length");
            var bounded=await client.GetAsync($"/api/local/sessions/{id}/audio/mic?start=1&seconds=2");
            var boundedBytes=await bounded.Content.ReadAsByteArrayAsync();
            Check(bounded.IsSuccessStatusCode && boundedBytes.Length==192044 && boundedBytes.AsSpan(44).IndexOfAnyExcept((byte)0)<0,"authorized bounded playback preserves pause");
            Check((await client.GetAsync($"/api/local/sessions/{id}/audio/mic?start=0&seconds=61")).StatusCode==HttpStatusCode.UnprocessableEntity,"oversized playback window rejected");
            using(var captureLease=new FileStream(Path.Combine(vault.SessionPath(id),"capture.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
                Check((await client.GetAsync($"/api/local/sessions/{id}/audio/mic?start=0&seconds=1")).StatusCode==HttpStatusCode.Conflict,"active capture cannot play its own loopback audio");
                Check((await client.PostAsync($"/api/local/sessions/{id}/stop",null)).StatusCode==HttpStatusCode.Accepted,"authorized stop enqueues encrypted request");
                Check(vault.Read($"sessions/{id}/stop-request.sne").Metadata.GetProperty("kind").GetString()=="stop-request","stop request is authenticated archive data");
            }
            using var foreign = new HttpRequestMessage(HttpMethod.Post, $"/api/local/sessions/{id}/transcribe");
            foreign.Headers.Add("Origin", "https://example.invalid");
            Check((await client.SendAsync(foreign)).StatusCode == HttpStatusCode.Forbidden, "cross-origin processing denied");
            using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/local/sessions"); rebound.Headers.Host = "attacker.invalid";
            Check((await client.SendAsync(rebound)).StatusCode == HttpStatusCode.BadRequest, "DNS rebinding host denied");
            Check((await client.GetAsync("/vault.json")).StatusCode == HttpStatusCode.NotFound, "vault files are never static assets");
            using var foreignReference=new HttpRequestMessage(HttpMethod.Put,$"/api/local/sessions/{id}/evaluation/not-a-case");foreignReference.Headers.Add("Origin","https://example.invalid");foreignReference.Content=new StringContent("{\"text\":\"fixture\",\"verified\":true}",System.Text.Encoding.UTF8,"application/json");
            Check((await client.SendAsync(foreignReference)).StatusCode==HttpStatusCode.Forbidden,"cross-origin reference modification denied");
            string caseId=Evaluation.ReadSet(vault,id).Cases[0].Id;
            var referenceResponse=await client.PutAsync($"/api/local/sessions/{id}/evaluation/{caseId}",new StringContent("{\"text\":\"Keep the recording and its words together.\",\"verified\":true}",System.Text.Encoding.UTF8,"application/json"));
            Check(referenceResponse.IsSuccessStatusCode,"owner can save a checked local reference");
            using var scores=JsonDocument.Parse(await referenceResponse.Content.ReadAsStringAsync());
            Check(scores.RootElement.GetProperty("summaries").EnumerateArray().All(s=>s.GetProperty("verifiedReferences").GetInt32()==1),"every compared model uses the same checked reference");
            Check(scores.RootElement.GetProperty("summaries").EnumerateArray().Single(s=>s.GetProperty("model").GetProperty("name").GetString()=="large-v3").GetProperty("wordErrorRate").GetDouble()==0,"reference scores the matching synthetic hypothesis");
            Evaluation.SaveReference(vault,id,caseId,"Keep the recording and its words together.",false);
            var queued=await client.PostAsync($"/api/local/sessions/{id}/queue-refinement",new StringContent("{\"model\":\"large-v3\"}",System.Text.Encoding.UTF8,"application/json"));
            Check(queued.StatusCode==HttpStatusCode.Accepted,"owner can enqueue without waiting for inference");
            using var queuedBody=JsonDocument.Parse(await queued.Content.ReadAsStringAsync());string queueId=queuedBody.RootElement.GetProperty("id").GetString()!;
            Check(!queuedBody.RootElement.TryGetProperty("install",out _),"queue summary does not expose executable paths or transcript words");
            Check((await client.PostAsync($"/api/local/refinement-queue/{queueId}",new StringContent("{\"action\":\"pause\"}",System.Text.Encoding.UTF8,"application/json"))).IsSuccessStatusCode,"owner can pause queued work");
            Check(RefinementQueue.Read(vault,queueId).State=="paused","pause is persisted");
            using var foreignQueue=new HttpRequestMessage(HttpMethod.Post,$"/api/local/refinement-queue/{queueId}");foreignQueue.Headers.Add("Origin","https://example.invalid");foreignQueue.Content=new StringContent("{\"action\":\"resume\"}",System.Text.Encoding.UTF8,"application/json");
            Check((await client.SendAsync(foreignQueue)).StatusCode==HttpStatusCode.Forbidden,"cross-origin queue mutations are denied");
            Check((await client.PostAsync($"/api/local/sessions/{id}/queue-refinement",new StringContent("{\"model\":\"../unknown\"}",System.Text.Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.UnprocessableEntity,"queue accepts provisioned profile names only");
        }
        finally { await server.App.StopAsync(); await server.App.DisposeAsync(); }
        await CheckServiceLifecycle(vault,queueProfile,port);
        Console.WriteLine("Passed: pause-aligned WAV, pause diagnostics, bounded transcript timestamps, private API/audio authentication, no-store responses, cross-origin denial, Host validation, and static vault-file denial. Synthetic fixtures only.");
    }

    static async Task CheckServiceLifecycle(Vault vault,string profile,int port)
    {
        System.Diagnostics.Process Start() {
            var info=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(typeof(IntegrationTests).Assembly.Location);
            foreach(string argument in new[]{"serve","--no-open","--vault",vault.Root,"--public",Path.GetFullPath("public"),"--install",profile,"--port",port.ToString()})info.ArgumentList.Add(argument);
            return System.Diagnostics.Process.Start(info)!;
        }
        using var service=Start();
        var output=service.StandardOutput.ReadToEndAsync();var error=service.StandardError.ReadToEndAsync();
        try {
            string ready=Path.Combine(vault.Root,"service-process.json");
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(!File.Exists(ready) && !service.HasExited)await Task.Delay(50,deadline.Token);
            Check(!service.HasExited && File.Exists(ready),"native ready marker is published");
            var identity=JsonSerializer.Deserialize<ServiceIdentity>(File.ReadAllBytes(ready),Vault.Json)!;
            Check(identity.Pid==service.Id && Directory.GetFiles(vault.Root,"ui-*.snu").Length>0,"ready marker follows encrypted browser bootstrap");
            using var client=new HttpClient();Check((await client.GetAsync($"http://127.0.0.1:{port}/api/local/health",deadline.Token)).StatusCode==HttpStatusCode.Unauthorized,"background listener remains authenticated");
            using(var duplicate=Start()) {
                await duplicate.WaitForExitAsync(deadline.Token);
                Check(duplicate.ExitCode!=0 && !service.HasExited,"duplicate native service cannot replace the owner");
            }
            Vault.AtomicWrite(Path.Combine(vault.Root,"service-stop.json"),JsonSerializer.SerializeToUtf8Bytes(identity with {StartTicks="0"},Vault.Json),replace:true);
            await Task.Delay(300,deadline.Token);Check(!service.HasExited,"stale shutdown request is ignored");
            Vault.AtomicWrite(Path.Combine(vault.Root,"service-stop.json"),JsonSerializer.SerializeToUtf8Bytes(identity,Vault.Json),replace:true);
            await service.WaitForExitAsync(deadline.Token);
            Check(service.ExitCode==0 && !File.Exists(ready),"identity-bound shutdown cancels the background worker and removes readiness");
            await output;await error;
            Console.WriteLine("Passed: native background readiness, authenticated listener, duplicate prevention, stale-stop rejection and graceful shutdown.");
        }finally {if(!service.HasExited){service.Kill(true);await service.WaitForExitAsync();}}
    }

    static async Task CheckQuality(Vault vault,string id)
    {
        Check(QualityMetrics.Score("Hello, WORLD!","hello world").WordErrorRate==0,"case/punctuation normalization");
        Check(QualityMetrics.Score("one two three","one four three").Edits==1,"substitution count");
        Check(QualityMetrics.Score("one two three","one three").Edits==1,"deletion count");
        Check(QualityMetrics.Score("one two three","one two four three").Edits==1,"insertion count");
        Check(QualityMetrics.Score("","invented").WordErrorRate==null,"silence has no WER denominator");
        Check(QualityMetrics.Score("DON’T café","don't café").Edits==0,"Unicode and apostrophe normalization");
        string sourceHash=vault.Read($"sessions/{id}/final.snm").Hash;
        CaptureConditions.ReportSpeakerBleed(vault,id);
        Check(CaptureConditions.Read(vault,id)!.SpeakerBleedReported && vault.Read($"sessions/{id}/final.snm").Hash==sourceHash,"speaker-bleed context preserves original audio manifest");
        var original=Transcription.Latest(vault,id)!;
        var refined=await Transcription.Run(vault,id,"fixture",testDecoder:(audio,track,_)=>Task.FromResult(new TrackTranscript(track,"transcribed",audio.DurationSeconds,"Synthetic model fixture",[new(track,0,audio.DurationSeconds,"Synthetic refined passage")])));
        Check(refined.ParentRevisionId==original.Id && refined.Kind=="refined" && refined.WindowSeconds==30,"refinement provenance and parent");
        Check(Transcription.Revisions(vault,id).Length==2 && Transcription.Latest(vault,id)!.Id==refined.Id,"refinement retains original and selects new version");
        Transcription.Select(vault,id,original.Id);Check(Transcription.Latest(vault,id)!.Id==original.Id,"owner can restore original live text");
        try { await Transcription.Run(vault,id,"fixture",testDecoder:(_,_,_)=>throw new IOException("Synthetic refinement failure")); throw new Exception("Failure was expected."); }
        catch(IOException) { }
        Check(Transcription.ReadRefinement(vault,id)!.State=="failed" && Transcription.Latest(vault,id)!.Id==original.Id,"failed refinement never replaces selected text");
        var set=Evaluation.ReadSet(vault,id); var item=set.Cases[0];
        Evaluation.SaveReference(vault,id,item.Id,"Keep the recording and its words together.",false);
        Check(Evaluation.Reference(vault,id,item.Id)!.Verified==false,"draft references remain unverified");
        Evaluation.SaveReference(vault,id,item.Id,"Keep the recording and its words together.",true);
        Check(Evaluation.Reference(vault,id,item.Id)!.Verified,"verified reference encrypted round trip");
        var score=QualityMetrics.Score(Evaluation.Reference(vault,id,item.Id)!.Text,"Keep the recording and its words together.");
        Check(score.WordErrorRate==0,"checked reference scores exact hypothesis");
        Evaluation.SaveReference(vault,id,item.Id,"Keep the recording and its words together.",false);
        var replay=await ReplayEvaluation.Run(vault,id,"fixture",testDecoder:(audio,track,_)=>Task.FromResult(new TrackTranscript(track,"transcribed",audio.DurationSeconds,"Synthetic replay fixture",[new(track,0,audio.DurationSeconds,"Synthetic replay words")])));
        Check(replay.State=="complete" && replay.Jobs==2 && replay.MissedDeadlines==0,"bounded two-source replay and final tail");
        Check(Transcription.Latest(vault,id)!.Id==original.Id,"replay never replaces owner-selected text automatically");
        Console.WriteLine("Passed: word-error normalization, silence scoring, encrypted reference lifecycle, refinement lineage, immutable versions, and original restoration.");
    }

    static async Task CheckLive(Vault vault)
    {
        string id = Guid.NewGuid().ToString("N");
        var session = new SessionInfo(1,id,DateTimeOffset.UtcNow,ChunkPump.QpcNow(),System.Diagnostics.Stopwatch.Frequency,"recording","live-local-english",[new("mic","fixture","Synthetic microphone",48000,1,32,"ieee-float"),new("output","fixture","Synthetic output",48000,2,32,"ieee-float")]);
        var pump = new ChunkPump(vault,session);
        var live = new LiveTranscription(vault,session,"unused",async (audio,track,token)=> {
            await Task.Delay(40,token);
            return new(track,"transcribed",audio.DurationSeconds,"Synthetic decoder fixture",[new(track,0,audio.DurationSeconds,"Synthetic live passage")]);
        });
        live.Start();
        void Offer(int second) {
            foreach(var track in session.Tracks) Check(pump.Offer(track.Id,new byte[48000*track.BlockAlign],second*48000,session.StartQpc100ns+second*10_000_000L,0),"capture remains independent of live decoder");
        }
        async Task Until(Func<bool> condition) {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(!condition()) await Task.Delay(20,deadline.Token);
        }
        for(int second=0;second<4;second++) Offer(second);
        // The writer may commit a source between another source's two polls.
        // Each source updates independently; one checkpoint does not imply the other exists yet.
        await Until(()=>new[]{"mic","output"}.All(track=>File.Exists(Path.Combine(vault.SessionPath(id),$"live-{track}-00000000-draft.snt"))));
        Check(Archive.IsRecording(vault,id),"draft appears before capture stops");
        var draft = LiveTranscription.ReadTranscript(vault,id)!;
        Check(draft.Tracks.All(t=>t.State=="provisional" && t.Segments.Length==1),"separate provisional sources");
        for(int second=4;second<9;second++) Offer(second);
        await Until(()=>new[]{"mic","output"}.All(track=>File.Exists(Path.Combine(vault.SessionPath(id),$"live-{track}-00000000-final.snt"))));
        var updated = LiveTranscription.ReadTranscript(vault,id)!;
        Check(updated.Tracks.All(t=>t.Segments.Length==1 && t.Segments[0].End==8),"final replaces its draft without duplication");
        await pump.Finish("synthetic live fixture",true); await live.Finish();
        Check(live.Status.State=="complete" && Archive.Inspect(vault,id).State=="complete","independent capture and inference completion");
        var final = Transcription.Latest(vault,id)!;
        Check(final.Tracks.All(t=>t.Segments.Length==2 && t.Segments[1].Start==8 && t.Segments[1].End==9),"final tail and source timestamps preserved");
        Console.WriteLine("Passed: live drafts before capture stops, independent durable capture, draft replacement, separate sources, final tail, bounded window pause alignment.");
    }

    static async Task CheckLiveFailure(Vault vault)
    {
        string id=Guid.NewGuid().ToString("N");
        var session=new SessionInfo(1,id,DateTimeOffset.UtcNow,ChunkPump.QpcNow(),System.Diagnostics.Stopwatch.Frequency,"recording","live-local-english",[new("mic","fixture","Synthetic input",48000,1,32,"ieee-float")]);
        var pump=new ChunkPump(vault,session);
        var live=new LiveTranscription(vault,session,"unused",(_,_,_)=>throw new IOException("Synthetic model failure")); live.Start();
        for(int second=0;second<4;second++) Check(pump.Offer("mic",new byte[192000],second*48000,session.StartQpc100ns+second*10_000_000L,0),"queue accepts synthetic audio");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(live.Status.State!="failed") await Task.Delay(20,deadline.Token);
        Check(Archive.IsRecording(vault,id),"model failure must not stop capture");
        Check(pump.Offer("mic",new byte[192000],4*48000,session.StartQpc100ns+4*10_000_000L,0),"capture continues after model failure");
        await pump.Finish("synthetic model failure isolation",true); await live.Finish();
        var inspection=Archive.Inspect(vault,id);
        Check(inspection.State=="complete" && inspection.VerifiedFrames==240000,"all audio survives failed inference");
        Console.WriteLine("Passed: failed live inference preserves ongoing capture and every committed source frame.");
    }

    public static async Task<string> Fixture(Vault vault)
    {
        string id = Guid.NewGuid().ToString("N");
        var session = new SessionInfo(1, id, new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), ChunkPump.QpcNow(), System.Diagnostics.Stopwatch.Frequency,
            "recording", "not-configured", [new("mic", "synthetic", "Synthetic microphone", 48000, 1, 32, "ieee-float"), new("output", "synthetic", "Synthetic output", 48000, 2, 32, "ieee-float")]);
        var pump = new ChunkPump(vault, session);
        foreach (var track in session.Tracks)
        {
            byte[] tone = new byte[48000 * track.BlockAlign];
            for (int frame = 0; frame < 48000; frame++)
                for (int channel = 0; channel < track.Channels; channel++) BitConverter.TryWriteBytes(tone.AsSpan(frame * track.BlockAlign + channel * 4, 4), track.Id == "mic" ? (float)(0.01 * Math.Sin(frame * .05)) : 0f);
            Check(pump.Offer(track.Id, tone, 0, session.StartQpc100ns, 1), "fixture first packet");
            Check(pump.Offer(track.Id, tone, 144000, session.StartQpc100ns + 30_000_000, 0), "fixture resumed packet");
        }
        await pump.Finish("synthetic integration fixture", true);
        string[] kinds = ["capture-started", "paused", "resumed", "capture-stopped"];
        int[] seconds = [0, 1, 3, 4];
        for (int index = 0; index < kinds.Length; index++) vault.Write($"sessions/{id}/event-{index:D8}.sne", new { version = 1, kind = kinds[index], qpc100ns = session.StartQpc100ns + seconds[index] * 10_000_000L });
        var revision = new TranscriptRevision(1, Guid.NewGuid().ToString("N"), id, DateTimeOffset.UtcNow, "synthetic integration fixture", "fixture", "fixture", "en",
            [new("mic", "transcribed", 4, "Synthetic test transcript paired with generated tone. This is interface test data, not speech-recognition evidence.",
                [new("mic", 0, .9, "Keep the recording and its words together."), new("mic", 3, 3.9, "The pause stays on the timeline. Copy the context you need.")]),
             new("output", "too-quiet", 4, "Synthetic silent output. Audio is retained.", [])],Kind:"live",ModelName:"tiny.en",WindowSeconds:8);
        vault.Write($"sessions/{id}/transcript-{revision.CreatedUtc:yyyyMMddTHHmmssfffffffZ}-{revision.Id}.snt", revision);
        string caseId=Guid.NewGuid().ToString("N");
        var set=new EvaluationSet(1,Guid.NewGuid().ToString("N"),id,vault.Read($"sessions/{id}/final.snm").Hash,DateTimeOffset.UtcNow,[new(caseId,"mic",0,4)]);
        vault.Write($"sessions/{id}/evaluation-set.snq",set);
        foreach(string model in new[]{"tiny.en","large-v3"}) {
            var text=model=="tiny.en"?"Keep a recording and its words together.":"Keep the recording and its words together.";
            var hypothesis=new TrackTranscript("mic","transcribed",4,"Synthetic comparison fixture",[new("mic",0,3.9,text)]);
            var run=new EvaluationRun(1,Guid.NewGuid().ToString("N"),set.Id,id,DateTimeOffset.UtcNow,"complete",new(model,"fixture","fixture","fixture",1,1),[new(caseId,"mic",0,8,model=="tiny.en"?.1:.4,"fixture",hypothesis)],[]);
            vault.Write($"sessions/{id}/evaluation-run-{run.Id}.snq",run);
        }
        return id;
    }
}
