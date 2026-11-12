namespace FactorioModStudio.Core;

/// <param name="Id">"thumbnail", "changelog", "locale-name", "author", "errors" or "packed".</param>
/// <param name="Status">"ok", "warn", "fail" or "info".</param>
public sealed record ReadinessCheck(string Id, string Status, string? Version = null, int? Count = null, string? File = null, int? Line = null);

/// <summary>A short "is this mod ready to publish" checklist shown at the top of the Overview.</summary>
public static class ReleaseReadiness
{
    public const string Ok = "ok", Warn = "warn", Fail = "fail", Info = "info";

    /// <summary>Always returns the same six checks in a fixed order.</summary>
    public static IReadOnlyList<ReadinessCheck> Evaluate(string modDirectory, ModOverview overview, string? distDirectory)
    {
        var name = overview.Info?.Name;
        var version = overview.Info?.Version;

        return new[]
        {
            Thumbnail(modDirectory),
            ChangelogEntry(modDirectory, version),
            LocaleName(modDirectory, name),
            Author(overview.Info?.Author),
            Errors(overview),
            Packed(modDirectory, name, version, distDirectory)
        };
    }

    private static ReadinessCheck Thumbnail(string modDirectory) =>
        new("thumbnail", System.IO.File.Exists(Path.Combine(modDirectory, "thumbnail.png")) ? Ok : Warn);

    private static ReadinessCheck ChangelogEntry(string modDirectory, string? version)
    {
        var path = Path.Combine(modDirectory, "changelog.txt");
        if (version is not null && System.IO.File.Exists(path))
        {
            try
            {
                var entry = Changelog.Parse(System.IO.File.ReadAllLines(path).ToList()).Entries
                    .FirstOrDefault(e => string.Equals(e.Version, version, StringComparison.Ordinal));
                if (entry is not null) return new("changelog", Ok, version, File: "changelog.txt", Line: entry.Line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* treated as missing */ }
        }

        return new("changelog", Warn, version);
    }

    private static ReadinessCheck LocaleName(string modDirectory, string? name)
    {
        if (name is not null)
        {
            try
            {
                foreach (var language in LocaleData.Languages(modDirectory))
                {
                    var data = LocaleData.Load(modDirectory, language);
                    if (data.Find("mod-name", name) is not null && data.Find("mod-description", name) is not null)
                        return new("locale-name", Ok);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* treated as missing */ }
        }

        return new("locale-name", Warn);
    }

    /// <summary>
    /// The author the mod portal shows: a warning while it is empty or still the "Your Name" a new mod starts with, which is
    /// easy to publish by mistake.
    /// </summary>
    private static ReadinessCheck Author(string? author) =>
        string.IsNullOrWhiteSpace(author) || string.Equals(author.Trim(), ModScaffolder.DefaultAuthor, StringComparison.OrdinalIgnoreCase)
            ? new("author", Warn, File: "info.json")
            : new("author", Ok);

    private static ReadinessCheck Errors(ModOverview overview)
    {
        var count = overview.Issues.Count(i => i.Severity == Severity.Error);
        return new("errors", count == 0 ? Ok : Fail, Count: count);
    }

    private static ReadinessCheck Packed(string modDirectory, string? name, string? version, string? distDirectory)
    {
        if (distDirectory is null || name is null || version is null) return new("packed", Info, version);

        var zip = Path.Combine(distDirectory, $"{name}_{version}.zip");
        if (!System.IO.File.Exists(zip)) return new("packed", Info, version);

        try
        {
            var packedAt = System.IO.File.GetLastWriteTimeUtc(zip);
            var changed = ModFiles.Enumerate(modDirectory, distDirectory)
                .Any(f => System.IO.File.GetLastWriteTimeUtc(Path.Combine(modDirectory, f)) > packedAt);
            return new("packed", changed ? Warn : Ok, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new("packed", Info, version);
        }
    }
}
