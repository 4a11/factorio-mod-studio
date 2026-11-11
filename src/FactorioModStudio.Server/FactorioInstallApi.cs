using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioModStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FactorioModStudio.Server;

/// <summary>Where an install is: "downloading", "unpacking", "succeeded", "failed" or "cancelled". <paramref name="Game"/>: the full game.</summary>
internal sealed record InstallSnapshot(string Version, string State, long Received, long? Total, string? Error, string? Path, bool AlreadyInstalled, bool Game = false);

/// <summary>
/// The Build tab's "install a missing Factorio": one install at a time (the headless server, or the full game with a factorio.com
/// account), run in the background and polled by the page.
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
    /// <param name="playerDataFile">The game's player-data.json; null is the real one (<see cref="FactorioAccount.GamePlayerDataPath"/>).</param>
    /// <param name="accountFile">The studio's saved account; null is <see cref="FactorioAccount.StorePath"/>.</param>
    public FactorioInstalls(Func<FactorioDownloads> create, Action changed, string? playerDataFile = null, string? accountFile = null)
    {
        _create = create;
        _changed = changed;
        PlayerDataFile = playerDataFile;
        AccountFile = accountFile ?? FactorioAccount.StorePath();
    }

    public string? PlayerDataFile { get; }
    public string AccountFile { get; }

    public FactorioAccountStatus AccountStatus() => FactorioAccount.Status(AccountFile, PlayerDataFile);

    public FactorioCredentials? Credentials() => FactorioAccount.Resolve(AccountFile, PlayerDataFile);

    private readonly Queue<DateTime> _logins = new();

    /// <summary>
    /// False when there were already 5 login attempts in the last minute: factorio.com rate-limits clients that fail often, and a
    /// page (or a script on this machine with the session token) must not be able to hammer it with passwords.
    /// </summary>
    public bool TryCountLogin()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            while (_logins.Count > 0 && now - _logins.Peek() > TimeSpan.FromMinutes(1)) _logins.Dequeue();
            if (_logins.Count >= 5) return false;
            _logins.Enqueue(now);
            return true;
        }
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
    /// <param name="credentials">Given: the full game with this account; null: the headless server.</param>
    public InstallSnapshot? Start(string version, FactorioCredentials? credentials = null)
    {
        var game = credentials is not null;
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_current is { State: "downloading" or "unpacking" }) return null;
            _cancel?.Dispose();
            _cancel = cancel = new CancellationTokenSource();
            _current = new InstallSnapshot(version, "downloading", 0, null, null, null, false, game);
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
                var result = credentials is not null
                    ? await downloads.InstallGameAsync(version, credentials, progress, cancel.Token).ConfigureAwait(false)
                    : await downloads.InstallHeadlessAsync(version, progress, cancel.Token).ConfigureAwait(false);
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

    public bool Remove(string version, bool game = false)
    {
        lock (_gate)
            if (_current is { State: "downloading" or "unpacking" } c && c.Version == version && c.Game == game)
                throw new FmsException($"Factorio {version} is being installed right now.");
        var removed = game ? _create().RemoveGame(version) : _create().Remove(version);
        if (removed) _changed();
        return removed;
    }

    private sealed class SyncProgress(Action<(long Received, long? Total)> report) : IProgress<(long Received, long? Total)>
    {
        public void Report((long Received, long? Total) value) => report(value);
    }
}

