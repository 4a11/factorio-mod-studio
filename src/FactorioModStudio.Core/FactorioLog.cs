namespace FactorioModStudio.Core;

/// <summary>
/// Where an installation of Factorio writes <c>factorio-current.log</c>, and reading it while the game may still hold it open.
/// The log lives in the "write data" folder: next to the game when its <c>config-path.cfg</c> says
/// <c>use-system-read-write-data-directories=false</c> (zip and headless installs), else in the user's data folder
/// (%APPDATA%\Factorio, ~/Library/Application Support/factorio, ~/.factorio).
/// </summary>
public static class FactorioLog
{
    public const string FileName = "factorio-current.log";

    /// <summary>The log of the installation whose executable is <paramref name="factorioExecutable"/>; null when it cannot be told.</summary>
    public static string? CurrentLogPath(string factorioExecutable)
    {
        var root = InstallRoot(factorioExecutable);
        if (root is null) return null;

        if (UsesOwnFolder(root)) return Path.Combine(root, FileName);

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "Factorio", FileName);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "factorio", FileName)
            : Path.Combine(home, ".factorio", FileName);
    }

    /// <summary>The lines of the log when it was written at or after <paramref name="since"/> (UTC); null otherwise.</summary>
    public static IReadOnlyList<string>? ReadIfWrittenSince(string? path, DateTime since)
    {
        if (path is null) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LastWriteTimeUtc < since.AddSeconds(-2)) return null; // file times can be a little coarse
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The folder holding data/base and config-path.cfg: two levels above bin/x64/factorio(.exe), or Contents/ above
    /// Contents/MacOS/factorio on macOS.
    /// </summary>
    private static string? InstallRoot(string executable)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(executable));
            for (var i = 0; i < 3 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir, "data", "base"))) return dir;
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool UsesOwnFolder(string root)
    {
        try
        {
            var cfg = Path.Combine(root, "config-path.cfg");
            if (!File.Exists(cfg)) return false;
            return File.ReadLines(cfg).Any(l => l.Replace(" ", "").Equals("use-system-read-write-data-directories=false", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
