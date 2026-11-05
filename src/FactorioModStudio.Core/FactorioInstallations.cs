using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <param name="Id">First 12 lowercase hex characters of the SHA-256 of the UTF-8 bytes of <paramref name="Path"/> (lower-cased first on Windows).</param>
/// <param name="Path">Normalised absolute path of the factorio executable.</param>
/// <param name="Version">"2.0.76", read from <c>data/base/info.json</c> (the executable is never run); null when undetectable.</param>
/// <param name="Label">User label (registered installations only).</param>
/// <param name="Sources">Subset of "ui", "env", "config", "auto", in that order.</param>
public sealed record FactorioInstallation(string Id, string Path, string? Version, string? Label, IReadOnlyList<string> Sources);

/// <param name="Mode">"auto", "manual" or "none".</param>
public sealed record FactorioPick(FactorioInstallation? Chosen, string Mode, string Reason);

/// <summary>An installation the user added in the UI (stored in <c>.fmstudio/settings.json</c>).</summary>
public sealed record RegisteredInstallation(string Path, string? Label);

/// <summary>
/// Several Factorio installations side by side and the choice of the one that matches a mod's <c>factorio_version</c>.
/// Safety: only files named factorio / factorio.exe that are not symbolic links are ever offered, and the version comes from
/// <c>data/base/info.json</c>; an executable is never started to find out what it is.
/// </summary>
public static class FactorioInstallations
{
    public const string DataDirectoryVariable = "FMS_DATA_DIR";
    public const int MaxPathLength = 1024;

