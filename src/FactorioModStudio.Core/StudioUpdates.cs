using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <summary>
/// Which releases a studio is offered. The version number says it: X.Y.0 is a release (0.2.0, 0.3.0), X.Y.Z with Z &gt; 0 a preview
/// on the way to the next one (0.2.1, 0.2.2 ... before 0.3.0).
/// </summary>
public enum UpdateChannel { Stable, Preview }

/// <summary>How this copy of the studio was installed, which decides how it can be updated.</summary>
public enum InstallKind
{
    /// <summary>Unpacked from a release archive: the studio replaces its own files.</summary>
    ReleaseArchive,
    /// <summary>A .NET tool from NuGet: <c>dotnet tool update</c> does it.</summary>
    DotnetTool,
    /// <summary>Built from source, Nix or anything else: the studio only says that a new version is out.</summary>
    Other
}

/// <param name="Version">"0.3.0" (the tag without its "v").</param>
/// <param name="Notes">The release notes (Markdown): the version's section of CHANGELOG.md and the install steps.</param>
public sealed record StudioRelease(string Version, string Notes, string Url, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>When GitHub published the release ("published_at"); null when it does not say.</summary>
    public DateTimeOffset? Published { get; init; }
}

/// <param name="Sha256">The hex digest GitHub reports for the file, when it does.</param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <param name="Newer">The newest release of the channel that is newer than <paramref name="Current"/>; null when up to date.</param>
/// <param name="Command">For a .NET tool: the command that updates it.</param>
public sealed record UpdateCheck(string Current, UpdateChannel Channel, StudioRelease? Newer, InstallKind Kind, string? Command);

/// <summary>
/// Checks GitHub for a newer Factorio Mod Studio and, for a copy unpacked from a release archive, installs it: the archive for this
/// system is downloaded (HTTPS only, size limit, checked against GitHub's SHA-256 when it reports one), unpacked next to the
/// program, and its files swapped in. A running program can be renamed on every system, so the old files become "*.old" and are
/// deleted at the next start (<see cref="CleanUp"/>); a swap that fails half-way puts the old files back.
/// </summary>
public sealed class StudioUpdates
{
    public const string Repository = "4a11/factorio-mod-studio";
    public const string ReleasesUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=50";
    public const long MaxDownloadBytes = 400L * 1024 * 1024;
    private const string OldSuffix = ".old";
    private const string StagingPrefix = ".update-";
    /// <summary>A downloaded update waiting for the next start: <c>.pending-update/fmstudio/</c> and its <c>version</c>.</summary>
    private const string PendingFolder = ".pending-update";
    private const string PendingRoot = "fmstudio";
    private const string PendingVersionFile = "version";
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex TagPattern = new(@"^v?([0-9]{1,4})\.([0-9]{1,4})\.([0-9]{1,4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly IProcessRunner _runner;
    private readonly string _appDirectory;

    /// <param name="appDirectory">The folder the studio runs from; <see cref="AppContext.BaseDirectory"/> by default.</param>
    /// <param name="current">The running version; <see cref="CurrentVersion"/> by default.</param>
    public StudioUpdates(HttpClient http, IProcessRunner runner, string? appDirectory = null, string? current = null)
    {
        _http = http;
        _runner = runner;
        _appDirectory = Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory);
        Current = current ?? CurrentVersion;
        Kind = DetectKind(_appDirectory);
    }

    public string Current { get; }
    public InstallKind Kind { get; }
    public string AppDirectory => _appDirectory;

    /// <summary>The version of this build, "0.2.0".</summary>
    public static string CurrentVersion => typeof(StudioUpdates).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ------------------------------------------------------------------ versions and channels

