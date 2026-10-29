using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SaidNest.Capture;

sealed record LocalServerHandle(WebApplication App, string Token, string Origin);
sealed record RefineRequest(string Model);
sealed record RevisionRequest(string Id);
sealed record ReferenceRequest(string Text,bool Verified);
sealed record QueueControl(string Action);
sealed record ServiceIdentity(int Pid,string StartTicks);

static class LocalServer
{
    public static LocalServerHandle Create(Vault vault, string publicRoot, string installFile, int port, bool fixture = false)
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("Select a port from 1024–65535.");
        string origin = $"http://127.0.0.1:{port}";
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] expected = Encoding.ASCII.GetBytes("Bearer " + token);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(LocalServer).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => { server.Listen(IPAddress.Loopback, port); server.Limits.MaxRequestBodySize = 32768; });
        var app = builder.Build();
        var jobs = new SemaphoreSlim(1, 1);
        var audioGate = new SemaphoreSlim(1, 1);
        Task? refinementWorker=null;
        // Test fixtures never run real background inference or inspect owner activity.
        if(!fixture)app.Lifetime.ApplicationStarted.Register(()=>refinementWorker=Task.Run(()=>RefinementQueue.Run(vault,app.Lifetime.ApplicationStopping)));
        app.Lifetime.ApplicationStopped.Register(()=>refinementWorker?.GetAwaiter().GetResult());
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self' blob:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            if (context.Request.Host.Value != $"127.0.0.1:{port}" || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            { context.Response.StatusCode = 400; return; }
            string requestOrigin = context.Request.Headers.Origin.ToString();
            if ((requestOrigin.Length > 0 && requestOrigin != origin) || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
            { context.Response.StatusCode = 403; return; }
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                byte[] authorization = Encoding.ASCII.GetBytes(context.Request.Headers.Authorization.ToString());
                if (!CryptographicOperations.FixedTimeEquals(expected, authorization)) { context.Response.StatusCode = 401; return; }
            }
            try { await next(context); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException or InvalidOperationException or OperationCanceledException or ArgumentException)
            {
                if (!context.Response.HasStarted)
                { context.Response.StatusCode = 422; await context.Response.WriteAsJsonAsync(new { error = "The local operation could not finish. Saved audio is unchanged; inspect the recording before retrying." }); }
            }
        });

        object Summary(string id)
        {
            bool active = Archive.IsRecording(vault, id);
            var inspection = active ? new Inspection(id, "recording", 0, 0, 0, []) : Archive.Inspect(vault, id);
            if (inspection.State == "corrupt") return new { id, state = "corrupt", error = "Archive verification failed." };
            var session = Archive.ReadSession(vault, id);
            var diagnostic = inspection.State == "recording" ? null : Diagnostics.Analyze(vault, id);
            return new { id, state = inspection.State, session.StartedUtc, tracks = session.Tracks.Select(track => track.Id).ToArray(),
                durationSeconds = diagnostic?.WallSeconds ?? Math.Max(0, (ChunkPump.QpcNow() - session.StartQpc100ns) / 10_000_000.0), events = diagnostic?.Events ?? [],
                diagnosticFlags = diagnostic?.Tracks.SelectMany(track => track.Issues).ToArray() ?? [],
                live = LiveTranscription.ReadStatus(vault, id), refinement=Transcription.ReadRefinement(vault,id), queueJob=RefinementQueue.Jobs(vault).Where(j=>j.SessionId==id).LastOrDefault() is {} queueJob?RefinementQueue.Summary(queueJob):null, captureConditions=CaptureConditions.Read(vault,id), transcript = Transcription.Latest(vault, id) };
        }
        app.MapGet("/api/local/health", () => Results.Json(new { local = true, fixture, model = "Local CUDA profiles, selected per job (experimental)", language = "en" }));
        app.MapGet("/api/local/models",()=>Results.Json(ModelProfiles.Available(installFile)));
        app.MapGet("/api/local/refinement-queue",()=>Results.Json(new {policy=RefinementQueue.Policy(vault),jobs=RefinementQueue.Jobs(vault).Select(RefinementQueue.Summary)}));
        app.MapPut("/api/local/refinement-queue/policy",async(HttpRequest request)=> {
            var policy=await request.ReadFromJsonAsync<RefinementPolicy>() ?? throw new ArgumentException("Select queue preferences.");
            RefinementQueue.SetPolicy(vault,policy);return Results.Json(policy);
        });
        app.MapPost("/api/local/refinement-queue/{jobId}",async(string jobId,HttpRequest request)=> {
            var control=await request.ReadFromJsonAsync<QueueControl>() ?? throw new ArgumentException("Select a queue action.");
            return Results.Json(RefinementQueue.Summary(RefinementQueue.Control(vault,jobId,control.Action)));
        });
        app.MapPost("/api/local/sessions/{id}/queue-refinement",async(string id,HttpRequest request)=> {
            var selection=await request.ReadFromJsonAsync<RefineRequest>() ?? throw new ArgumentException("Select a model.");
            var job=RefinementQueue.Enqueue(vault,id,installFile,selection.Model);return Results.Json(RefinementQueue.Summary(job),statusCode:202);
        });
        app.MapGet("/api/local/sessions/{id}/revisions",(string id)=>Results.Json(Transcription.Revisions(vault,id).Select(r=>new {r.Id,r.CreatedUtc,r.Kind,model=Transcription.Name(r),passages=r.Tracks.Sum(t=>t.Segments.Length),r.ParentRevisionId,r.WindowSeconds})));
        app.MapPost("/api/local/sessions/{id}/revision",async(string id,HttpRequest request)=> {
            if(Archive.IsRecording(vault,id)) return Results.Conflict();
            var selection=await request.ReadFromJsonAsync<RevisionRequest>() ?? throw new ArgumentException("Select a revision.");
            Transcription.Select(vault,id,selection.Id); return Results.Json(Summary(id));
        });
        app.MapPost("/api/local/sessions/{id}/refine",async(string id,HttpRequest request)=> {
            if(ModelProfiles.Busy(vault)) return Results.Conflict();
            var selection=await request.ReadFromJsonAsync<RefineRequest>() ?? throw new ArgumentException("Select a local model.");
            string profile=ModelProfiles.Install(installFile,selection.Model);
            if(!await jobs.WaitAsync(0)) return Results.Conflict();
            try { await Transcription.Run(vault,id,profile,app.Lifetime.ApplicationStopping); return Results.Json(Summary(id)); }
            finally { jobs.Release(); }
        });
        app.MapGet("/api/local/sessions/{id}/evaluation",(string id)=>Results.Json(Evaluation.Review(vault,id)));
        app.MapPut("/api/local/sessions/{id}/evaluation/{caseId}",async(string id,string caseId,HttpRequest request)=> {
            var reference=await request.ReadFromJsonAsync<ReferenceRequest>() ?? throw new ArgumentException("Enter a reference.");
            Evaluation.SaveReference(vault,id,caseId,reference.Text,reference.Verified); return Results.Json(Evaluation.Review(vault,id));
        });
        app.MapGet("/api/local/sessions/{id}/live", (string id) => Results.Json(LiveTranscription.ReadStatus(vault, id)));
        app.MapGet("/api/local/sessions/{id}/progress",(string id)=>Results.Json(new {refinement=Transcription.ReadRefinement(vault,id),queueJob=RefinementQueue.Jobs(vault).Where(j=>j.SessionId==id).LastOrDefault() is {} queueJob?RefinementQueue.Summary(queueJob):null,transcript=Transcription.Latest(vault,id)}));
        app.MapPost("/api/local/sessions/{id}/stop", (string id) => {
            if (!Archive.IsRecording(vault, id)) return Results.Conflict();
            if (!File.Exists(Path.Combine(vault.SessionPath(id), "stop-request.sne"))) vault.Write($"sessions/{id}/stop-request.sne", new { kind = "stop-request", qpc100ns = ChunkPump.QpcNow() });
            return Results.Accepted();
        });
        app.MapGet("/api/local/sessions", () => Results.Json(Directory.GetDirectories(Path.Combine(vault.Root, "sessions"))
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)).OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Take(100).Select(path => Summary(Path.GetFileName(path))).ToArray()));
        app.MapGet("/api/local/sessions/{id}", (string id) => Results.Json(Summary(id)));
        app.MapPost("/api/local/sessions/{id}/transcribe", async (string id) =>
        {
            if(ModelProfiles.Busy(vault)) return Results.Conflict();
            _ = vault.SessionPath(id);
            if (!await jobs.WaitAsync(0)) return Results.Conflict(new { error = "A local transcription job is already running." });
            try { await Transcription.Run(vault, id, installFile, app.Lifetime.ApplicationStopping); return Results.Json(Summary(id)); }
            finally { jobs.Release(); }
        });
        app.MapGet("/api/local/sessions/{id}/audio/{track}", async (string id, string track, double? start, double? seconds) =>
        {
            if (track is not ("mic" or "output")) return Results.BadRequest();
            if (!await audioGate.WaitAsync(0)) return Results.Conflict();
            try {
                if (start == null && seconds == null) return Results.Bytes(TimelineAudio.Render(vault, id, track).Wav, "audio/wav");
                if (!double.IsFinite(start ?? 0) || (start ?? 0)<0 || !double.IsFinite(seconds ?? 60) || (seconds ?? 60)<=0 || (seconds ?? 60)>60) throw new ArgumentException("Select a valid playback window of up to sixty seconds.");
                var inspection = Archive.Inspect(vault, id);
                if (inspection.State is "corrupt" or "recording") return Results.Conflict();
                var session = Archive.ReadSession(vault, id);
                var windows = new AudioWindows(vault, session, session.Tracks.Single(t => t.Id == track)); windows.Poll();
                double begin = start ?? 0, duration = Math.Min(seconds ?? 60, windows.Available - begin);
                return Results.Bytes(windows.Render(begin, duration).Wav, "audio/wav");
            }
            finally { audioGate.Release(); }
        });
        app.MapGet("/api/local/sessions/{id}/diagnostics", (string id) => Results.Json(Diagnostics.Analyze(vault, id)));
        var assets = new Dictionary<string, (string File, string Mime)> {
            ["/styles.css"] = ("styles.css", "text/css"), ["/local.js"] = ("local.js", "application/javascript"), ["/local-quality.js"]=("local-quality.js","application/javascript"), ["/favicon.svg"] = ("favicon.svg", "image/svg+xml") };
        foreach (var asset in assets)
            app.MapGet(asset.Key, async () => Results.Bytes(await File.ReadAllBytesAsync(Path.Combine(publicRoot, asset.Value.File)), asset.Value.Mime));
        async Task<IResult> Page()
        {
            string page = await File.ReadAllTextAsync(Path.Combine(publicRoot, "app", "index.html"));
            page = page.Replace("src=\"/app.js\"", "src=\"/local.js\"")
                .Replace("<body class=\"app-page\">", $"<body class=\"app-page\" data-local=\"true\" data-fixture=\"{fixture.ToString().ToLowerInvariant()}\">");
            return Results.Text(page, "text/html; charset=utf-8");
        }
        app.MapGet("/", Page); app.MapGet("/app/", Page);
        return new(app, token, origin);
    }

    public static async Task Run(Vault vault, string publicRoot, string install, int port, bool openBrowser, bool fixture = false)
    {
        using var serviceLease=InferencePriority.TryAcquire(vault,"local-service.lock") ?? throw new InvalidOperationException("A local service is already running for this vault.");
        var server = Create(vault, publicRoot, install, port, fixture);
        await server.App.StartAsync();
        using var currentProcess=Process.GetCurrentProcess();
        string processMarker=Path.Combine(vault.Root,"service-process.json");
        var identity=new ServiceIdentity(Environment.ProcessId,currentProcess.StartTime.ToUniversalTime().Ticks.ToString());
        // Browser bootstrap credentials stay out of console logs and query strings.
        string url = server.Origin + "/app/#key=" + server.Token;
        vault.Write($"ui-{Guid.NewGuid():N}.snu", new { url });
        Vault.AtomicWrite(processMarker,JsonSerializer.SerializeToUtf8Bytes(identity,Vault.Json),replace:true);
        string stopMarker=Path.Combine(vault.Root,"service-stop.json");
        var stopWatcher=Task.Run(async()=> {
            while(!server.App.Lifetime.ApplicationStopping.IsCancellationRequested) {
                try {
                    if(File.Exists(stopMarker) && JsonSerializer.Deserialize<ServiceIdentity>(File.ReadAllBytes(stopMarker),Vault.Json)==identity){server.App.Lifetime.StopApplication();return;}
                    await Task.Delay(200,server.App.Lifetime.ApplicationStopping);
                }catch(OperationCanceledException)when(server.App.Lifetime.ApplicationStopping.IsCancellationRequested){return;}
                catch(Exception ex)when(ex is IOException or JsonException){await Task.Delay(200);}
            }
        });
        if (openBrowser) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Console.WriteLine($"SaidNest local timeline is running on {server.Origin}. Private access opened locally; press Ctrl+C to stop.");
        await server.App.WaitForShutdownAsync();
        await stopWatcher;
        await server.App.DisposeAsync();
        File.Delete(processMarker);
        if(File.Exists(stopMarker))File.Delete(stopMarker);
    }
}