/// <summary>
/// <c>/api/factorio/available</c>, <c>/api/factorio/install</c> (start, progress, cancel), removing an install and
/// <c>/api/factorio/account</c> (the factorio.com account for the full game; the token goes in, never out). Writes pass the
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
        new { version = s.Version, state = s.State, received = s.Received, total = s.Total, error = s.Error, path = s.Path, alreadyInstalled = s.AlreadyInstalled, game = s.Game };

    /// <summary><c>{ source, username, gameUsername, useGame }</c>: which account a full-game download uses; never the token.</summary>
    private static object Account(FactorioAccountStatus a) => new { source = a.Source, username = a.Username, gameUsername = a.GameUsername, useGame = a.UseGame };

    private static async Task<(JsonDocument? Body, IResult? Error)> ReadBody(HttpContext http, long limit)
    {
        if (http.Request.ContentLength > limit) return (null, Error(StatusCodes.Status413PayloadTooLarge, $"The request is too large (limit {limit} bytes)."));
        byte[] body;
        try { body = await BuildApi.ReadLimited(http.Request.Body, limit, http.RequestAborted); }
        catch (InvalidDataException) { return (null, Error(StatusCodes.Status413PayloadTooLarge, $"The request is too large (limit {limit} bytes).")); }
        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return (document, null);
            document.Dispose();
        }
        catch (JsonException) { return (null, Error(StatusCodes.Status400BadRequest, "The body is not valid JSON.")); }
        return (null, Error(StatusCodes.Status400BadRequest, "The body must be a JSON object."));
    }

    /// <summary><c>{ "install": { version, state, received, total, error, path, alreadyInstalled } | null }</c></summary>
    private static object Describe(InstallSnapshot? s) => new { install = Inner(s) };

    public static void Map(WebApplication app, FactorioInstalls installs)
    {
        app.MapGet("/api/factorio/available", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var downloads = installs.Downloads();
            var installed = downloads.Installed().Select(i => i.Version).ToList();
            var gameProblem = downloads.GamePlatformProblem;
            var game = new
            {
                platformProblem = gameProblem,
                installed = downloads.InstalledGames().Select(i => i.Version).ToList(),
                account = Account(installs.AccountStatus())
            };
            // Where neither the headless server nor the game can be installed there is nothing to offer, and no reason to ask Factorio's servers.
            if (downloads.PlatformProblem is { } problem && gameProblem is not null)
                return Json(new { platformProblem = problem, stable = (string?)null, experimental = (string?)null, branches = Array.Empty<object>(), installed, game });

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
            return Json(new { platformProblem = downloads.PlatformProblem, releases.Stable, releases.Experimental, branches, installed, game });
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
            var game = false;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.String)
                    return Error(StatusCodes.Status400BadRequest, "The body must be an object with a \"version\" string (MAJOR.MINOR or MAJOR.MINOR.PATCH).");
                spec = value.GetString();
                if (document.RootElement.TryGetProperty("game", out var kind))
                {
                    if (kind.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Error(StatusCodes.Status400BadRequest, "\"game\" must be true or false.");
                    game = kind.GetBoolean();
                }
            }
            catch (JsonException) { return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON."); }

            if (!FactorioDownloads.IsVersionSpec(spec))
                return Error(StatusCodes.Status400BadRequest, "The version must be MAJOR.MINOR (newest release of that branch), MAJOR.MINOR.PATCH, \"stable\" or \"latest\".");
            var downloads = installs.Downloads();
            FactorioCredentials? credentials = null;
            if (game)
            {
                if (downloads.GamePlatformProblem is { } gameProblem) return Error(StatusCodes.Status400BadRequest, gameProblem);
                credentials = installs.Credentials();
                if (credentials is null)
                    return Error(StatusCodes.Status400BadRequest, "No factorio.com account is set: enter your username and token, or use the account Factorio is signed in to.");
            }
            else if (downloads.PlatformProblem is { } problem) return Error(StatusCodes.Status400BadRequest, problem);

            string? version;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                version = FactorioDownloads.Resolve(await installs.ReleasesAsync(timeout.Token), spec!);
            }
            catch (FmsException ex) { return Error(StatusCodes.Status502BadGateway, ex.Message); }
            catch (OperationCanceledException) { return Error(StatusCodes.Status504GatewayTimeout, "Factorio's updater did not answer within 30 seconds."); }
            if (version is null) return Error(StatusCodes.Status404NotFound, $"Factorio has no release matching \"{spec}\".");

            var started = installs.Start(version, credentials);
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
            var game = http.Request.Query["game"] == "true";
            try
            {
                return installs.Remove(version, game)
                    ? Json(new { removed = version, game })
                    : Error(StatusCodes.Status404NotFound, $"Factorio {version} was not installed by the studio.");
            }
            catch (FmsException ex) { return Error(StatusCodes.Status409Conflict, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not remove it: " + ex.Message); }
        });

        app.MapGet("/api/factorio/account", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Json(new { account = Account(installs.AccountStatus()) });
        });

        // { "username": "...", "token": "..." } saves an account; { "useGame": true } uses the one the installed game is signed in to.
        app.MapPut("/api/factorio/account", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (document, error) = await ReadBody(http, MaxBodyBytes);
            if (error is not null) return error;
            using (document)
            {
                var root = document!.RootElement;
                try
                {
                    if (root.TryGetProperty("useGame", out var useGame) && useGame.ValueKind == JsonValueKind.True)
                    {
                        if (FactorioAccount.FromGame(installs.PlayerDataFile) is null)
                            return Error(StatusCodes.Status409Conflict, "Factorio is not signed in to a factorio.com account on this computer (its player-data.json has no token).");
                        FactorioAccount.SaveUseGame(installs.AccountFile);
                    }
                    else if (root.TryGetProperty("username", out var user) && user.ValueKind == JsonValueKind.String
                             && root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String)
                        FactorioAccount.Save(user.GetString()!.Trim(), token.GetString()!.Trim(), installs.AccountFile);
                    else
                        return Error(StatusCodes.Status400BadRequest, "Send { \"username\", \"token\" } or { \"useGame\": true }.");
                }
                catch (FmsException ex) { return Error(StatusCodes.Status400BadRequest, ex.Message); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not save the account: " + ex.Message); }
            }
            return Json(new { account = Account(installs.AccountStatus()) });
        });

        // { "login", "password", "emailCode"? }: asks factorio.com for a token and saves it. The password is used for this one request
        // and kept nowhere (not logged, not saved, not sent back); only the token is saved, protected like a typed one.
        app.MapPost("/api/factorio/account/login", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (document, error) = await ReadBody(http, 4096);
            if (error is not null) return error;
            string? login, password, emailCode = null;
            using (document)
            {
                var root = document!.RootElement;
                if (!root.TryGetProperty("login", out var l) || l.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("password", out var p) || p.ValueKind != JsonValueKind.String)
                    return Error(StatusCodes.Status400BadRequest, "Send { \"login\", \"password\" } and, when factorio.com asked for it, \"emailCode\".");
                login = l.GetString();
                password = p.GetString();
                if (root.TryGetProperty("emailCode", out var c) && c.ValueKind == JsonValueKind.String) emailCode = c.GetString();
            }
            if (!installs.TryCountLogin()) return Error(StatusCodes.Status429TooManyRequests, "Too many login attempts; wait a minute and try again.");

            FactorioLoginResult result;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                result = await installs.Downloads().LoginAsync(login!, password!, emailCode, timeout.Token);
            }
            catch (OperationCanceledException) { return Error(StatusCodes.Status504GatewayTimeout, "factorio.com did not answer within 30 seconds."); }

            if (!result.Ok)
            {
                var status = result.Error switch
                {
                    "invalid-input" => StatusCodes.Status400BadRequest,
                    "unreachable" or "bad-answer" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status409Conflict,
                };
                return Json(new { error = result.Message, code = result.Error, needsEmailCode = result.NeedsEmailCode }, status);
            }
            try { FactorioAccount.Save(result.Credentials!.Username, result.Credentials.Token, installs.AccountFile); }
            catch (FmsException ex) { return Error(StatusCodes.Status502BadGateway, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not save the account: " + ex.Message); }
            return Json(new { account = Account(installs.AccountStatus()) });
        });

        app.MapDelete("/api/factorio/account", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try { FactorioAccount.Forget(installs.AccountFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not forget the account: " + ex.Message); }
            return Json(new { account = Account(installs.AccountStatus()) });
        });
    }
}