    /// <summary>(major, minor, patch) of "0.2.1" or "v0.2.1"; null for anything else (pre-release suffixes included).</summary>
    public static (int Major, int Minor, int Patch)? Parse(string? version)
    {
        var match = version is null ? null : TagPattern.Match(version.Trim());
        if (match is not { Success: true }) return null;
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>True for a preview: a patch number above zero.</summary>
    public static bool IsPreview(string version) => Parse(version) is { Patch: > 0 };

    /// <summary>A preview keeps getting previews; a release gets releases.</summary>
    public static UpdateChannel DefaultChannel(string current) => IsPreview(current) ? UpdateChannel.Preview : UpdateChannel.Stable;

    public static UpdateChannel? ParseChannel(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "stable" or "release" => UpdateChannel.Stable,
        "preview" => UpdateChannel.Preview,
        _ => null
    };

    public static string ChannelName(UpdateChannel channel) => channel == UpdateChannel.Preview ? "preview" : "stable";

    /// <summary>Reads GitHub's release list; drafts and tags that are not vX.Y.Z are left out.</summary>
    public static IReadOnlyList<StudioRelease> ParseReleases(string json)
    {
        var list = new List<StudioRelease>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FmsException("GitHub answered without a list of releases.");
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object || Bool(release, "draft")) continue;
                var tag = Text(release, "tag_name");
                if (Parse(tag) is not { } v) continue;
                var assets = new List<ReleaseAsset>();
                if (release.TryGetProperty("assets", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var a in items.EnumerateArray())
                    {
                        if (Text(a, "name") is not { } name || Text(a, "browser_download_url") is not { } url) continue;
                        var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                        var digest = Text(a, "digest");
                        assets.Add(new ReleaseAsset(name, url, size,
                            digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null));
                    }
                list.Add(new StudioRelease($"{v.Major}.{v.Minor}.{v.Patch}", Text(release, "body") ?? "", Text(release, "html_url") ?? "", assets)
                {
                    Published = DateTimeOffset.TryParse(Text(release, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var published) ? published : null
                });
            }
        }
        catch (JsonException ex)
        {
            throw new FmsException($"GitHub answered with something that is not JSON: {ex.Message}", ex);
        }
        return list;
    }

