using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioModStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FactorioModStudio.Server;

/// <summary>
/// Where installing an update is: "downloading", "succeeded" (downloaded and waiting next to the program for the next start),
/// "failed" or "cancelled".
/// </summary>
internal sealed record UpdateSnapshot(string Version, string State, long Received, long? Total, string? Error);

/// <summary>
/// The studio's own updates for the page: whether a newer version of the chosen channel is out (asked from GitHub at most every
/// six hours), and installing it in the background. The work is <see cref="StudioUpdates"/> (Core).
/// </summary>
internal sealed class StudioUpdater
{
    private static readonly TimeSpan CheckLifetime = TimeSpan.FromHours(6);

    private readonly StudioUpdates _updates;
    private readonly string? _dataDirectory;
    private readonly object _gate = new();
    private (DateTime At, UpdateChannel Channel, UpdateCheck? Check, string? Error)? _last;
    private UpdateSnapshot? _install;
    private CancellationTokenSource? _cancel;

    /// <param name="dataDirectory">Where the chosen channel is kept; the studio's data folder by default (tests pass their own).</param>
    public StudioUpdater(StudioUpdates updates, string? dataDirectory = null)
    {
        _updates = updates;
        _dataDirectory = dataDirectory;
    }

    public StudioUpdates Updates => _updates;
    public UpdateChannel Channel => UpdateSettings.Channel(_updates.Current, _dataDirectory);
    public UpdateSnapshot? Install
    {
        get
        {
            lock (_gate)
            {
                if (_install is not null) return _install;
            }
            // An update downloaded in an earlier session that has not been swapped in yet (another studio of the folder was
            // still running at the last start): ready, as if downloaded just now.
            return _updates.Kind == InstallKind.ReleaseArchive && StudioUpdates.PendingVersion(_updates.AppDirectory) is { } pending
                ? new UpdateSnapshot(pending, "succeeded", 0, null, null)
                : null;
        }
    }

    /// <summary>When GitHub was last asked (UTC), successfully or not; null before the first check.</summary>
    public DateTime? LastChecked { get { lock (_gate) return _last?.At; } }

    public void SetChannel(UpdateChannel channel)
    {
        UpdateSettings.WriteChannel(channel, _dataDirectory);
        lock (_gate) _last = null;
    }

    /// <summary>The newest release of the channel; a failed check is kept for its lifetime too, so a page reload does not ask again.</summary>
    public async Task<(UpdateCheck? Check, string? Error)> CheckAsync(bool force, CancellationToken cancellationToken)
    {
        var channel = Channel;
        lock (_gate)
            if (!force && _last is { } last && last.Channel == channel && DateTime.UtcNow - last.At < CheckLifetime) return (last.Check, last.Error);

        UpdateCheck? check = null;
        string? error = null;
        try { check = await _updates.CheckAsync(channel, cancellationToken).ConfigureAwait(false); }
        catch (FmsException ex) { error = ex.Message; }
        catch (OperationCanceledException) { error = "GitHub did not answer in time."; }
        lock (_gate) _last = (DateTime.UtcNow, channel, check, error);
        return (check, error);
    }

    /// <summary>Starts installing <paramref name="release"/>; null when an install is already running.</summary>
    public UpdateSnapshot? Start(StudioRelease release)
    {
        lock (_gate)
        {
            if (_install is { State: "downloading" }) return null;
            _install = new UpdateSnapshot(release.Version, "downloading", 0, null, null);
            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
        }
        var token = _cancel.Token;
        var progress = new SyncProgress(p =>
        {
            lock (_gate)
                if (_install is { State: "downloading" } c) _install = c with { Received = p.Received, Total = p.Total };
        });
        _ = Task.Run(async () =>
        {
            UpdateSnapshot done;
            try
            {
                // Only prepared next to the program: the running studio keeps its files until it restarts (StudioUpdates.PrepareAsync).
                await _updates.PrepareAsync(release, progress, token).ConfigureAwait(false);
                lock (_gate) done = _install! with { State = "succeeded" };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Core checks the token up to the moment the files are swapped, never during the swap: nothing was changed.
                lock (_gate) done = _install! with { State = "cancelled" };
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the install has to end, or the page would wait for it forever.
                lock (_gate) done = _install! with { State = "failed", Error = ex.Message };
            }
            lock (_gate) _install = done;
        });
        return Install;
    }

    /// <summary>Stops a running download; false when nothing is being downloaded.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_install is not { State: "downloading" } || _cancel is null) return false;
            _cancel.Cancel();
            return true;
        }
    }

    private sealed class SyncProgress(Action<(long Received, long? Total)> report) : IProgress<(long Received, long? Total)>
    {
        public void Report((long Received, long? Total) value) => report(value);
    }
}

