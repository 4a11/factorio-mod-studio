using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioModStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FactorioModStudio.Server;

/// <summary>Where an install is: "downloading", "unpacking", "succeeded", "failed" or "cancelled".</summary>
internal sealed record InstallSnapshot(string Version, string State, long Received, long? Total, string? Error, string? Path, bool AlreadyInstalled);

/// <summary>
/// The Build tab's "install a missing Factorio": one headless install at a time, run in the background and polled by the page.
/// The work itself is <see cref="FactorioDownloads"/> (Core); this class only keeps its progress and lets it be cancelled.
/// </summary>
internal sealed class FactorioInstalls
{
    private static readonly TimeSpan ReleasesLifetime = TimeSpan.FromMinutes(10);

    private readonly Func<FactorioDownloads> _create;
    private readonly Action _changed;
    private readonly object _gate = new();
    private InstallSnapshot? _current;
    private CancellationTokenSource? _cancel;
    private (DateTime At, AvailableReleases Releases)? _releases;

    /// <param name="create">Makes the downloader (tests pass one with a fake HTTP client and tar).</param>
    /// <param name="changed">Called after an install or removal, so the installation lists are scanned again.</param>
    public FactorioInstalls(Func<FactorioDownloads> create, Action changed)
    {
        _create = create;
        _changed = changed;
    }

    public FactorioDownloads Downloads() => _create();

    public InstallSnapshot? Current { get { lock (_gate) return _current; } }

    /// <summary>The updater's list, kept for 10 minutes.</summary>
    public async Task<AvailableReleases> ReleasesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            if (_releases is { } cached && DateTime.UtcNow - cached.At < ReleasesLifetime) return cached.Releases;
        var releases = await _create().AvailableAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) _releases = (DateTime.UtcNow, releases);
        return releases;
    }

    /// <summary>Starts installing <paramref name="version"/>; null when another install is still running.</summary>
    public InstallSnapshot? Start(string version)
    {
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_current is { State: "downloading" or "unpacking" }) return null;
            _cancel?.Dispose();
            _cancel = cancel = new CancellationTokenSource();
            _current = new InstallSnapshot(version, "downloading", 0, null, null, null, false);
        }

        var downloads = _create();
        var progress = new SyncProgress(p =>
        {
            lock (_gate)
                if (_current is { } c && c.Version == version && c.State == "downloading")
                    // Once every byte is in, tar is unpacking and the version is being checked.
                    _current = c with { Received = p.Received, Total = p.Total, State = p.Total is { } t && p.Received >= t ? "unpacking" : "downloading" };
        });

        _ = Task.Run(async () =>
        {
            InstallSnapshot done;
            try
            {
                var result = await downloads.InstallHeadlessAsync(version, progress, cancel.Token).ConfigureAwait(false);
                done = Snapshot(version) with { State = "succeeded", Path = result.Path, AlreadyInstalled = result.AlreadyInstalled };
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                done = Snapshot(version) with { State = "cancelled" };
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the install has to end: a state left at "downloading" would block every later install.
                done = Snapshot(version) with { State = "failed", Error = ex.Message };
            }
            lock (_gate) _current = done;
            if (done.State == "succeeded") _changed();
        });

        return Current;
    }

    private InstallSnapshot Snapshot(string version)
    {
        lock (_gate) return _current is { } c && c.Version == version ? c : new InstallSnapshot(version, "downloading", 0, null, null, null, false);
    }

    /// <summary>True when a running install was asked to stop.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_current is not { State: "downloading" or "unpacking" } || _cancel is null) return false;
            _cancel.Cancel();
            return true;
        }
    }

    public bool Remove(string version)
    {
        lock (_gate)
            if (_current is { State: "downloading" or "unpacking" } c && c.Version == version)
                throw new FmsException($"Factorio {version} is being installed right now.");
        var removed = _create().Remove(version);
        if (removed) _changed();
        return removed;
    }

    private sealed class SyncProgress(Action<(long Received, long? Total)> report) : IProgress<(long Received, long? Total)>
    {
        public void Report((long Received, long? Total) value) => report(value);
    }
}

/// <summary>
/// <c>/api/factorio/available</c>, <c>/api/factorio/install</c> (start, progress, cancel) and removing an install. Writes pass the
/// session-token and same-origin check in <c>UiServer.MapSecurity</c> like every other write; versions are accepted only as
/// MAJOR.MINOR or MAJOR.MINOR.PATCH and resolved against Factorio's own list before anything is downloaded.
/// </summary>
internal static class FactorioInstallApi
{
    private const long MaxBodyBytes = 1024;

