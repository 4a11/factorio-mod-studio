using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioModStudio.Core;
using FactorioModStudio.Core.Lua;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactorioModStudio.Server;

/// <param name="Port">0 picks a free port.</param>
/// <param name="ExtraModFolders">Additional folders with installed mods (zips or unpacked) to offer read-only in the UI.</param>
/// <param name="FactorioExecutable">When known, the game's own data folder (base, core, ...) is offered for browsing too.</param>
/// <param name="WorkspaceDirectory">Overrides the project's workspace folder. Mods inside it can be edited from the UI.</param>
/// <param name="FileManager">Opens the OS file manager for "Reveal"; null starts the real one. Tests pass a fake.</param>
/// <param name="FactorioDownloads">Makes the downloader for "install a missing Factorio"; null uses Factorio's servers. Tests pass a fake.</param>
public sealed record UiOptions(
    int Port = 5757,
    IReadOnlyList<string>? ExtraModFolders = null,
    string? FactorioExecutable = null,
    string? WorkspaceDirectory = null,
    FileManagerLauncher? FileManager = null,
    Func<FactorioDownloads>? FactorioDownloads = null);

public sealed class RunningUi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly WorkspaceWatcher _watcher;
    private readonly BuildJobs _jobs;

    internal RunningUi(WebApplication app, WorkspaceWatcher watcher, BuildJobs jobs, string url, string token)
    {
        _app = app;
        _watcher = watcher;
        _jobs = jobs;
        Url = url;
        Token = token;
    }

    public string Url { get; }

    /// <summary>Secret that write requests must carry in the <c>X-FMS-Token</c> header. Only served to the page itself.</summary>
    public string Token { get; }

    /// <summary>Number of folders currently watched for live updates.</summary>
    public int WatchedFolders => _watcher.WatcherCount;

    public async ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        _jobs.Dispose(); // kills Factorio processes started from the Build tab
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Local web host: a JSON API over <see cref="ModInspector"/>, live change notifications (server-sent events), controlled file
/// editing and the embedded single-page UI.
/// Safety model: loopback only; foreign Host headers are rejected (DNS rebinding); every write needs a per-session token that
/// only the served page can read; writes are limited to text files inside editable mods and use optimistic concurrency (ETag).
/// </summary>
public static class UiServer
{
    /// <summary>Languages of the web UI; one embedded <c>wwwroot/i18n/&lt;code&gt;.json</c> each. Keep in sync with <c>LANGUAGES</c> in i18n.js.</summary>
    private static readonly string[] UiLanguages = ["en", "ru", "de", "zh-CN", "uk", "pl", "fr", "es"];

    /// <summary>Plain ES modules (and the classic theme-init.js) served next to app.js; one embedded resource each.</summary>
    private static readonly string[] StaticScripts =
    [
        "lua-highlight.js", "locale-placeholders.js", "locale-view.js", "build-view.js", "i18n.js",
        "theme-init.js", "theme.js", "ui-state.js", "palette-logic.js", "palette.js", "editor-tabs.js",
        "bottom-panel.js", "search-panel.js", "action-bar.js", "files-logic.js", "files-view.js", "prototypes-view.js", "resources-view.js"
    ];

    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private const long MaxTextFileBytes = 2 * 1024 * 1024;
    private const string TokenHeader = "X-FMS-Token";