/// <summary>
/// <c>GET /api/update</c> (the running version, the channel, a newer release if any, an install in progress),
/// <c>POST /api/update/channel</c>, <c>POST /api/update/install</c>, <c>POST /api/update/cancel</c> and <c>POST /api/update/restart</c>. Writes pass the session
/// token check in <c>UiServer.MapSecurity</c>; the release to install is always the one the server found itself, never one
/// named by the request.
/// </summary>
internal static class UpdateApi
{
    private const long MaxBodyBytes = 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, Options, statusCode: status);
    private static IResult Error(int status, string message) => Json(new { error = message }, status);

    private static object Describe(StudioUpdater updater, UpdateCheck? check, string? error, bool canRestart)
    {
        var install = updater.Install;
        var kind = updater.Updates.Kind;
        return new
        {
            current = updater.Updates.Current,
            channel = StudioUpdates.ChannelName(updater.Channel),
            kind = kind switch { InstallKind.ReleaseArchive => "archive", InstallKind.DotnetTool => "tool", _ => "other" },
            command = kind == InstallKind.DotnetTool ? StudioUpdates.ToolCommand : null,
            latest = check?.Newer is { } r
                ? new { version = r.Version, url = r.Url, preview = StudioUpdates.IsPreview(r.Version), notes = r.Notes, published = r.Published }
                : null,
            error,
            checkedAt = updater.LastChecked is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : (DateTime?)null,
            install = install is null ? null : new { version = install.Version, state = install.State, received = install.Received, total = install.Total, error = install.Error },
            canRestart
        };
    }

    public static void Map(WebApplication app, StudioUpdater updater, Action? restart)
    {
        app.MapGet("/api/update", async (HttpContext http, bool? force) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var (check, error) = await updater.CheckAsync(force == true, timeout.Token);
            return Json(Describe(updater, check, error, restart is not null));
        });

        app.MapPost("/api/update/channel", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.ContentLength > MaxBodyBytes) return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 1 KB).");
            UpdateChannel? channel = null;
            try
            {
                var body = await BuildApi.ReadLimited(http.Request.Body, MaxBodyBytes, http.RequestAborted);
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("channel", out var value) && value.ValueKind == JsonValueKind.String)
                    channel = StudioUpdates.ParseChannel(value.GetString());
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { /* answered below */ }
            if (channel is null) return Error(StatusCodes.Status400BadRequest, "The body must be { \"channel\": \"stable\" | \"preview\" }.");

            try { updater.SetChannel(channel.Value); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not save the channel: " + ex.Message); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var (check, error) = await updater.CheckAsync(force: true, timeout.Token);
            return Json(Describe(updater, check, error, restart is not null));
        });

        app.MapPost("/api/update/install", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (updater.Updates.Kind != InstallKind.ReleaseArchive)
                return Error(StatusCodes.Status409Conflict, updater.Updates.Kind == InstallKind.DotnetTool
                    ? $"This studio is a .NET tool; update it with `{StudioUpdates.ToolCommand}`."
                    : "This studio was not installed from a release archive; update it the way it was installed.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var (check, error) = await updater.CheckAsync(force: false, timeout.Token);
            if (check?.Newer is not { } release) return Error(StatusCodes.Status409Conflict, error ?? "No newer version is out.");
            var started = updater.Start(release);
            if (started is null) return Error(StatusCodes.Status409Conflict, "An update is already being installed.");
            return Json(Describe(updater, check, error, restart is not null), StatusCodes.Status202Accepted);
        });

        app.MapPost("/api/update/cancel", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!updater.Cancel()) return Error(StatusCodes.Status409Conflict, "No update is being downloaded.");
            // The download notices the cancellation within a moment; answer once it has, so the page sees "cancelled".
            for (var i = 0; i < 50 && updater.Install is { State: "downloading" }; i++) await Task.Delay(100, http.RequestAborted);
            var (check, error) = await updater.CheckAsync(force: false, http.RequestAborted);
            return Json(Describe(updater, check, error, restart is not null));
        });

        app.MapPost("/api/update/restart", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (restart is null) return Error(StatusCodes.Status409Conflict, "Start fmstudio again to use the new version.");
            if (updater.Install is not { State: "succeeded" }) return Error(StatusCodes.Status409Conflict, "No update has been installed.");
            try { restart(); }
            catch (Exception ex)
            {
                // The new files are in place either way: say why the restart did not happen instead of an empty 500.
                return Error(StatusCodes.Status500InternalServerError, $"Could not restart ({ex.GetType().Name}: {ex.Message}). Close the studio and start it again to use the new version.");
            }
            return Results.Accepted();
        });
    }
}
