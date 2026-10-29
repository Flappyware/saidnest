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

        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var server = LocalServer.Create(vault, Path.GetFullPath("public"), ".saidnest/inference/install.json", port, fixture: true);
        await server.App.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(server.Origin) };
            Check((await client.GetAsync("/api/local/sessions")).StatusCode == HttpStatusCode.Unauthorized, "private API rejects missing token");
            Check((await client.GetAsync($"/api/local/sessions/{id}/audio/mic")).StatusCode == HttpStatusCode.Unauthorized, "audio rejects missing token");
            Check((await client.GetAsync($"/api/local/sessions/{id}/live")).StatusCode == HttpStatusCode.Unauthorized,"live progress rejects missing token");
            Check((await client.PostAsync($"/api/local/sessions/{id}/stop",null)).StatusCode == HttpStatusCode.Unauthorized,"recording controls reject missing token");
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
        }
        finally { await server.App.StopAsync(); await server.App.DisposeAsync(); }
        Console.WriteLine("Passed: pause-aligned WAV, pause diagnostics, bounded transcript timestamps, private API/audio authentication, no-store responses, cross-origin denial, Host validation, and static vault-file denial. Synthetic fixtures only.");
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
        await Until(()=>File.Exists(Path.Combine(vault.SessionPath(id),"live-output-00000000-draft.snt")));
        Check(Archive.IsRecording(vault,id),"draft appears before capture stops");
        var draft = LiveTranscription.ReadTranscript(vault,id)!;
        Check(draft.Tracks.All(t=>t.State=="provisional" && t.Segments.Length==1),"separate provisional sources");
        for(int second=4;second<9;second++) Offer(second);
        await Until(()=>File.Exists(Path.Combine(vault.SessionPath(id),"live-output-00000000-final.snt")));
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
             new("output", "too-quiet", 4, "Synthetic silent output. Audio is retained.", [])]);
        vault.Write($"sessions/{id}/transcript-{revision.CreatedUtc:yyyyMMddTHHmmssfffffffZ}-{revision.Id}.snt", revision);
        return id;
    }
}