    internal static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".lua", ".cfg", ".json", ".txt", ".md", ".csv", ".ini" };

    /// <summary>What may be written through the API. Narrower than what may be viewed.</summary>
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".lua", ".cfg", ".json", ".txt", ".md" };

    private static readonly Dictionary<string, string> BinaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".bmp"] = "image/bmp",
        [".ogg"] = "audio/ogg", [".wav"] = "audio/wav", [".mp3"] = "audio/mpeg", [".flac"] = "audio/flac"
    };

    public static async Task<RunningUi> StartAsync(ProjectContext context, UiOptions options, CancellationToken cancellationToken = default)
    {
        var workspace = options.WorkspaceDirectory ?? context.WorkspaceDirectory;
        var catalog = new ModCatalog(
            context,
            options.ExtraModFolders ?? Array.Empty<string>(),
            ModCatalog.GameDataFor(options.FactorioExecutable),
            workspace);
        catalog.List(); // prime the path -> mod mapping used by the watcher

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var feed = new ChangeFeed();
        var watcher = new WorkspaceWatcher(catalog, feed);
        var jobs = new BuildJobs(context, catalog);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        builder.Logging.ClearProviders();
        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        var app = builder.Build();
        MapSecurity(app, token);
        MapStaticAssets(app, token);
        MapApi(app, context, catalog, feed, watcher, jobs);
        BuildApi.Map(app, catalog, jobs);
        FilesApi.Map(app, catalog, context, options.FileManager);
        FactorioSettingsApi.Map(app, jobs, token);
        FactorioInstallApi.Map(app, new FactorioInstalls(options.FactorioDownloads ?? DefaultDownloads, () => jobs.InvalidateEnvironment()));

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            watcher.Dispose();
            jobs.Dispose();
            throw new FmsException($"Could not listen on port {options.Port}: {ex.Message} Try another port with --port.", ex);
        }

        watcher.Start();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RunningUi(app, watcher, jobs, address.Replace("127.0.0.1", "localhost"), token);
    }

    /// <summary>One HTTP client for every install: Factorio's updater and its download servers.</summary>
    private static readonly Lazy<HttpClient> DownloadClient = new(() =>
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // the downloads have their own cancellation
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FactorioModStudio/{typeof(UiServer).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        return http;
    });

    private static FactorioDownloads DefaultDownloads() =>
        new(DownloadClient.Value, new ProcessRunner(), FactorioInstallations.ManagedInstallationsDirectory());

    private static void MapSecurity(WebApplication app, string token)
    {
        var expected = Encoding.UTF8.GetBytes(token);

        app.Use(async (http, next) =>
        {
            var host = http.Request.Host.Host;
            if (host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Forbidden host.");
                return;
            }

            var headers = http.Response.Headers;
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";

            // Anything that changes state needs the session token and, if the browser sent an Origin, the same origin.
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
            {
                var supplied = Encoding.UTF8.GetBytes(http.Request.Headers[TokenHeader].ToString());
                var origin = http.Request.Headers.Origin.ToString();
                var sameOrigin = origin.Length == 0 || string.Equals(origin, $"http://{http.Request.Host}", StringComparison.OrdinalIgnoreCase);

                if (!sameOrigin || !CryptographicOperations.FixedTimeEquals(supplied, expected))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new { error = "Missing or invalid session token." });
                    return;
                }
            }

            await next();
        });
    }

    private static void MapStaticAssets(WebApplication app, string token)
    {
        var assembly = typeof(UiServer).Assembly;

        byte[] Load(string name)
        {
            using var stream = assembly.GetManifestResourceStream("wwwroot/" + name)
                               ?? throw new InvalidOperationException($"Embedded resource wwwroot/{name} is missing.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        // The translation files live under /i18n/. Kestrel and routing normalise dot segments, %2e escapes, case and trailing
        // slashes before an endpoint is chosen, so judge the raw request target: only the exact allowlisted paths pass.
        app.Use(async (http, next) =>
        {
            var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget ?? http.Request.Path.Value ?? "";
            var query = raw.IndexOf('?', StringComparison.Ordinal);
            var path = query >= 0 ? raw[..query] : raw;
            var underI18n = path.StartsWith("/i18n/", StringComparison.OrdinalIgnoreCase) || path.Equals("/i18n", StringComparison.OrdinalIgnoreCase);
            if (underI18n && !Array.Exists(UiLanguages, l => string.Equals(path, $"/i18n/{l}.json", StringComparison.Ordinal)))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });

        app.MapGet("/", () =>
        {
            var html = Encoding.UTF8.GetString(Load("index.html")).Replace("{{FMS_TOKEN}}", token, StringComparison.Ordinal);
            return Results.Text(html, "text/html; charset=utf-8");
        });
        app.MapGet("/app.js", () => Results.Bytes(Load("app.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/style.css", () => Results.Bytes(Load("style.css"), "text/css; charset=utf-8"));
        foreach (var script in StaticScripts)
        {
            var name = script; // closure copy; the resource name comes from this allowlist, never from the request
            app.MapGet("/" + name, () => Results.Bytes(Load(name), "text/javascript; charset=utf-8"));
        }

        // Interface translations. Only codes from the allowlist are served and the resource name is taken from the
        // allowlist entry, never from the request.
        app.MapGet("/i18n/{code}.json", (string code, HttpContext http) =>
        {
            var language = Array.Find(UiLanguages, l => string.Equals(l, code, StringComparison.Ordinal));
            // Routing is case-insensitive and ignores a trailing slash; the exact path must match the allowlist entry.
            if (language is null || !string.Equals(http.Request.Path.Value, $"/i18n/{language}.json", StringComparison.Ordinal)) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Bytes(Load($"i18n/{language}.json"), "application/json; charset=utf-8");
        });
    }

    private static void MapApi(WebApplication app, ProjectContext context, ModCatalog catalog, ChangeFeed feed, WorkspaceWatcher watcher, BuildJobs jobs)
    {
        MapImport(app, catalog, feed, watcher);
        MapNew(app, catalog, feed, watcher);
        MapLocale(app, catalog);
        ModSearch.Map(app, catalog);

        app.MapGet("/api/project", () => new
        {
            root = context.Root,
            hasConfig = context.ConfigFileExists,
            modDirectory = context.ModDirectory,
            workspace = catalog.WorkspaceDirectory,
            workspaceExists = catalog.WorkspaceDirectory is not null && Directory.Exists(catalog.WorkspaceDirectory),
            profiles = context.Config.Profiles.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            modFolders = catalog.ModFolders,
            gameData = catalog.GameDataDirectory
        });

        app.MapGet("/api/mods", () => catalog.List().Select(e => new { e.Id, e.Name, e.Version, e.Source, e.Kind, e.Editable }));

        app.MapGet("/api/mods/{id}", (string id) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");

            try
            {
                var overview = catalog.Overview(entry);
                // Readiness depends on dist/ and file times, so it is computed per request and never cached.
                return Results.Ok(overview with { Readiness = ReleaseReadiness.Evaluate(catalog.Materialize(entry), overview, jobs.DistDirectory) });
            }
            catch (FmsException ex) { return Error(StatusCodes.Status422UnprocessableEntity, ex.Message); }
        });

        app.MapGet("/api/mods/{id}/file", (HttpContext http, string id, string path) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");

            string root;
            try { root = Path.GetFullPath(catalog.Materialize(entry)); }
            catch (FmsException ex) { return Error(StatusCodes.Status422UnprocessableEntity, ex.Message); }

            var full = ResolveInside(root, path);
            if (full is null) return Error(StatusCodes.Status400BadRequest, "Invalid path.");
            if (!File.Exists(full)) return Error(StatusCodes.Status404NotFound, "File not found.");

            var ext = Path.GetExtension(full);
            if (BinaryTypes.TryGetValue(ext, out var contentType))
                return Results.File(full, contentType, enableRangeProcessing: true);

            if (!TextExtensions.Contains(ext)) return Error(StatusCodes.Status415UnsupportedMediaType, "This file type cannot be previewed.");
            if (new FileInfo(full).Length > MaxTextFileBytes) return Error(StatusCodes.Status413PayloadTooLarge, "File is too large to preview.");

            http.Response.Headers.ETag = ETagOf(full);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Text(File.ReadAllText(full), "text/plain; charset=utf-8");
        });

        app.MapPut("/api/mods/{id}/file", async (HttpContext http, string id, string path) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (!entry.Editable)
                return Error(StatusCodes.Status403Forbidden, "This mod is read-only. Only unpacked mods in the workspace folder and the project mod can be edited.");

            var root = Path.GetFullPath(entry.Path);
            var full = ResolveInside(root, path);
            if (full is null || !IsWritablePath(root, full)) return Error(StatusCodes.Status400BadRequest, "Invalid path.");
            if (!EditableExtensions.Contains(Path.GetExtension(full)))
                return Error(StatusCodes.Status415UnsupportedMediaType, "Only .lua, .cfg, .json, .txt and .md files can be edited.");

            byte[] body;
            try { body = await ReadLimited(http.Request.Body, MaxTextFileBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "File is too large to save (limit 2 MB)."); }

            string text;
            try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body); }
            catch (DecoderFallbackException) { return Error(StatusCodes.Status400BadRequest, "Content is not valid UTF-8."); }

            // Optimistic concurrency: overwrite only the version the editor was based on; create only if nothing is there.
            var exists = File.Exists(full);
            if (exists)
            {
                var ifMatch = http.Request.Headers.IfMatch.ToString();
                if (ifMatch.Length == 0) return Error(StatusCodes.Status428PreconditionRequired, "If-Match header required to overwrite a file.");

                var current = ETagOf(full);
                if (ifMatch != current)
                    return Results.Json(new { error = "The file changed on disk since you opened it.", etag = current }, statusCode: StatusCodes.Status409Conflict);
            }
            else if (http.Request.Headers.IfNoneMatch.ToString() != "*")
            {
                return Error(StatusCodes.Status428PreconditionRequired, "The file does not exist; send If-None-Match: * to create it.");
            }

            try
            {
                WriteAtomically(full, text, exists);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not write the file: {ex.Message}");
            }

            var etag = ETagOf(full);
            http.Response.Headers.ETag = etag;
            return Results.Json(new { etag, size = new FileInfo(full).Length });
        });

        // Structural Lua check of an editor buffer. Read-only on disk: the text comes from the request body, so it also
        // works for unsaved edits and for read-only mods. POST only because the buffer can be large.
        app.MapPost("/api/lint", async (HttpContext http, string id, string path) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");

            // Purely lexical path check (nothing is read from disk), so it applies to zipped mods as well.
            if (ResolveInside(Path.GetFullPath(entry.Path), path) is null) return Error(StatusCodes.Status400BadRequest, "Invalid path.");
            if (!string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { diagnostics = Array.Empty<LuaDiagnostic>() });

            byte[] body;
            try { body = await ReadLimited(http.Request.Body, MaxTextFileBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "File is too large to check (limit 2 MB)."); }

            string text;
            try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body); }
            catch (DecoderFallbackException) { return Error(StatusCodes.Status400BadRequest, "Content is not valid UTF-8."); }

            return Results.Json(new { diagnostics = LuaDiagnostics.Check(text) });
        });

        // Server-sent events: one long-lived response per open browser tab.
        app.MapGet("/api/events", async (HttpContext http) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            using var subscription = feed.Subscribe();
            await http.Response.WriteAsync("retry: 2000\n: connected\n\n", http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);

            // Also ends when the server stops; otherwise shutting down waits for every open tab to disconnect by itself.
            using var open = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, app.Lifetime.ApplicationStopping);
            var ct = open.Token;
            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));

                try
                {
                    if (!await subscription.Reader.WaitToReadAsync(timeout.Token)) break;
                    while (subscription.Reader.TryRead(out var message))
                        await http.Response.WriteAsync($"data: {message}\n\n", ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct); // keep proxies and the browser from timing out
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await http.Response.Body.FlushAsync(ct);
            }
        });
    }

    private static void MapImport(WebApplication app, ModCatalog catalog, ChangeFeed feed, WorkspaceWatcher watcher)
    {
        var importer = catalog.WorkspaceDirectory is null ? null : new ModImporter(catalog.WorkspaceDirectory);

        IResult Fail(ModImportException ex)
        {
            if (ex.Kind == ModImportError.Conflict && ex.ExistingPath is { } existing)
            {
                var id = ModCatalog.IdFor(existing);
                return Results.Json(
                    new { error = ex.Message, existing = catalog.List().Any(e => e.Id == id) ? id : null },
                    statusCode: StatusCodes.Status409Conflict);
            }

            var status = ex.Kind switch
            {
                ModImportError.Conflict => StatusCodes.Status409Conflict,
                ModImportError.TooLarge => StatusCodes.Status413PayloadTooLarge,
                ModImportError.UnsupportedType => StatusCodes.Status415UnsupportedMediaType,
                ModImportError.NotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest
            };
            return Error(status, ex.Message);
        }

        IResult Done(ModImportResult result)
        {
            catalog.List();
            feed.Publish(new { type = "mods" });
            watcher.EnsureWatching();
            var id = ModCatalog.IdFor(result.Folder);
            return Results.Json(
                new { id, name = result.Name, version = result.Version, folder = result.Folder, files = result.Files, skipped = result.Skipped },
                statusCode: StatusCodes.Status201Created);
        }

        IResult? NoWorkspace() =>
            importer is null ? Error(StatusCodes.Status400BadRequest, "No workspace folder is configured.") : null;

        static void LiftBodyLimit(HttpContext http) =>
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;

        app.MapPost("/api/workspace/import-zip", async (HttpContext http) =>
        {
            if (NoWorkspace() is { } none) return none;
            if (http.Request.ContentLength > ModImporter.MaxZipBytes)
                return Error(StatusCodes.Status413PayloadTooLarge, "The zip is larger than 200 MB.");

            LiftBodyLimit(http);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                if (memory.Length + read > ModImporter.MaxZipBytes)
                    return Error(StatusCodes.Status413PayloadTooLarge, "The zip is larger than 200 MB.");
                memory.Write(buffer, 0, read);
            }

            memory.Position = 0;
            try { return Done(importer!.ImportZip(memory)); }
            catch (ModImportException ex) { return Fail(ex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not import the mod: {ex.Message}");
            }
        });

        app.MapPost("/api/workspace/imports", () =>
        {
            if (NoWorkspace() is { } none) return none;
            try { return Results.Json(new { importId = importer!.CreateSession() }, statusCode: StatusCodes.Status201Created); }
            catch (ModImportException ex) { return Fail(ex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not start the import: {ex.Message}");
            }
        });

        app.MapPut("/api/workspace/imports/{importId}/file", async (HttpContext http, string importId, string? path) =>
        {
            if (NoWorkspace() is { } none) return none;
            if (http.Request.ContentLength > ModImporter.MaxFileBytes)
                return Error(StatusCodes.Status413PayloadTooLarge, "A file is larger than 50 MB.");

            LiftBodyLimit(http);
            try
            {
                await importer!.AddFileAsync(importId, path, http.Request.Body, http.RequestAborted);
                return Results.NoContent();
            }
            catch (ModImportException ex) { return Fail(ex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not store the file: {ex.Message}");
            }
        });

        app.MapPost("/api/workspace/imports/{importId}/commit", (string importId) =>
        {
            if (NoWorkspace() is { } none) return none;
            try { return Done(importer!.Commit(importId)); }
            catch (ModImportException ex) { return Fail(ex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not import the mod: {ex.Message}");
            }
        });

        app.MapDelete("/api/workspace/imports/{importId}", (string importId) =>
        {
            if (NoWorkspace() is { } none) return none;
            try
            {
                importer!.Cancel(importId);
                return Results.NoContent();
            }
            catch (ModImportException ex) { return Fail(ex); }
        });
    }

    private const long MaxNewModBodyBytes = 16 * 1024;

    private static void MapNew(WebApplication app, ModCatalog catalog, ChangeFeed feed, WorkspaceWatcher watcher)
    {
        app.MapGet("/api/templates", () => ModScaffolder.Templates.Select(t => new { t.Id, t.Title, t.Description, t.SupportedVersions }));

        app.MapPost("/api/workspace/new", async (HttpContext http) =>
        {
            var workspace = catalog.WorkspaceDirectory;
            if (workspace is null) return Error(StatusCodes.Status400BadRequest, "No workspace folder is configured.");

            byte[] body;
            try { body = await ReadLimited(http.Request.Body, MaxNewModBodyBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 16 KB)."); }

            ScaffoldRequest request;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Error(StatusCodes.Status400BadRequest, "The body must be a JSON object.");

                string? Text(string field)
                {
                    if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
                    if (value.ValueKind != JsonValueKind.String) throw new FormatException($"\"{field}\" must be a string.");
                    return value.GetString();
                }

                var template = Text("template");
                request = new ScaffoldRequest(
                    Text("name") ?? "",
                    string.IsNullOrEmpty(template) ? "empty" : template,
                    Text("title"), Text("author"), Text("description"),
                    Text("factorioVersion") is { Length: > 0 } version ? version : ModScaffolder.DefaultFactorioVersion);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                return Error(StatusCodes.Status400BadRequest, ex is FormatException ? ex.Message : "The body is not valid JSON.");
            }

            try
            {
                var result = ModScaffolder.Create(request, workspace);
                catalog.List();
                feed.Publish(new { type = "mods" });
                watcher.EnsureWatching();
                return Results.Json(
                    new { id = ModCatalog.IdFor(result.Folder), name = result.Name, version = result.Version, folder = result.Folder, files = result.Files.Count },
                    statusCode: StatusCodes.Status201Created);
            }
            catch (ScaffoldException ex) when (ex.Kind == ScaffoldError.Exists)
            {
                var id = ex.ExistingPath is null ? null : ModCatalog.IdFor(ex.ExistingPath);
                return Results.Json(
                    new { error = ex.Message, existing = id is not null && catalog.List().Any(e => e.Id == id) ? id : null },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (ScaffoldException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (Exception ex) when (ex is FmsException or IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not create the mod: {ex.Message}");
            }
        });
    }

    // ---------------------------------------------------------------- localisation editor

    private const long MaxLocaleBodyBytes = 2 * 1024 * 1024;
    private const int MaxMemoryEntries = 20000;

    /// <summary>The locale API always writes null properties (a missing translation is <c>"target": null</c>, not an absent field).</summary>
    private static readonly JsonSerializerOptions LocaleJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static IResult LocaleResult(object value, int status = StatusCodes.Status200OK) =>
        Results.Json(value, LocaleJson, statusCode: status);

    private static void MapLocale(WebApplication app, ModCatalog catalog)
    {
        // Translated rows of the same language in the other editable mods: a local translation memory (no network).
        List<LocaleMemoryEntry> BuildMemory(string currentId, string language)
        {
            var memory = new List<LocaleMemoryEntry>();
            foreach (var other in catalog.List())
            {
                if (!other.Editable || other.Id == currentId || other.Kind != "directory") continue;
                if (memory.Count >= MaxMemoryEntries) break;

                try
                {
                    var directory = Path.GetFullPath(other.Path);
                    if (!Directory.Exists(Path.Combine(directory, "locale", language))) continue;
                    var table = LocaleEditor.Build(directory, language);
                    if (string.Equals(table.SourceLanguage, language, StringComparison.Ordinal)) continue;

                    foreach (var row in table.Rows)
                    {
                        if (row.Status != LocaleRowStatus.Translated || row.Source.Length == 0 || string.IsNullOrWhiteSpace(row.Target)) continue;
                        memory.Add(new LocaleMemoryEntry(row.Source, row.Target));
                        if (memory.Count >= MaxMemoryEntries) break;
                    }
                }
                catch (Exception ex) when (ex is FmsException or IOException or UnauthorizedAccessException) { /* a broken neighbour must not break this mod */ }
            }
            return memory;
        }

        app.MapGet("/api/mods/{id}/locale", (string id) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");

            string root;
            try { root = Path.GetFullPath(catalog.Materialize(entry)); }
            catch (FmsException ex) { return Error(StatusCodes.Status422UnprocessableEntity, ex.Message); }

            try
            {
                return LocaleResult(new
                {
                    sourceLanguage = LocaleEditor.SourceLanguage(root),
                    editable = entry.Editable,
                    languages = LocaleEditor.Summaries(root)
                });
            }
            catch (Exception ex) when (ex is FmsException or IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not read the translations: {ex.Message}");
            }
        });

        app.MapGet("/api/mods/{id}/locale/{lang}", (HttpContext http, string id, string lang) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (!LocaleEditor.IsValidLanguageCode(lang)) return Error(StatusCodes.Status400BadRequest, "Invalid language code.");

            string root;
            try { root = Path.GetFullPath(catalog.Materialize(entry)); }
            catch (FmsException ex) { return Error(StatusCodes.Status422UnprocessableEntity, ex.Message); }

            if (!Directory.Exists(Path.Combine(root, "locale", lang))) return Error(StatusCodes.Status404NotFound, "This mod has no such language.");

            try
            {
                var table = LocaleEditor.Build(root, lang, BuildMemory(id, lang));
                http.Response.Headers.ETag = $"\"{table.Revision}\"";
                http.Response.Headers.CacheControl = "no-store";
                return LocaleResult(table);
            }
            catch (FmsException ex) { return Error(StatusCodes.Status400BadRequest, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not read the translations: {ex.Message}");
            }
        });

        app.MapPut("/api/mods/{id}/locale/{lang}", async (HttpContext http, string id, string lang) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (!entry.Editable)
                return Error(StatusCodes.Status403Forbidden, "This mod is read-only. Only unpacked mods in the workspace folder and the project mod can be edited.");
            if (!LocaleEditor.IsValidLanguageCode(lang)) return Error(StatusCodes.Status400BadRequest, "Invalid language code.");

            var root = Path.GetFullPath(entry.Path);
            if (!Directory.Exists(Path.Combine(root, "locale", lang))) return Error(StatusCodes.Status404NotFound, "This mod has no such language.");

            byte[] body;
            try { body = await ReadLimited(http.Request.Body, MaxLocaleBodyBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 2 MB)."); }

            List<LocaleChange> changes;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("changes", out var list) || list.ValueKind != JsonValueKind.Array)
                    return Error(StatusCodes.Status400BadRequest, "The body must be an object with a \"changes\" array.");

                changes = new List<LocaleChange>();
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("section", out var section) || section.ValueKind != JsonValueKind.String ||
                        !item.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                        !item.TryGetProperty("value", out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        return Error(StatusCodes.Status400BadRequest, "Every change needs a string \"section\", a string \"key\" and a \"value\" (a string, or null to remove the key).");
                    changes.Add(new LocaleChange(section.GetString()!, key.GetString()!, value.ValueKind == JsonValueKind.Null ? null : value.GetString()));
                }
            }
            catch (JsonException) { return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON."); }
            catch (InvalidOperationException) { return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON."); }

            var ifMatch = http.Request.Headers.IfMatch.ToString().Trim();
            if (ifMatch.Length == 0) return Error(StatusCodes.Status428PreconditionRequired, "If-Match header required to save translations.");
            if (ifMatch.StartsWith("W/", StringComparison.Ordinal)) ifMatch = ifMatch[2..];
            ifMatch = ifMatch.Trim('"');

            try
            {
                var result = LocaleEditor.Apply(root, lang, changes, ifMatch);
                http.Response.Headers.ETag = $"\"{result.Revision}\"";
                return LocaleResult(new { revision = result.Revision, added = result.Added, updated = result.Updated, removed = result.Removed, touchedFiles = result.TouchedFiles });
            }
            catch (LocaleConflictException ex)
            {
                return LocaleResult(new { error = ex.Message, revision = ex.CurrentRevision }, StatusCodes.Status409Conflict);
            }
            catch (FmsException ex) { return Error(StatusCodes.Status400BadRequest, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not write the translations: {ex.Message}");
            }
        });

        app.MapPost("/api/mods/{id}/locale", async (HttpContext http, string id) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (!entry.Editable)
                return Error(StatusCodes.Status403Forbidden, "This mod is read-only. Only unpacked mods in the workspace folder and the project mod can be edited.");

            byte[] body;
            try { body = await ReadLimited(http.Request.Body, MaxNewModBodyBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 16 KB)."); }

            string? code;
            try
            {
                using var document = JsonDocument.Parse(body);
                code = document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException) { return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON."); }

            if (!LocaleEditor.IsValidLanguageCode(code)) return Error(StatusCodes.Status400BadRequest, "Invalid language code.");

            var root = Path.GetFullPath(entry.Path);
            if (LocaleEditor.SourceLanguage(root) is null) return Error(StatusCodes.Status400BadRequest, "This mod has no locale folder to copy the structure from.");
            if (LocaleData.Languages(root).Any(l => string.Equals(l, code, StringComparison.OrdinalIgnoreCase)))
                return Error(StatusCodes.Status409Conflict, $"The language \"{code}\" already exists.");

            try
            {
                LocaleEditor.CreateLanguage(root, code!);
                return LocaleResult(new { code }, StatusCodes.Status201Created);
            }
            catch (FmsException ex)
            {
                // Two requests can both pass the pre-check above; the one that loses the race finds the folder there now.
                var exists = LocaleData.Languages(root).Any(l => string.Equals(l, code, StringComparison.OrdinalIgnoreCase));
                return Error(exists ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not create the language: {ex.Message}");
            }
        });
    }

    /// <summary>Quoted validator for a file: changes whenever size or modification time change.</summary>
    internal static string ETagOf(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
    }

    /// <summary>Resolves a client-supplied relative path, returning null if it is absolute, contains "..", or leaves <paramref name="root"/>.</summary>
    internal static string? ResolveInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0') || Path.IsPathRooted(relative)) return null;
        if (relative.Split('/', '\\').Any(part => part == "..")) return null;

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(prefix, comparison) ? full : null;
    }

    /// <summary>Extra rules for writing: no hidden folders or files (.git, .fmstudio, ...) and no symbolic links or junctions on the way.</summary>
    internal static bool IsWritablePath(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => p.Length == 0 || p.StartsWith('.'))) return false;

        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }

        return true;
    }

    internal static async Task<byte[]> ReadLimited(Stream input, long limit, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > limit) throw new InvalidDataException("Body too large.");
        }
        return memory.ToArray();
    }

    /// <summary>Writes to a temp file next to the target and renames it over, so readers never see a half-written file.
    /// An existing file keeps its line-ending style (CRLF) and UTF-8 BOM, which avoids noisy diffs.</summary>
    private static void WriteAtomically(string full, string text, bool exists)
    {
        var bom = false;
        if (exists)
        {
            var existing = File.ReadAllBytes(full);
            bom = existing.Length >= 3 && existing[0] == 0xEF && existing[1] == 0xBB && existing[2] == 0xBF;
            if (Encoding.UTF8.GetString(existing).Contains("\r\n", StringComparison.Ordinal))
                text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);

        var payload = Encoding.UTF8.GetBytes(text);
        if (bom) payload = Encoding.UTF8.GetPreamble().Concat(payload).ToArray();

        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.fms-tmp");
        try
        {
            File.WriteAllBytes(temp, payload);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static IResult Error(int status, string message) =>
        Results.Json(new { error = message }, statusCode: status);
}
