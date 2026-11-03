namespace FactorioModStudio.Core;

/// <summary>Enumerates the files of a mod directory that belong in a release, skipping tooling and VCS noise.</summary>
public static class ModFiles
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".github", ".vscode", ".vs", ".idea", "node_modules", ".fmstudio"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitignore", ".gitattributes", ".editorconfig", "Thumbs.db", ".DS_Store", "desktop.ini", "fmstudio.json", ".fmstudio-extracted"
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".log", ".tmp"
    };

    /// <summary>
    /// Returns paths relative to <paramref name="root"/>, using '/' separators, sorted ordinally.
    /// <paramref name="excludeDirectory"/> (e.g. an output folder inside the mod) is skipped entirely.
    /// </summary>
    public static IReadOnlyList<string> Enumerate(string root, string? excludeDirectory = null)
    {
        var rootFull = Path.GetFullPath(root);
        var excluded = excludeDirectory is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludeDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var result = new List<string>();
        Walk(rootFull, "");
        result.Sort(StringComparer.Ordinal);
        return result;

        void Walk(string dir, string relative)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (ExcludedDirectories.Contains(name)) continue;
                if (excluded is not null &&
                    string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sub)), excluded, comparison))
                    continue;
                Walk(sub, relative + name + "/");
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (ExcludedFileNames.Contains(name)) continue;
                if (ExcludedExtensions.Contains(Path.GetExtension(name))) continue;
                result.Add(relative + name);
            }
        }
    }
}