    private const int MaxInspectedPerParent = 200;
    private const int MaxRootLevels = 6;
    private const long MaxInfoBytes = 64 * 1024;
    private static readonly string[] SourceOrder = { "ui", "env", "config", "auto" };
    private static readonly Regex InstallVersionPattern = new(@"^([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MajorMinorPattern = new(@"^\s*([0-9]{1,9})\.([0-9]{1,9})(?:\.[0-9A-Za-z]+)*\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SiblingName = new(@"^factorio", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ identity and version

    public static string IdOf(string executablePath)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        var text = OperatingSystem.IsWindows() ? executablePath.ToLowerInvariant() : executablePath;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    }

    /// <summary>The installation folder (the one holding <c>data/base/info.json</c>) above the executable, at most 6 levels up.</summary>
    public static string? InstallRoot(string executable)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(executable));
            for (var level = 0; level <= MaxRootLevels && directory is not null; level++)
            {
                if (File.Exists(System.IO.Path.Combine(directory, "data", "base", "info.json"))) return directory;
                directory = System.IO.Path.GetDirectoryName(directory);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The game version (MAJOR.MINOR.PATCH) from <c>data/base/info.json</c>. Reads a file only; any problem gives null.</summary>
    public static string? DetectVersion(string executable)
    {
        try
        {
            var root = InstallRoot(executable);
            if (root is null) return null;
            var info = new FileInfo(System.IO.Path.Combine(root, "data", "base", "info.json"));
            if (!info.Exists || info.Length > MaxInfoBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(info.FullName), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String) return null;
            var text = version.GetString();
            return text is not null && InstallVersionPattern.IsMatch(text) ? text : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True iff both parse and MAJOR.MINOR are equal ("2.0.76" and "2.0"). Only major.minor of the mod's value is used.</summary>
    public static bool VersionMatches(string? installVersion, string? modFactorioVersion) =>
        TryMajorMinor(installVersion, out var a) && TryMajorMinor(modFactorioVersion, out var b) && a == b;

    /// <summary>"2.0", "2.0.x" or "2.0.76" become "2.0"; null when the text does not start with MAJOR.MINOR.</summary>
    public static string? NormaliseMajorMinor(string? text) =>
        TryMajorMinor(text, out var value) ? $"{value.Major}.{value.Minor}" : null;

    private static bool TryMajorMinor(string? text, out (int Major, int Minor) value)
    {
        value = default;
        if (text is null) return false;
        var m = MajorMinorPattern.Match(text);
        if (!m.Success) return false;
        value = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        return true;
    }

    private static (int, int, int)? TryTriple(string? text)
    {
        if (text is null) return null;
        var parts = text.Trim().Split('.');
        if (parts.Length < 2) return null;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length) { numbers[i] = 0; continue; }
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        }
        return (numbers[0], numbers[1], numbers[2]);
    }

    private static int CompareVersions(string? a, string? b)
    {
        var x = TryTriple(a);
        var y = TryTriple(b);
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return x.Value.CompareTo(y.Value);
    }

    // ------------------------------------------------------------------ choosing

    private static int SourceRank(FactorioInstallation install)
    {
        var best = int.MaxValue;
        foreach (var source in install.Sources) best = Math.Min(best, Array.IndexOf(SourceOrder, source) is var i and >= 0 ? i : int.MaxValue - 1);
        return best;
    }

    private static string Describe(FactorioInstallation install) => install.Version is null ? $"an installation of unknown version ({install.Path})" : $"Factorio {install.Version}";

    /// <summary>Picks the installation for a mod (see the versions contract). Pure: no file access.</summary>
    public static FactorioPick Pick(IReadOnlyList<FactorioInstallation> installs, string? modFactorioVersion, string? manualId = null)
    {
        ArgumentNullException.ThrowIfNull(installs);
        var wanted = TryMajorMinor(modFactorioVersion, out var mm) ? $"{mm.Major}.{mm.Minor}" : null;

        if (manualId is not null)
        {
            var manual = installs.FirstOrDefault(i => string.Equals(i.Id, manualId, StringComparison.OrdinalIgnoreCase));
            if (manual is null)
                return new FactorioPick(null, "none", "The selected Factorio installation is not known (it may have been removed). Choose another installation or use automatic selection.");
            var reason = wanted is not null && !VersionMatches(manual.Version, modFactorioVersion)
                ? $"{Describe(manual)} selected by hand, but the mod targets Factorio {wanted} (version mismatch)."
                : $"{Describe(manual)} selected by hand.";
            return new FactorioPick(manual, "manual", reason);
        }

        var ordered = installs
            .OrderByDescending(i => i, Comparer<FactorioInstallation>.Create((a, b) => CompareVersions(a.Version, b.Version)))
            .ThenBy(SourceRank)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();

        if (wanted is null)
        {
            if (ordered.Count == 0)
                return new FactorioPick(null, "none", "No Factorio installation was found. Add one or choose an installation manually.");
            var top = ordered[0];
            var why = modFactorioVersion is null ? "The mod has no valid factorio_version" : $"The mod has no valid factorio_version (\"{modFactorioVersion}\")";
            return new FactorioPick(top, "auto", $"{why}; using the newest installation, {Describe(top)}.");
        }

        var match = ordered.FirstOrDefault(i => VersionMatches(i.Version, wanted));
        if (match is not null)
            return new FactorioPick(match, "auto", $"{Describe(match)} matches the mod's factorio_version {wanted}.");

        var found = installs.Select(i => i.Version).Distinct().OrderBy(v => v, Comparer<string?>.Create(CompareVersions))
            .Select(v => v ?? "unknown").ToList();
        var list = found.Count == 0 ? "none" : string.Join(", ", found);
        return new FactorioPick(null, "none", $"No installed Factorio matches {wanted} (found: {list}). Install one or choose an installation manually.");
    }

    /// <summary>
    /// Like <see cref="Pick"/>, for starting the game itself (<c>run</c>): the headless servers the studio installed have no graphics,
    /// so they are left out, and the reason says so when that leaves nothing.
    /// </summary>
    /// <param name="isHeadless">Tells the studio's headless servers apart; <see cref="FactorioDownloads.IsHeadlessInstall"/> by default.</param>
    public static FactorioPick PickToPlay(IReadOnlyList<FactorioInstallation> installs, string? modFactorioVersion, string? manualId = null, Func<string, bool>? isHeadless = null)
    {
        ArgumentNullException.ThrowIfNull(installs);
        isHeadless ??= path => FactorioDownloads.IsHeadlessInstall(path);
        var playable = installs.Where(i => !isHeadless(i.Path)).ToList();
        var pick = Pick(playable, modFactorioVersion, manualId);
        if (pick.Chosen is null && playable.Count != installs.Count)
            pick = pick with { Reason = pick.Reason + " The headless servers installed by the studio have no graphics: they are used for smoke tests, not to play." };
        return pick;
    }

    // ------------------------------------------------------------------ validation of a single executable

    public static bool IsFactorioName(string name) =>
        string.Equals(name, "factorio", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "factorio.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks a path typed by the user (executable or install folder) and returns the normalised executable path in
    /// <paramref name="executable"/>; the return value is an error message, or null when the path is acceptable. Nothing is started.
    /// </summary>
    public static string? CheckExecutable(string? input, out string? executable)
    {
        executable = null;
        if (string.IsNullOrWhiteSpace(input)) return "The path is empty.";
        if (input.Length > MaxPathLength) return $"The path is too long (limit {MaxPathLength} characters).";
        if (input.Any(char.IsControl)) return "The path contains control characters.";

        string full;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(input)) return "The path must be absolute.";
            full = System.IO.Path.GetFullPath(input);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The path is not valid.";
        }

        var exe = FactorioLocator.ResolveExecutable(full);
        if (exe is null) return "No Factorio executable was found there. Choose the installation folder or the factorio executable (bin/x64/factorio).";
        if (!IsFactorioName(System.IO.Path.GetFileName(exe))) return "Only a file named factorio (factorio.exe on Windows) can be used.";

        try { if ((new FileInfo(exe).Attributes & FileAttributes.ReparsePoint) != 0) return "The Factorio executable must not be a symbolic link."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The Factorio executable cannot be read."; }

        executable = exe;
        return null;
    }

    // ------------------------------------------------------------------ candidates

    /// <summary>The user data folder of Factorio Mod Studio (<c>FMS_DATA_DIR</c>, else the platform's per-user data folder).</summary>
    public static string DataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try { return System.IO.Path.GetFullPath(overridden); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        string baseDirectory;
        if (OperatingSystem.IsWindows()) baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        else if (OperatingSystem.IsMacOS()) baseDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            baseDirectory = !string.IsNullOrWhiteSpace(xdg) && System.IO.Path.IsPathFullyQualified(xdg)
                ? xdg
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return System.IO.Path.Combine(baseDirectory, "FactorioModStudio");
    }

    /// <summary>Where installations downloaded by the studio itself will live: <c>&lt;data dir&gt;/factorio</c>.</summary>
    public static string ManagedInstallationsDirectory() => System.IO.Path.Combine(DataDirectory(), "factorio");

    /// <summary>
    /// Every installation that can be used: registered (source "ui"), FACTORIO_PATH ("env"), fmstudio.json ("config") and found
    /// automatically ("auto"). Entries are merged by <see cref="FactorioInstallation.Id"/>; paths that do not resolve are left out.
    /// </summary>
    public static IReadOnlyList<FactorioInstallation> Candidates(ProjectContext context, IReadOnlyList<RegisteredInstallation> registered)
    {
        ArgumentNullException.ThrowIfNull(context);
        registered ??= Array.Empty<RegisteredInstallation>();

        var found = new Dictionary<string, (string Path, HashSet<string> Sources, string? Label)>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var roots = new List<string>(); // install roots whose parent folders are scanned for siblings

        void Add(string exe, string source, string? label = null, bool scanParent = false)
        {
            var key = exe;
            if (found.TryGetValue(key, out var existing))
            {
                existing.Sources.Add(source);
                if (existing.Label is null && label is not null) found[key] = (existing.Path, existing.Sources, label);
            }
            else found[key] = (exe, new HashSet<string>(StringComparer.Ordinal) { source }, label);
            if (scanParent && InstallRoot(exe) is { } root) roots.Add(root);
        }

        foreach (var item in registered)
            if (item is not null && CheckExecutable(item.Path, out var exe) is null) Add(exe!, "ui", item.Label, scanParent: true);

        var env = Environment.GetEnvironmentVariable(FactorioLocator.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && TryResolveConfigured(env, out var envExe)) Add(envExe, "env", scanParent: true);

        if (!string.IsNullOrWhiteSpace(context.Config.FactorioPath))
        {
            string? configured = null;
            try { configured = context.ResolvePath(context.Config.FactorioPath); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            if (configured is not null && TryResolveConfigured(configured, out var configExe)) Add(configExe, "config", scanParent: true);
        }

        var parents = new List<string>();
        foreach (var location in FactorioLocator.WellKnownLocations())
        {
            if (!TryResolveSafe(location, out var exe)) continue;
            Add(exe, "auto");
            try
            {
                if (Directory.Exists(location)) parents.Add(System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(location)))!);
                else if (InstallRoot(exe) is { } root) parents.Add(System.IO.Path.GetDirectoryName(root)!);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        foreach (var root in roots)
        {
            var parent = System.IO.Path.GetDirectoryName(root);
            if (parent is not null) parents.Add(parent);
        }

        foreach (var parent in parents.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal))
            foreach (var child in ChildDirectories(parent, SiblingName))
                if (TryResolveSafe(child, out var exe)) Add(exe, "auto");

        foreach (var child in ChildDirectories(ManagedInstallationsDirectory(), null))
            if (TryResolveSafe(child, out var exe)) Add(exe, "auto");

        return found.Values
            .Select(v => new FactorioInstallation(
                IdOf(v.Path), v.Path, DetectVersion(v.Path), v.Label,
                SourceOrder.Where(v.Sources.Contains).ToList()))
            .OrderByDescending(i => i, Comparer<FactorioInstallation>.Create((a, b) => CompareVersions(a.Version, b.Version)))
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Settings (FACTORIO_PATH, fmstudio.json) that name a path that does not resolve to an executable, as readable fragments such
    /// as <c>FACTORIO_PATH ("/x")</c>. Used to explain an empty result.
    /// </summary>
    public static IReadOnlyList<string> UnusableConfiguredSources(ProjectContext context)
    {
        var list = new List<string>();
        var env = Environment.GetEnvironmentVariable(FactorioLocator.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && !TryResolveConfigured(env, out _)) list.Add($"{FactorioLocator.EnvironmentVariable} (\"{env}\")");
        if (!string.IsNullOrWhiteSpace(context.Config.FactorioPath))
        {
            string? path = null;
            try { path = context.ResolvePath(context.Config.FactorioPath); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            if (path is null || !TryResolveConfigured(path, out _)) list.Add($"factorioPath in {ProjectConfig.FileName} (\"{context.Config.FactorioPath}\")");
        }
        return list;
    }

    /// <summary>FACTORIO_PATH / fmstudio.json are set by the user themselves: they only have to resolve to an existing file.</summary>
    private static bool TryResolveConfigured(string path, out string executable)
    {
        executable = "";
        try
        {
            var exe = FactorioLocator.ResolveExecutable(path);
            if (exe is null) return false;
            executable = exe;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Resolves a folder to an executable that is named factorio and is no symbolic link.</summary>
    private static bool TryResolveSafe(string path, out string executable)
    {
        executable = "";
        try
        {
            var exe = FactorioLocator.ResolveExecutable(path);
            if (exe is null || !IsFactorioName(System.IO.Path.GetFileName(exe))) return false;
            if ((new FileInfo(exe).Attributes & FileAttributes.ReparsePoint) != 0) return false;
            executable = exe;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Immediate sub-folders (no links), at most 200 entries inspected. <paramref name="name"/> null accepts every name.</summary>
    private static IEnumerable<string> ChildDirectories(string parent, Regex? name)
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(parent)) return result;
            var inspected = 0;
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            foreach (var entry in new DirectoryInfo(parent).EnumerateFileSystemInfos("*", options))
            {
                if (++inspected > MaxInspectedPerParent) break;
                if (entry is not DirectoryInfo directory) continue;
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (name is not null && !name.IsMatch(directory.Name)) continue;
                result.Add(directory.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