    /// <summary>The newest release of <paramref name="channel"/> newer than <paramref name="current"/>; a preview channel sees releases too.</summary>
    public static StudioRelease? Newest(IEnumerable<StudioRelease> releases, UpdateChannel channel, string current)
    {
        var now = Parse(current) ?? (0, 0, 0);
        return releases
            .Where(r => channel == UpdateChannel.Preview || !IsPreview(r.Version))
            .Where(r => Parse(r.Version) is { } v && v.CompareTo(now) > 0)
            .OrderByDescending(r => Parse(r.Version))
            .FirstOrDefault();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // ------------------------------------------------------------------ how it is installed

    /// <summary>
    /// A release archive holds fmstudio, FactorioModStudio and LICENSE side by side (and is not a build folder); a .NET tool lives in
    /// the tools store; anything else (a build from source, Nix) is <see cref="InstallKind.Other"/>.
    /// </summary>
    public static InstallKind DetectKind(string appDirectory)
    {
        var path = appDirectory.Replace('\\', '/');
        if (path.Contains("/.store/factoriomodstudio.cli/", StringComparison.OrdinalIgnoreCase)) return InstallKind.DotnetTool;
        if (path.StartsWith("/nix/store/", StringComparison.Ordinal) || Regex.IsMatch(path, "/bin/(Debug|Release)/", RegexOptions.IgnoreCase))
            return InstallKind.Other;
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        return File.Exists(Path.Combine(appDirectory, "fmstudio" + exe)) &&
               File.Exists(Path.Combine(appDirectory, "FactorioModStudio" + exe)) &&
               File.Exists(Path.Combine(appDirectory, "LICENSE"))
            ? InstallKind.ReleaseArchive
            : InstallKind.Other;
    }

    /// <summary>The command that updates the .NET tool. NuGet carries releases only, so it is the same for both channels.</summary>
    public const string ToolCommand = "dotnet tool update -g FactorioModStudio.Cli";

    /// <summary>The release archive's runtime identifier for this machine; null where no archive is published.</summary>
    public static string? RuntimeId()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows() && arch == Architecture.X64) return "win-x64";
        if (OperatingSystem.IsLinux() && arch == Architecture.X64) return "linux-x64";
        if (OperatingSystem.IsMacOS()) return arch == Architecture.Arm64 ? "osx-arm64" : arch == Architecture.X64 ? "osx-x64" : null;
        return null;
    }

    /// <summary>"fmstudio-v0.3.0-win-x64.zip", as the release workflow names it.</summary>
    public static string AssetName(string version, string runtimeId) =>
        $"fmstudio-v{version}-{runtimeId}" + (runtimeId.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");

    // ------------------------------------------------------------------ check

    public async Task<IReadOnlyList<StudioRelease>> ReleasesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (request.Headers.UserAgent.Count == 0 && _http.DefaultRequestHeaders.UserAgent.Count == 0)
                request.Headers.UserAgent.ParseAdd($"FactorioModStudio/{Current}");   // GitHub refuses requests without one
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new FmsException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            return ParseReleases(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            throw new FmsException($"Could not reach GitHub ({ReleasesUrl}): {ex.Message}", ex);
        }
    }

    public async Task<UpdateCheck> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        var newer = Newest(await ReleasesAsync(cancellationToken).ConfigureAwait(false), channel, Current);
        return new UpdateCheck(Current, channel, newer, Kind, Kind == InstallKind.DotnetTool ? ToolCommand : null);
    }

    // ------------------------------------------------------------------ install

    /// <summary>
    /// Replaces this copy's files with <paramref name="release"/>'s archive for this system right away. Only for
    /// <see cref="InstallKind.ReleaseArchive"/>, and only for a program that ends right after it: a single-file program loads its
    /// libraries from its own file on demand, so one that keeps running after the swap fails on the first library it has not
    /// loaded yet. The studio's window and `fmstudio update` use <see cref="PrepareAsync"/> and <see cref="ApplyPendingAtStart"/>.
    /// <paramref name="progress"/> gets (bytes received, total).
    /// </summary>
    public async Task ApplyAsync(StudioRelease release, IProgress<(long Received, long? Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var (staging, root) = await DownloadAndUnpackAsync(release, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Swap(root, _appDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FmsException($"The studio's folder cannot be written ({_appDirectory}): {ex.Message}", ex);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    /// <summary>
    /// Downloads and unpacks <paramref name="release"/> into the pending-update folder next to the program, touching nothing that
    /// runs. The next start of the window or of fmstudio swaps it in (<see cref="ApplyPendingAtStart"/>); "Restart now" is such a
    /// start. A pending update of another version is replaced.
    /// </summary>
    public async Task PrepareAsync(StudioRelease release, IProgress<(long Received, long? Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var (staging, root) = await DownloadAndUnpackAsync(release, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = Path.Combine(_appDirectory, PendingFolder);
            DeleteQuietly(pending);
            if (Directory.Exists(pending)) throw new FmsException($"An earlier pending update could not be removed ({pending}).");
            Directory.CreateDirectory(pending);
            Directory.Move(root, Path.Combine(pending, PendingRoot));
            File.WriteAllText(Path.Combine(pending, PendingVersionFile), release.Version); // written last: a complete folder has it
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FmsException($"The studio's folder cannot be written ({_appDirectory}): {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new FmsException($"The update could not be put next to the studio ({_appDirectory}): {ex.Message}", ex);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    /// <summary>The version waiting in the pending-update folder, or null when there is none (or it is incomplete).</summary>
    public static string? PendingVersion(string? appDirectory = null)
    {
        var pending = Path.Combine(appDirectory ?? AppContext.BaseDirectory, PendingFolder);
        try
        {
            var file = Path.Combine(pending, PendingVersionFile);
            if (!File.Exists(file) || !Directory.Exists(Path.Combine(pending, PendingRoot))) return null;
            return Parse(File.ReadAllText(file).Trim()) is { } v ? $"{v.Major}.{v.Minor}.{v.Patch}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// For the first lines of Program.Main (the window and fmstudio): swaps in a pending update and starts the new program with
    /// the same arguments. Returns the exit code to end with, or null to go on as usual (no update waiting, another studio of this
    /// folder still running, or the swap failed and was undone). Nothing is loaded lazily between the swap and the start of the
    /// new program, see <see cref="ApplyAsync"/>.
    /// </summary>
    /// <param name="programName">"FactorioModStudio" or "fmstudio" (without ".exe").</param>
    /// <param name="waitForExit">fmstudio waits for the new program and passes its exit code on (its console stays); the window
    /// does not.</param>
    /// <param name="waitForPid">A process to wait for first: the window that asked for the restart.</param>
    public static int? ApplyPendingAtStart(string programName, string[] args, bool waitForExit, int? waitForPid = null)
    {
        var folder = AppContext.BaseDirectory;
        try
        {
            if (waitForPid is { } pid)
            {
                try { using var parent = System.Diagnostics.Process.GetProcessById(pid); parent.WaitForExit(30_000); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
            }
            if (PendingVersion(folder) is null || DetectKind(folder) != InstallKind.ReleaseArchive) return null;
            if (OtherStudioRunning(folder)) return null; // its program files must not change under it; next time

            // Everything the start of the new program needs is loaded now, while this program's file is still in place.
            var exe = OperatingSystem.IsWindows() ? ".exe" : "";
            var program = Path.Combine(folder, programName + exe);
            var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = Directory.GetCurrentDirectory() };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            WarmUpProcessStart();

            var pending = Path.Combine(folder, PendingFolder);
            Swap(Path.Combine(pending, PendingRoot), folder);
            DeleteQuietly(pending);

            using var next = System.Diagnostics.Process.Start(start);
            if (next is null) return 1;
            if (!waitForExit) return 0;
            next.WaitForExit();
            return next.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FmsException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // Swap undoes a half-done swap; the old version starts and the update waits for the next time
        }
    }

    /// <summary>True when another FactorioModStudio or fmstudio process runs from <paramref name="folder"/>.</summary>
    private static bool OtherStudioRunning(string folder)
    {
        var self = Environment.ProcessId;
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var name in new[] { "FactorioModStudio", "fmstudio" })
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                using (process)
                {
                    if (process.Id == self) continue;
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is not null && string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), full,
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            return true;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { /* not ours to see */ }
                }
        return false;
    }

    /// <summary>Starts a program that does nothing, so every library starting a process needs is loaded before the swap.</summary>
    private static void WarmUpProcessStart()
    {
        _ = typeof(System.ComponentModel.Win32Exception).Assembly;
        var info = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { ArgumentList = { "/c", "exit", "0" } }
            : new System.Diagnostics.ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "true" } };
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        try { using var p = System.Diagnostics.Process.Start(info); p?.WaitForExit(5000); _ = p?.ExitCode; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* the real start reports its own error */ }
    }

    private static void DeleteQuietly(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; CleanUp gets staging folders next time */ }
    }

    /// <summary>Downloads the archive for this system into a new staging folder and unpacks it; the caller deletes the folder.</summary>
    private async Task<(string Staging, string Root)> DownloadAndUnpackAsync(StudioRelease release, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken)
    {
        if (Kind != InstallKind.ReleaseArchive)
            throw new FmsException(Kind == InstallKind.DotnetTool
                ? $"This studio is a .NET tool; update it with `{ToolCommand}`."
                : "This studio was not installed from a release archive (built from source or by a package manager); update it the way it was installed.");
        var rid = RuntimeId() ?? throw new FmsException($"No release archive is published for {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}.");
        var name = AssetName(release.Version, rid);
        var asset = release.Assets.FirstOrDefault(a => a.Name == name)
                    ?? throw new FmsException($"Release {release.Version} has no archive for this system ({name}).");
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FmsException($"The archive's address is not HTTPS ({asset.Url}); refusing it.");

        var staging = Path.Combine(_appDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, name);
            await DownloadAsync(uri, archive, asset.Sha256, progress, cancellationToken).ConfigureAwait(false);

            var unpacked = Path.Combine(staging, "unpacked");
            Directory.CreateDirectory(unpacked);
            if (name.EndsWith(".zip", StringComparison.Ordinal)) ZipFile.ExtractToDirectory(archive, unpacked);
            else
            {
                // tar keeps the executable bits that ZipFile would lose.
                var result = _runner.Run("tar", new[] { "-xzf", archive, "-C", unpacked }, ExtractTimeout);
                if (result.TimedOut || result.ExitCode != 0)
                    throw new FmsException($"Unpacking the archive failed (tar exit code {result.ExitCode}).");
            }

            var root = Path.Combine(unpacked, "fmstudio");
            var exe = OperatingSystem.IsWindows() ? ".exe" : "";
            if (!File.Exists(Path.Combine(root, "fmstudio" + exe)) || !File.Exists(Path.Combine(root, "FactorioModStudio" + exe)))
                throw new FmsException("The archive does not hold fmstudio and FactorioModStudio; nothing was changed.");
            return (staging, root);
        }
        catch (UnauthorizedAccessException ex)
        {
            DeleteQuietly(staging);
            throw new FmsException($"The studio's folder cannot be written ({_appDirectory}): {ex.Message}", ex);
        }
        catch
        {
            DeleteQuietly(staging);
            throw;
        }
    }

    private async Task DownloadAsync(Uri uri, string file, string? sha256, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new FmsException($"The download failed: {(int)response.StatusCode} {response.ReasonPhrase} ({uri}).");
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new FmsException($"The download was redirected to a non-HTTPS address ({final}); refusing it.");
            var total = response.Content.Headers.ContentLength;
            if (total > MaxDownloadBytes) throw new FmsException($"The download is larger than {MaxDownloadBytes / (1024 * 1024)} MB; refusing it.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaxDownloadBytes) throw new FmsException($"The download is larger than {MaxDownloadBytes / (1024 * 1024)} MB; refusing it.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((received, total));
                }
            }

            if (sha256 is not null && !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.Ordinal))
                throw new FmsException("The downloaded archive does not match the checksum GitHub reports; nothing was changed.");
        }
        catch (HttpRequestException ex)
        {
            // GitHub sends the archive from another host (release-assets.githubusercontent.com) that some networks block while the
            // release list from api.github.com still comes through: give the real reason (not "see inner exception") and a way out.
            var reason = ex.InnerException?.Message is { Length: > 0 } inner && !ex.Message.Contains(inner, StringComparison.Ordinal)
                ? $"{ex.Message.Replace(", see inner exception.", ":", StringComparison.Ordinal)} {inner}" : ex.Message;
            throw new FmsException($"The download failed: {reason} If this keeps happening, download the archive from the release page and unpack it over the studio's folder.", ex);
        }
    }

    /// <summary>
    /// Moves every top-level entry of <paramref name="from"/> into <paramref name="to"/>; an entry already there is renamed to "*.old"
    /// first (a running program can be renamed). When a move fails, what was done is undone.
    /// </summary>
    public static void Swap(string from, string to)
    {
        var done = new List<(string Target, string? Old)>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                var target = Path.Combine(to, Path.GetFileName(entry));
                string? old = null;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    old = FreeOldName(target);
                    MoveEntry(target, old);
                }
                done.Add((target, old));
                MoveEntry(entry, target);
            }
        }
        catch
        {
            for (var i = done.Count - 1; i >= 0; i--)
            {
                var (target, old) = done[i];
                try
                {
                    if (old is null) { if (File.Exists(target)) File.Delete(target); else if (Directory.Exists(target)) Directory.Delete(target, true); continue; }
                    if (File.Exists(old) || Directory.Exists(old))
                    {
                        if (File.Exists(target)) File.Delete(target); else if (Directory.Exists(target)) Directory.Delete(target, true);
                        MoveEntry(old, target);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* keep undoing the rest */ }
            }
            throw;
        }
    }

    /// <summary>"name.old", or "name.old-&lt;guid&gt;" when an older one is still in use and cannot be deleted.</summary>
    private static string FreeOldName(string target)
    {
        var old = target + OldSuffix;
        if (!File.Exists(old) && !Directory.Exists(old)) return old;
        try
        {
            if (File.Exists(old)) File.Delete(old); else Directory.Delete(old, recursive: true);
            return old;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return old + "-" + Guid.NewGuid().ToString("N");
        }
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    /// <summary>Deletes what an earlier update left behind ("*.old", unfinished downloads). Anything still in use stays for next time.</summary>
    public static void CleanUp(string? appDirectory = null)
    {
        var folder = appDirectory ?? AppContext.BaseDirectory;
        try
        {
            // Only a release folder is ever updated in place; a build folder or the tools store is none of our business.
            if (!Directory.Exists(folder) || DetectKind(folder) != InstallKind.ReleaseArchive) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(entry);
                if (!name.EndsWith(OldSuffix, StringComparison.Ordinal) && !name.Contains(OldSuffix + "-", StringComparison.Ordinal) &&
                    !name.StartsWith(StagingPrefix, StringComparison.Ordinal)) continue;
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still running or locked */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable folder: nothing to clean */ }
    }
}

/// <summary>The update channel the user picked, kept in the studio's data folder (updates.json); null when never picked.</summary>
public static class UpdateSettings
{
    private static string FilePath(string? dataDirectory) => Path.Combine(dataDirectory ?? FactorioInstallations.DataDirectory(), "updates.json");

    public static UpdateChannel? ReadChannel(string? dataDirectory = null)
    {
        try
        {
            var file = FilePath(dataDirectory);
            if (!File.Exists(file)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
                ? StudioUpdates.ParseChannel(c.GetString())
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void WriteChannel(UpdateChannel channel, string? dataDirectory = null)
    {
        var file = FilePath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(new { channel = StudioUpdates.ChannelName(channel) }));
    }

    /// <summary>The picked channel, else the one the running version belongs to.</summary>
    public static UpdateChannel Channel(string current, string? dataDirectory = null) =>
        ReadChannel(dataDirectory) ?? StudioUpdates.DefaultChannel(current);
}
