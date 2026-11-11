using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <param name="Versions">Every headless release the updater knows, newest first ("2.1.24", "2.1.23", ...).</param>
/// <param name="Stable">The current stable release, when the updater names one.</param>
/// <param name="Experimental">The current experimental release, when the updater names one.</param>
public sealed record AvailableReleases(IReadOnlyList<string> Versions, string? Stable, string? Experimental);

/// <param name="Version">"2.0.72".</param>
/// <param name="Path">The installation folder (holds <c>bin/x64/factorio</c> and <c>data/base/info.json</c>).</param>
/// <param name="AlreadyInstalled">True when nothing was downloaded because the version was already there.</param>
/// <param name="Game">The full game (with graphics, downloaded with a factorio.com account), not the headless server.</param>
public sealed record ManagedInstallation(string Version, string Path, string Executable, bool AlreadyInstalled = false, bool Game = false);

/// <summary>
/// Installs Factorio's headless server (free, no account) into the studio's own folder,
/// <see cref="FactorioInstallations.ManagedInstallationsDirectory"/>, where the normal discovery finds it, so <c>smoke</c> picks it up
/// for mods with a matching <c>factorio_version</c> (<c>run</c> needs the game with graphics and leaves it out, see
/// <see cref="FactorioInstallations.PickToPlay"/>).
/// The headless server exists for Linux only. Versions come from Factorio's updater API, the archive from the documented download
/// address (https://wiki.factorio.com/Download_API). Nothing is started except the system's <c>tar</c> to unpack; a version is
/// accepted only as MAJOR.MINOR.PATCH, so it can never change the URL or the target folder; the unpacked game must report that
/// same version in <c>data/base/info.json</c> before it is moved into place, and a failed install leaves nothing behind.
/// The full game (<see cref="InstallGameAsync"/>) comes the same way from the same address with "alpha" instead of "headless"; it
/// needs the username and service token of an account that owns Factorio (<see cref="FactorioAccount"/>), which travel only to
/// factorio.com over HTTPS and are left out of every message. It goes into "&lt;version&gt;-game", next to the headless servers.
/// </summary>
public sealed class FactorioDownloads
{
    public const string UpdaterUrl = "https://updater.factorio.com/get-available-versions?apiVersion=2";
    public const string HeadlessPackage = "core-linux_headless64";
    public const long MaxDownloadBytes = 512L * 1024 * 1024;
    /// <summary>The full game is much larger than the headless server (graphics, sounds and the expansion).</summary>
    public const long MaxGameDownloadBytes = 8L * 1024 * 1024 * 1024;
    private const string FolderSuffix = "-headless";
    private const string GameFolderSuffix = "-game";
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(10);
    /// <summary>A download that receives nothing for this long is given up (the HTTP client itself has no timeout).</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static readonly Regex VersionPattern = new(@"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BranchPattern = new(@"^[0-9]{1,4}\.[0-9]{1,4}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly IProcessRunner _runner;
    private readonly string _directory;
    private readonly Func<string?> _platformProblem;
    private readonly Func<(string? Platform, string? Problem)> _gamePlatform;
    private readonly TimeSpan _stallTimeout;

    /// <param name="directory">Where installations go; normally <see cref="FactorioInstallations.ManagedInstallationsDirectory"/>.</param>
    /// <param name="platformProblem">Why this machine cannot run the headless server, or null when it can. Tests replace it.</param>
    /// <param name="stallTimeout">How long a download may receive nothing; <see cref="StallTimeout"/> by default.</param>
    /// <param name="gamePlatform">The full game's package for this machine, or why there is none (<see cref="GamePlatform"/>). Tests replace it.</param>
    public FactorioDownloads(HttpClient http, IProcessRunner runner, string directory, Func<string?>? platformProblem = null, TimeSpan? stallTimeout = null,
        Func<(string? Platform, string? Problem)>? gamePlatform = null)
    {
        _http = http;
        _runner = runner;
        _directory = Path.GetFullPath(directory);
        _platformProblem = platformProblem ?? HeadlessPlatformProblem;
        _gamePlatform = gamePlatform ?? GamePlatform;
        _stallTimeout = stallTimeout ?? StallTimeout;
    }

    /// <summary>Why the full game cannot be installed on this machine, or null when it can.</summary>
    public string? GamePlatformProblem => _gamePlatform().Problem;

