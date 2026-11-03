namespace FactorioModStudio.Core;

/// <summary>Finds the Factorio executable: explicit path, FACTORIO_PATH, config file, then well-known install locations.</summary>
public static class FactorioLocator
{
    public const string EnvironmentVariable = "FACTORIO_PATH";

    public static string Locate(string? explicitPath, string? configuredPath)
    {
        var candidates = new List<(string Source, string Path)>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(("--factorio", explicitPath));

        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add((EnvironmentVariable, env));
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(("factorioPath", configuredPath));

        // An explicitly provided path must work; do not silently fall through to a different installation.
        foreach (var (source, path) in candidates)
        {
            var exe = ResolveExecutable(path);
            if (exe is not null) return exe;
            throw new FmsException($"Factorio executable not found at \"{path}\" (from {source}).");
        }

        foreach (var path in WellKnownLocations())
        {
            var exe = ResolveExecutable(path);
            if (exe is not null) return exe;
        }

        throw new FmsException(
            $"Could not find Factorio. Pass --factorio <path>, set {EnvironmentVariable}, or set \"factorioPath\" in {ProjectConfig.FileName}.");
    }

    /// <summary>Accepts either the executable itself or an installation directory.</summary>
    public static string? ResolveExecutable(string path)
    {
        if (File.Exists(path)) return Path.GetFullPath(path);
        if (!Directory.Exists(path)) return null;

        string[] relatives =
        {
            Path.Combine("bin", "x64", "factorio.exe"),
            Path.Combine("bin", "x64", "factorio"),
            Path.Combine("factorio.app", "Contents", "MacOS", "factorio"),
            Path.Combine("Contents", "MacOS", "factorio")
        };

        foreach (var relative in relatives)
        {
            var candidate = Path.Combine(path, relative);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>Folders where Factorio is commonly installed (existing or not).</summary>
    public static IEnumerable<string> WellKnownLocations()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         Environment.GetEnvironmentVariable("ProgramFiles")
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                yield return Path.Combine(root, "Steam", "steamapps", "common", "Factorio");
                yield return Path.Combine(root, "Factorio");
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".factorio");
                yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "Factorio");
                yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Factorio");
                yield return Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "Factorio");
            }

            yield return "/opt/factorio";
            yield return "/Applications/factorio.app/Contents/MacOS/factorio";
        }
    }
}
