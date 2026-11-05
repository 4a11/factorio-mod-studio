using System.Globalization;
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
public sealed record ManagedInstallation(string Version, string Path, string Executable, bool AlreadyInstalled = false);

/// <summary>
/// Installs Factorio's headless server (free, no account) into the studio's own folder,
/// <see cref="FactorioInstallations.ManagedInstallationsDirectory"/>, where the normal discovery finds it, so <c>smoke</c> picks it up
/// for mods with a matching <c>factorio_version</c> (<c>run</c> needs the game with graphics and leaves it out, see
/// <see cref="FactorioInstallations.PickToPlay"/>).
/// The headless server exists for Linux only. Versions come from Factorio's updater API, the archive from the documented download
/// address (https://wiki.factorio.com/Download_API). Nothing is started except the system's <c>tar</c> to unpack; a version is
/// accepted only as MAJOR.MINOR.PATCH, so it can never change the URL or the target folder; the unpacked game must report that
/// same version in <c>data/base/info.json</c> before it is moved into place, and a failed install leaves nothing behind.
/// </summary>
public sealed class FactorioDownloads
{
    public const string UpdaterUrl = "https://updater.factorio.com/get-available-versions?apiVersion=2";
    public const string HeadlessPackage = "core-linux_headless64";
    public const long MaxDownloadBytes = 512L * 1024 * 1024;
    private const string FolderSuffix = "-headless";
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(10);
    /// <summary>A download that receives nothing for this long is given up (the HTTP client itself has no timeout).</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static readonly Regex VersionPattern = new(@"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BranchPattern = new(@"^[0-9]{1,4}\.[0-9]{1,4}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly IProcessRunner _runner;
    private readonly string _directory;
    private readonly Func<string?> _platformProblem;
    private readonly TimeSpan _stallTimeout;

    /// <param name="directory">Where installations go; normally <see cref="FactorioInstallations.ManagedInstallationsDirectory"/>.</param>
    /// <param name="platformProblem">Why this machine cannot run the headless server, or null when it can. Tests replace it.</param>
    /// <param name="stallTimeout">How long a download may receive nothing; <see cref="StallTimeout"/> by default.</param>
    public FactorioDownloads(HttpClient http, IProcessRunner runner, string directory, Func<string?>? platformProblem = null, TimeSpan? stallTimeout = null)
    {
        _http = http;
        _runner = runner;
        _directory = Path.GetFullPath(directory);
        _platformProblem = platformProblem ?? HeadlessPlatformProblem;
        _stallTimeout = stallTimeout ?? StallTimeout;
    }

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
    public static bool IsHeadlessInstall(string executable, string? managedDirectory = null)
    {
        try
        {
            var root = FactorioInstallations.InstallRoot(executable);
            if (root is null) return false;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            if (!name.EndsWith(FolderSuffix, StringComparison.Ordinal) || !IsVersion(name[..^FolderSuffix.Length])) return false;
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

    /// <summary>Headless installations made by the studio, newest first.</summary>
    public IReadOnlyList<ManagedInstallation> Installed()
    {
        var list = new List<ManagedInstallation>();
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return list;
            foreach (var folder in System.IO.Directory.EnumerateDirectories(_directory))
            {
                var name = Path.GetFileName(folder);
                if (!name.EndsWith(FolderSuffix, StringComparison.Ordinal)) continue;
                var version = name[..^FolderSuffix.Length];
                if (!IsVersion(version) || (new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (FactorioLocator.ResolveExecutable(folder) is { } exe) list.Add(new ManagedInstallation(version, folder, exe));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable folder holds nothing usable */ }
        return list.OrderByDescending(i => Key(i.Version)).ToList();
    }

    /// <summary>Deletes a headless installation made by the studio. False when that version is not installed.</summary>
    public bool Remove(string version)
    {
        if (!IsVersion(version)) throw new FmsException($"\"{version}\" is not a MAJOR.MINOR.PATCH version.");
        var folder = FolderOf(version);
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
            await DownloadAsync(HeadlessUrl(version), archive, progress, cancellationToken).ConfigureAwait(false);

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

    private async Task DownloadAsync(string url, string file, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken)
    {
        // Cancelled by the caller, or by silence: restarted after every chunk, so a slow download is fine and a dead one is not.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new FmsException($"Factorio has no headless download at {url}.");
            if (!response.IsSuccessStatusCode)
                throw new FmsException($"The download failed: {(int)response.StatusCode} {response.ReasonPhrase} ({url}).");
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new FmsException($"The download was redirected to a non-HTTPS address ({final}); refusing it.");

            var total = response.Content.Headers.ContentLength;
            if (total > MaxDownloadBytes) throw new FmsException($"The download is larger than {MaxDownloadBytes / (1024 * 1024)} MB; refusing it.");

            await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(_stallTimeout);
                received += read;
                if (received > MaxDownloadBytes) throw new FmsException($"The download is larger than {MaxDownloadBytes / (1024 * 1024)} MB; refusing it.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report((received, total));
            }
            if (received == 0) throw new FmsException($"The download from {url} was empty.");
        }
        catch (HttpRequestException ex)
        {
            throw new FmsException($"Could not download {url}: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FmsException($"The download from {url} stalled: nothing arrived for {_stallTimeout.TotalSeconds:0} seconds.", ex);
        }
    }
}