    /// <summary>
    /// The full game's download package for this machine: "win64-manual" (a zip, no installer) on Windows x64, "linux64" (tar.xz) on
    /// Linux x64. macOS gets a disk image that would have to be mounted, so it is not offered.
    /// </summary>
    public static (string? Platform, string? Problem) GamePlatform()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return (null, $"The studio installs the full game for x64 Windows and Linux; this machine is {RuntimeInformation.OSArchitecture}.");
        if (OperatingSystem.IsWindows()) return ("win64-manual", null);
        if (OperatingSystem.IsLinux()) return ("linux64", null);
        return (null, "On macOS the full game comes as a disk image; install it from factorio.com or Steam and add it in the installations list.");
    }

    /// <summary>The full game's download address, without the account (see <see cref="InstallGameAsync"/>).</summary>
    public static string GameUrl(string version, string platform) =>
        IsVersion(version) && platform is "win64-manual" or "linux64"
            ? $"https://www.factorio.com/get-download/{version}/alpha/{platform}"
            : throw new ArgumentException("Not a MAJOR.MINOR.PATCH version or not a known package.", nameof(version));

    public string Directory => _directory;

    /// <summary>Why the headless server cannot run on this machine, or null when it can.</summary>
    public string? PlatformProblem => _platformProblem();

    /// <summary>The download address of a headless release. <paramref name="version"/> must be MAJOR.MINOR.PATCH.</summary>
    public static string HeadlessUrl(string version) =>
        IsVersion(version) ? $"https://www.factorio.com/get-download/{version}/headless/linux64" : throw new ArgumentException("Not a MAJOR.MINOR.PATCH version.", nameof(version));

    public static bool IsVersion(string? text) => text is not null && VersionPattern.IsMatch(text);

    /// <summary>What <see cref="Resolve"/> accepts: MAJOR.MINOR.PATCH, MAJOR.MINOR, "stable" or "latest".</summary>
    public static bool IsVersionSpec(string? text)
    {
        var value = text?.Trim().ToLowerInvariant();
        return value is "stable" or "latest" || IsVersion(value) || (value is not null && BranchPattern.IsMatch(value));
    }

    /// <summary>
    /// True when <paramref name="executable"/> belongs to a headless server installed by the studio (a "&lt;version&gt;-headless" folder
    /// directly in <paramref name="managedDirectory"/>, by default <see cref="FactorioInstallations.ManagedInstallationsDirectory"/>).
    /// </summary>
    public static bool IsHeadlessInstall(string executable, string? managedDirectory = null) => IsManaged(executable, FolderSuffix, managedDirectory);

    /// <summary>True when <paramref name="executable"/> belongs to a full game installed by the studio ("&lt;version&gt;-game").</summary>
    public static bool IsGameInstall(string executable, string? managedDirectory = null) => IsManaged(executable, GameFolderSuffix, managedDirectory);

    private static bool IsManaged(string executable, string suffix, string? managedDirectory)
    {
        try
        {
            var root = FactorioInstallations.InstallRoot(executable);
            if (root is null) return false;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            if (!name.EndsWith(suffix, StringComparison.Ordinal) || !IsVersion(name[..^suffix.Length])) return false;
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root));
            var managed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedDirectory ?? FactorioInstallations.ManagedInstallationsDirectory()));
            return parent is not null && string.Equals(parent, managed, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Null on Linux x64; otherwise why the headless server cannot run here and what to do instead.</summary>
    public static string? HeadlessPlatformProblem()
    {
        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64) return null;
        if (OperatingSystem.IsLinux())
            return $"Factorio's headless server is published for Linux x64; this machine is {RuntimeInformation.OSArchitecture}.";
        var system = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;
        return $"Factorio's headless server exists for Linux only, and this is {system}. " +
               (OperatingSystem.IsWindows() ? "Run fmstudio inside WSL to use it, " : "Run fmstudio on Linux (or in a Linux container) to use it, ") +
               "or use a Factorio you installed yourself (see `fmstudio factorio`).";
    }

    // ------------------------------------------------------------------ versions

    /// <summary>Reads the updater's answer: every "from"/"to" of <see cref="HeadlessPackage"/>, plus its stable / experimental entry.</summary>
    public static AvailableReleases ParseAvailable(string json)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        string? stable = null, experimental = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(HeadlessPackage, out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new FmsException($"Factorio's updater answered without a \"{HeadlessPackage}\" list.");

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                foreach (var name in new[] { "from", "to" })
                    if (StringProperty(entry, name) is { } v && IsVersion(v)) versions.Add(v);
                if (StringProperty(entry, "stable") is { } s && IsVersion(s)) { stable = s; versions.Add(s); }
                if (StringProperty(entry, "experimental") is { } e && IsVersion(e)) { experimental = e; versions.Add(e); }
            }
        }
        catch (JsonException ex)
        {
            throw new FmsException($"Factorio's updater answered with something that is not JSON: {ex.Message}", ex);
        }

        return new AvailableReleases(versions.OrderByDescending(Key).ToList(), stable, experimental);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (int, int, int) Key(string version)
    {
        var parts = version.Split('.');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Turns what the user typed into a release: "2.0.72" (must exist), "2.0" (its newest release), "stable" or "latest".
    /// Null when nothing matches; an <see cref="FmsException"/> when the text is none of these forms.
    /// </summary>
    public static string? Resolve(AvailableReleases releases, string spec)
    {
        var text = spec.Trim().ToLowerInvariant();
        if (text is "stable") return releases.Stable ?? releases.Versions.FirstOrDefault();
        if (text is "latest") return releases.Versions.FirstOrDefault();
        if (IsVersion(text)) return releases.Versions.Contains(text) ? text : null;
        if (BranchPattern.IsMatch(text)) return releases.Versions.FirstOrDefault(v => v.StartsWith(text + ".", StringComparison.Ordinal));
        throw new FmsException($"\"{spec}\" is not a Factorio version. Use MAJOR.MINOR (newest release of that branch, e.g. 2.0), MAJOR.MINOR.PATCH (e.g. 2.0.72), \"stable\" or \"latest\".");
    }

    public async Task<AvailableReleases> AvailableAsync(CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            using var response = await _http.GetAsync(UpdaterUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new FmsException($"Factorio's updater answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new FmsException($"Could not reach Factorio's updater ({UpdaterUrl}): {ex.Message}", ex);
        }
        return ParseAvailable(json);
    }

    // ------------------------------------------------------------------ installed

    /// <summary>The folder a version is (or would be) installed in.</summary>
    public string FolderOf(string version) =>
        IsVersion(version) ? Path.Combine(_directory, version + FolderSuffix) : throw new ArgumentException("Not a MAJOR.MINOR.PATCH version.", nameof(version));

    /// <summary>The folder the full game of a version is (or would be) installed in.</summary>
    public string GameFolderOf(string version) =>
        IsVersion(version) ? Path.Combine(_directory, version + GameFolderSuffix) : throw new ArgumentException("Not a MAJOR.MINOR.PATCH version.", nameof(version));

    /// <summary>Headless installations made by the studio, newest first.</summary>
    public IReadOnlyList<ManagedInstallation> Installed() => List(FolderSuffix, game: false);

    /// <summary>Full games installed by the studio, newest first.</summary>
    public IReadOnlyList<ManagedInstallation> InstalledGames() => List(GameFolderSuffix, game: true);

    private List<ManagedInstallation> List(string suffix, bool game)
    {
        var list = new List<ManagedInstallation>();
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return list;
            foreach (var folder in System.IO.Directory.EnumerateDirectories(_directory))
            {
                var name = Path.GetFileName(folder);
                if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var version = name[..^suffix.Length];
                if (!IsVersion(version) || (new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (FactorioLocator.ResolveExecutable(folder) is { } exe) list.Add(new ManagedInstallation(version, folder, exe, Game: game));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable folder holds nothing usable */ }
        return list.OrderByDescending(i => Key(i.Version)).ToList();
    }

    /// <summary>Deletes a headless installation made by the studio. False when that version is not installed.</summary>
    public bool Remove(string version) => RemoveFolder(version, game: false);

    /// <summary>Deletes a full game installed by the studio. False when that version is not installed.</summary>
    public bool RemoveGame(string version) => RemoveFolder(version, game: true);

    private bool RemoveFolder(string version, bool game)
    {
        if (!IsVersion(version)) throw new FmsException($"\"{version}\" is not a MAJOR.MINOR.PATCH version.");
        var folder = game ? GameFolderOf(version) : FolderOf(version);
        if (!System.IO.Directory.Exists(folder)) return false;
        if ((new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FmsException($"\"{folder}\" is a link; it is not removed.");
        System.IO.Directory.Delete(folder, recursive: true);
        return true;
    }

    // ------------------------------------------------------------------ install

    /// <summary>
    /// Downloads and unpacks one headless release. <paramref name="progress"/> gets (bytes received, total when known).
    /// Installing a version that is already there downloads nothing.
    /// </summary>
    public async Task<ManagedInstallation> InstallHeadlessAsync(string version, IProgress<(long Received, long? Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsVersion(version)) throw new FmsException($"\"{version}\" is not a MAJOR.MINOR.PATCH version.");
        if (_platformProblem() is { } problem) throw new FmsException(problem);

        var target = FolderOf(version);
        if (FactorioLocator.ResolveExecutable(target) is { } existing) return new ManagedInstallation(version, target, existing, AlreadyInstalled: true);
        if (System.IO.Directory.Exists(target))
            throw new FmsException($"\"{target}\" exists but holds no Factorio; remove it first (fmstudio factorio remove {version}).");

        System.IO.Directory.CreateDirectory(_directory);
        if ((new DirectoryInfo(_directory).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FmsException($"\"{_directory}\" is a link; refusing to install into it.");

        // Everything happens in a staging folder next to the target; only a verified game is moved into place.
        var staging = Path.Combine(_directory, $".staging-{Guid.NewGuid():N}");
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, "factorio-headless.tar.xz");
            await DownloadAsync(HeadlessUrl(version), HeadlessUrl(version), archive, MaxDownloadBytes, "headless", progress, cancellationToken).ConfigureAwait(false);

            var unpacked = Path.Combine(staging, "unpacked");
            System.IO.Directory.CreateDirectory(unpacked);
            var result = _runner.Run("tar", new[] { "-xf", archive, "-C", unpacked }, ExtractTimeout);
            if (result.TimedOut || result.ExitCode != 0)
                throw new FmsException($"Unpacking the archive failed (tar exit code {result.ExitCode}){(result.Lines.Count > 0 ? ": " + string.Join(" ", result.Lines.Take(3)) : ".")}");

            var root = Path.Combine(unpacked, "factorio");
            var exe = FactorioLocator.ResolveExecutable(root);
            if (exe is null) throw new FmsException("The archive does not contain factorio/bin/x64/factorio.");
            var found = FactorioInstallations.DetectVersion(exe);
            if (found != version) throw new FmsException($"The archive contains Factorio {found ?? "of unknown version"}, not {version}; nothing was installed.");

            try { System.IO.Directory.Move(root, target); }
            catch (IOException) when (FactorioLocator.ResolveExecutable(target) is not null)
            {
                // Another install of the same version finished first; theirs is as good as ours.
                return new ManagedInstallation(version, target, FactorioLocator.ResolveExecutable(target)!, AlreadyInstalled: true);
            }
            return new ManagedInstallation(version, target, FactorioLocator.ResolveExecutable(target)!);
        }
        finally
        {
            try { if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Downloads and unpacks the full game of one release with a factorio.com account. Installing a version that is already there
    /// downloads nothing. A refused account (wrong token, or an account that does not own the game) is an <see cref="FmsException"/>
    /// that says so; nothing is left behind.
    /// </summary>
    public async Task<ManagedInstallation> InstallGameAsync(string version, FactorioCredentials credentials, IProgress<(long Received, long? Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!IsVersion(version)) throw new FmsException($"\"{version}\" is not a MAJOR.MINOR.PATCH version.");
        var (platform, problem) = _gamePlatform();
        if (platform is null) throw new FmsException(problem ?? "The full game cannot be installed on this machine.");
        if (!FactorioAccount.IsUsername(credentials.Username) || !FactorioAccount.IsToken(credentials.Token))
            throw new FmsException("The factorio.com username or token is not valid.");

        var target = GameFolderOf(version);
        if (FactorioLocator.ResolveExecutable(target) is { } existing) return new ManagedInstallation(version, target, existing, AlreadyInstalled: true, Game: true);
        if (System.IO.Directory.Exists(target))
            throw new FmsException($"\"{target}\" exists but holds no Factorio; remove it first.");

        System.IO.Directory.CreateDirectory(_directory);
        if ((new DirectoryInfo(_directory).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FmsException($"\"{_directory}\" is a link; refusing to install into it.");

        var shown = GameUrl(version, platform);
        var url = $"{shown}?username={Uri.EscapeDataString(credentials.Username)}&token={Uri.EscapeDataString(credentials.Token)}";
        var staging = Path.Combine(_directory, $".staging-{Guid.NewGuid():N}");
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            var zip = platform == "win64-manual";
            var archive = Path.Combine(staging, zip ? "factorio.zip" : "factorio.tar.xz");
            await DownloadAsync(url, shown, archive, MaxGameDownloadBytes, "game", progress, cancellationToken).ConfigureAwait(false);

            var unpacked = Path.Combine(staging, "unpacked");
            System.IO.Directory.CreateDirectory(unpacked);
            if (zip)
            {
                // ExtractToDirectory refuses entries that would land outside the folder ("../"), so the archive cannot write elsewhere.
                try { await Task.Run(() => ZipFile.ExtractToDirectory(archive, unpacked), cancellationToken).ConfigureAwait(false); }
                catch (InvalidDataException ex) { throw new FmsException($"The downloaded game is not a valid zip archive: {ex.Message}", ex); }
            }
            else
            {
                var result = _runner.Run("tar", new[] { "-xf", archive, "-C", unpacked }, ExtractTimeout);
                if (result.TimedOut || result.ExitCode != 0)
                    throw new FmsException($"Unpacking the archive failed (tar exit code {result.ExitCode}){(result.Lines.Count > 0 ? ": " + string.Join(" ", result.Lines.Take(3)) : ".")}");
            }

            // The zip holds "Factorio_2.0.72", the tar "factorio": take the one folder that holds a game.
            var roots = System.IO.Directory.EnumerateDirectories(unpacked)
                .Where(d => File.Exists(Path.Combine(d, "data", "base", "info.json")) && FactorioLocator.ResolveExecutable(d) is not null)
                .ToList();
            if (roots.Count != 1) throw new FmsException("The archive does not contain one Factorio installation folder.");
            var exe = FactorioLocator.ResolveExecutable(roots[0])!;
            var found = FactorioInstallations.DetectVersion(exe);
            if (found != version) throw new FmsException($"The archive contains Factorio {found ?? "of unknown version"}, not {version}; nothing was installed.");

            try { System.IO.Directory.Move(roots[0], target); }
            catch (IOException) when (FactorioLocator.ResolveExecutable(target) is not null)
            {
                return new ManagedInstallation(version, target, FactorioLocator.ResolveExecutable(target)!, AlreadyInstalled: true, Game: true);
            }
            return new ManagedInstallation(version, target, FactorioLocator.ResolveExecutable(target)!, Game: true);
        }
        finally
        {
            try { if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <param name="url">What is requested; may carry the account, so it never appears in a message.</param>
    /// <param name="shown">The same address without the account, for messages.</param>
    /// <param name="what">"headless" or "game", for messages.</param>
    private async Task DownloadAsync(string url, string shown, string file, long maxBytes, string what, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken)
    {
        var limit = $"The download is larger than {maxBytes / (1024 * 1024)} MB; refusing it.";
        // Cancelled by the caller, or by silence: restarted after every chunk, so a slow download is fine and a dead one is not.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            var game = what == "game";
            if (game && response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new FmsException(RefusedAccount);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new FmsException($"Factorio has no {what} download at {shown}.");
            if (!response.IsSuccessStatusCode)
                throw new FmsException($"The download failed: {(int)response.StatusCode} {response.ReasonPhrase} ({shown}).");
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new FmsException($"The download was redirected to a non-HTTPS address ({final.GetLeftPart(UriPartial.Path)}); refusing it.");
            // A token factorio.com does not accept ends on its login page, an HTML page, instead of the archive.
            if (game && response.Content.Headers.ContentType?.MediaType is { } media && media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                throw new FmsException(RefusedAccount);

            var total = response.Content.Headers.ContentLength;
            if (total > maxBytes) throw new FmsException(limit);

            await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(_stallTimeout);
                received += read;
                if (received > maxBytes) throw new FmsException(limit);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report((received, total));
            }
            if (received == 0) throw new FmsException($"The download from {shown} was empty.");
        }
        catch (HttpRequestException ex)
        {
            // The handler's message can quote the address it asked for; the account must not end up in a log.
            throw new FmsException($"Could not download {shown}: {Redact(ex.Message, url, shown)}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FmsException($"The download from {shown} stalled: nothing arrived for {_stallTimeout.TotalSeconds:0} seconds.", ex);
        }
    }

    private const string RefusedAccount =
        "factorio.com did not accept the username and token, or this account does not own Factorio. Check them on your profile at factorio.com.";

    private static string Redact(string message, string url, string shown) => url == shown ? message : message.Replace(url, shown, StringComparison.Ordinal);
}