    // Null fields are written out ("install": null, "platformProblem": null): the page and the documented contract rely on them.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, Options, statusCode: status);
    private static IResult Error(int status, string message) => Json(new { error = message }, status);

    private static object? Inner(InstallSnapshot? s) => s is null ? null :
        new { version = s.Version, state = s.State, received = s.Received, total = s.Total, error = s.Error, path = s.Path, alreadyInstalled = s.AlreadyInstalled };

    /// <summary><c>{ "install": { version, state, received, total, error, path, alreadyInstalled } | null }</c></summary>
    private static object Describe(InstallSnapshot? s) => new { install = Inner(s) };

    public static void Map(WebApplication app, FactorioInstalls installs)
    {
        app.MapGet("/api/factorio/available", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var downloads = installs.Downloads();
            var installed = downloads.Installed().Select(i => i.Version).ToList();
            // Where the headless server cannot run there is nothing to offer, and no reason to ask Factorio's servers.
            if (downloads.PlatformProblem is { } problem)
                return Json(new { platformProblem = problem, stable = (string?)null, experimental = (string?)null, branches = Array.Empty<object>(), installed });

            AvailableReleases releases;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                releases = await installs.ReleasesAsync(timeout.Token);
            }
            catch (FmsException ex) { return Error(StatusCodes.Status502BadGateway, ex.Message); }
            catch (OperationCanceledException) { return Error(StatusCodes.Status504GatewayTimeout, "Factorio's updater did not answer within 30 seconds."); }

            var branches = releases.Versions
                .GroupBy(v => string.Join('.', v.Split('.').Take(2)))
                .Select(g => new { branch = g.Key, newest = g.First(), count = g.Count() })
                .ToList();
            return Json(new { platformProblem = (string?)null, releases.Stable, releases.Experimental, branches, installed });
        });

        app.MapGet("/api/factorio/install", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Json(Describe(installs.Current));
        });

        app.MapPost("/api/factorio/install", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.ContentLength > MaxBodyBytes) return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 1 KB).");
            byte[] body;
            try { body = await BuildApi.ReadLimited(http.Request.Body, MaxBodyBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 1 KB)."); }

            string? spec;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.String)
                    return Error(StatusCodes.Status400BadRequest, "The body must be an object with a \"version\" string (MAJOR.MINOR or MAJOR.MINOR.PATCH).");
                spec = value.GetString();
            }
            catch (JsonException) { return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON."); }

            if (!FactorioDownloads.IsVersionSpec(spec))
                return Error(StatusCodes.Status400BadRequest, "The version must be MAJOR.MINOR (newest release of that branch), MAJOR.MINOR.PATCH, \"stable\" or \"latest\".");
            var downloads = installs.Downloads();
            if (downloads.PlatformProblem is { } problem) return Error(StatusCodes.Status400BadRequest, problem);

            string? version;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                version = FactorioDownloads.Resolve(await installs.ReleasesAsync(timeout.Token), spec!);
            }
            catch (FmsException ex) { return Error(StatusCodes.Status502BadGateway, ex.Message); }
            catch (OperationCanceledException) { return Error(StatusCodes.Status504GatewayTimeout, "Factorio's updater did not answer within 30 seconds."); }
            if (version is null) return Error(StatusCodes.Status404NotFound, $"Factorio has no headless release matching \"{spec}\".");

            var started = installs.Start(version);
            if (started is null) return Json(new { error = "Another Factorio install is still running.", install = Inner(installs.Current) }, StatusCodes.Status409Conflict);
            return Json(Describe(started), StatusCodes.Status202Accepted);
        });

        app.MapPost("/api/factorio/install/cancel", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return installs.Cancel() ? Json(Describe(installs.Current)) : Error(StatusCodes.Status409Conflict, "No Factorio install is running.");
        });

        app.MapDelete("/api/factorio/install/{version}", (HttpContext http, string version) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!FactorioDownloads.IsVersion(version)) return Error(StatusCodes.Status400BadRequest, "The version must be MAJOR.MINOR.PATCH.");
            try
            {
                return installs.Remove(version)
                    ? Json(new { removed = version })
                    : Error(StatusCodes.Status404NotFound, $"Factorio {version} was not installed by the studio.");
            }
            catch (FmsException ex) { return Error(StatusCodes.Status409Conflict, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not remove it: " + ex.Message); }
        });
    }
}
