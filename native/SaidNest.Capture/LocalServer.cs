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
        builder.WebHost.ConfigureKestrel(server => { server.Listen(IPAddress.Loopback, port); server.Limits.MaxRequestBodySize = 1024; });
        var app = builder.Build();
        var jobs = new SemaphoreSlim(1, 1);
        var audioGate = new SemaphoreSlim(1, 1);
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
            var inspection = Archive.Inspect(vault, id);
            if (inspection.State == "corrupt") return new { id, state = "corrupt", error = "Archive verification failed." };
            var session = Archive.ReadSession(vault, id);
            var diagnostic = inspection.State == "recording" ? null : Diagnostics.Analyze(vault, id);
            return new { id, state = inspection.State, session.StartedUtc, tracks = session.Tracks.Select(track => track.Id).ToArray(),
                durationSeconds = diagnostic?.WallSeconds ?? 0, events = diagnostic?.Events ?? [],
                diagnosticFlags = diagnostic?.Tracks.SelectMany(track => track.Issues).ToArray() ?? [],
                transcript = Transcription.Latest(vault, id) };
        }
        app.MapGet("/api/local/health", () => Results.Json(new { local = true, fixture, model = "tiny.en (experimental)", language = "en" }));
        app.MapGet("/api/local/sessions", () => Results.Json(Directory.GetDirectories(Path.Combine(vault.Root, "sessions"))
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)).OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Take(100).Select(path => Summary(Path.GetFileName(path))).ToArray()));
        app.MapPost("/api/local/sessions/{id}/transcribe", async (string id) =>
        {
            _ = vault.SessionPath(id);
            if (!await jobs.WaitAsync(0)) return Results.Conflict(new { error = "A local transcription job is already running." });
            try { await Transcription.Run(vault, id, installFile, app.Lifetime.ApplicationStopping); return Results.Json(Summary(id)); }
            finally { jobs.Release(); }
        });
        app.MapGet("/api/local/sessions/{id}/audio/{track}", async (string id, string track) =>
        {
            if (track is not ("mic" or "output")) return Results.BadRequest();
            if (!await audioGate.WaitAsync(0)) return Results.Conflict();
            try { return Results.Bytes(TimelineAudio.Render(vault, id, track).Wav, "audio/wav"); }
            finally { audioGate.Release(); }
        });
        app.MapGet("/api/local/sessions/{id}/diagnostics", (string id) => Results.Json(Diagnostics.Analyze(vault, id)));
        var assets = new Dictionary<string, (string File, string Mime)> {
            ["/styles.css"] = ("styles.css", "text/css"), ["/local.js"] = ("local.js", "application/javascript"), ["/favicon.svg"] = ("favicon.svg", "image/svg+xml") };
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
        var server = Create(vault, publicRoot, install, port, fixture);
        await server.App.StartAsync();
        // Browser bootstrap credentials stay out of console logs and query strings.
        string url = server.Origin + "/app/#key=" + server.Token;
        vault.Write($"ui-{Guid.NewGuid():N}.snu", new { url });
        if (openBrowser) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Console.WriteLine($"SaidNest local timeline is running on {server.Origin}. Private access opened locally; press Ctrl+C to stop.");
        await server.App.WaitForShutdownAsync();
        await server.App.DisposeAsync();
    }
}
