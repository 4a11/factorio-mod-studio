using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

public enum Severity { Info, Warning, Error }

public sealed record ValidationIssue(Severity Severity, string Code, string Message, string? File = null, int? Line = null);

public sealed class ValidationResult
{
    private readonly List<ValidationIssue> _issues = new();

    public IReadOnlyList<ValidationIssue> Issues => _issues;
    public ModInfo? Info { get; internal set; }
    public bool HasErrors => _issues.Any(i => i.Severity == Severity.Error);
    public int ErrorCount => _issues.Count(i => i.Severity == Severity.Error);
    public int WarningCount => _issues.Count(i => i.Severity == Severity.Warning);

    internal void Add(Severity severity, string code, string message, string? file = null, int? line = null) =>
        _issues.Add(new ValidationIssue(severity, code, message, file, line));
}

/// <summary>
/// Static checks of a mod directory. Rule codes (FMSxxx) are stable so they can be referenced in docs and CI.
/// </summary>
public static class ModValidator
{
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9_\- ]+$", RegexOptions.Compiled);
    private static readonly Regex FactorioVersionPattern = new(@"^[0-9]+\.[0-9]+$", RegexOptions.Compiled);
    private static readonly string[] KnownFactorioVersions = { "1.0", "1.1", "2.0", "2.1" };
    private static readonly HashSet<string> DlcMods = new(StringComparer.Ordinal) { "space-age", "quality", "elevated-rails" };

    private static readonly string[] RequiredStringFields = { "name", "version", "title", "author" };

    public static ValidationResult Validate(string modDirectory)
    {
        var result = new ValidationResult();
        var infoPath = Path.Combine(modDirectory, "info.json");

        if (!File.Exists(infoPath))
        {
            result.Add(Severity.Error, "FMS001", "info.json not found.", "info.json");
            return result;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(infoPath));
        }
        catch (JsonException ex)
        {
            result.Add(Severity.Error, "FMS002", $"info.json is not valid JSON: {ex.Message}", "info.json",
                ex.LineNumber is long ln ? (int)ln + 1 : null);
            return result;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                result.Add(Severity.Error, "FMS002", "info.json must contain a JSON object.", "info.json");
                return result;
            }

            result.Info = ModInfo.FromElement(root);
            CheckInfo(result, root, result.Info);
        }

        CheckThumbnail(result, modDirectory);
        CheckChangelog(result, modDirectory, result.Info);

        // FMS030 / FMS031 / FMS032: broken __mod__/ asset paths and requires. ModReferences is the single implementation; the
        // overview uses the same analysis for its "unused" flags.
        foreach (var issue in ModReferences.Analyze(modDirectory, result.Info.Name, ModFiles.Enumerate(modDirectory)).Issues)
            result.Add(issue.Severity, issue.Code, issue.Message, issue.File, issue.Line);

        try { LocaleChecker.Check(result, modDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* unreadable tree: skip locale checks */ }

        return result;
    }

    private static void CheckInfo(ValidationResult result, JsonElement root, ModInfo info)
    {
        foreach (var field in RequiredStringFields)
        {
            if (!root.TryGetProperty(field, out var value) ||
                value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                result.Add(Severity.Error, "FMS003", $"Required field \"{field}\" is missing or not a non-empty string.", "info.json");
            }
        }

        if (info.Name is { Length: > 0 } name)
        {
            if (name.Length > 100)
                result.Add(Severity.Error, "FMS004", $"Mod name is {name.Length} characters; the maximum is 100.", "info.json");
            if (!NamePattern.IsMatch(name))
                result.Add(Severity.Error, "FMS004",
                    $"Mod name \"{name}\" may only contain letters, digits, '_', '-' and spaces.", "info.json");
        }

        if (info.Version is { Length: > 0 } version && !ModVersion.TryParse(version, out _))
            result.Add(Severity.Error, "FMS005",
                $"Version \"{version}\" is invalid; expected X.Y.Z with numbers 0-65535.", "info.json");

        if (info.FactorioVersion is null)
        {
            if (!root.TryGetProperty("factorio_version", out _))
                result.Add(Severity.Warning, "FMS006",
                    "\"factorio_version\" is missing; Factorio assumes the ancient default 0.12. Set it, e.g. \"2.0\".", "info.json");
            else
                result.Add(Severity.Error, "FMS007", "\"factorio_version\" must be a string such as \"2.0\".", "info.json");
        }
        else if (!FactorioVersionPattern.IsMatch(info.FactorioVersion))
        {
            result.Add(Severity.Error, "FMS007",
                $"\"factorio_version\" \"{info.FactorioVersion}\" is invalid; expected MAJOR.MINOR such as \"2.0\".", "info.json");
        }

        CheckDependencies(result, root);
        CheckVersionRules(result, info);
    }

    /// <summary>FMS015 (DLC mod needs Factorio 2.0+) and FMS016 (unusual target version).</summary>
    private static void CheckVersionRules(ValidationResult result, ModInfo info)
    {
        if (info.FactorioVersion is not { } version || !FactorioVersionPattern.IsMatch(version)) return;
        var parts = version.Split('.');
        var parsed = int.TryParse(parts[0], out var major) & int.TryParse(parts[1], out var minor);

        if (parsed && (major < 2))
        {
            foreach (var dep in info.ParsedDependencies.Where(d => d.Kind != DependencyKind.Incompatible && DlcMods.Contains(d.Name)).Select(d => d.Name).Distinct())
                result.Add(Severity.Error, "FMS015",
                    $"Dependency \"{dep}\" is a Factorio DLC mod that exists only in Factorio 2.0 and later, but \"factorio_version\" is \"{version}\".", "info.json");
        }

        var normalised = parsed ? $"{major}.{minor}" : version;
        if (!KnownFactorioVersions.Contains(normalised, StringComparer.Ordinal))
            result.Add(Severity.Info, "FMS016",
                $"\"factorio_version\" \"{version}\" is unusual (known versions: {string.Join(", ", KnownFactorioVersions)}); fine if intentional.", "info.json");
    }

    private static void CheckDependencies(ValidationResult result, JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var deps)) return; // defaults to ["base"]

        if (deps.ValueKind != JsonValueKind.Array)
        {
            result.Add(Severity.Error, "FMS008", "\"dependencies\" must be an array of strings.", "info.json");
            return;
        }

        var parsed = new List<Dependency>();
        foreach (var item in deps.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (Dependency.TryParse(text, out var dep))
                parsed.Add(dep);
            else
                result.Add(Severity.Error, "FMS008",
                    $"Invalid dependency {item.GetRawText()}; expected e.g. \"base >= 2.0.0\" or \"? some-mod\".", "info.json");
        }

        if (!parsed.Any(d => d.Name == "base"))
            result.Add(Severity.Warning, "FMS009",
                "No dependency on \"base\". Add e.g. \"base >= 2.0.0\" so the mod portal shows the right requirements.", "info.json");
    }

    private static void CheckThumbnail(ValidationResult result, string modDirectory)
    {
        var path = Path.Combine(modDirectory, "thumbnail.png");
        if (!File.Exists(path))
        {
            result.Add(Severity.Warning, "FMS010", "thumbnail.png is missing (the mod portal expects a 144x144 image).", "thumbnail.png");
            return;
        }

        var size = ReadPngSize(path);
        if (size is null)
            result.Add(Severity.Warning, "FMS011", "thumbnail.png is not a valid PNG file.", "thumbnail.png");
        else if (size.Value != (144, 144))
            result.Add(Severity.Warning, "FMS011",
                $"thumbnail.png is {size.Value.Width}x{size.Value.Height}; the mod portal expects 144x144.", "thumbnail.png");
    }

    internal static (int Width, int Height)? ReadPngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) < header.Length) return null;

            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (!header.AsSpan(0, 8).SequenceEqual(signature)) return null;

            static int BigEndian(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
            return (BigEndian(header, 16), BigEndian(header, 20));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void CheckChangelog(ValidationResult result, string modDirectory, ModInfo info)
    {
        var path = Path.Combine(modDirectory, "changelog.txt");
        if (!File.Exists(path))
        {
            result.Add(Severity.Info, "FMS012", "changelog.txt not found (recommended, shown on the mod portal and in game).", "changelog.txt");
            return;
        }

        var parsed = Changelog.Parse(File.ReadAllLines(path));

        if (parsed.Entries.Count == 0)
        {
            result.Add(Severity.Warning, "FMS014", "changelog.txt has no \"Version:\" entries.", "changelog.txt");
            return;
        }

        foreach (var line in parsed.SeparatorsWithoutVersion)
            result.Add(Severity.Warning, "FMS014", "Separator line is not followed by a \"Version:\" line.", "changelog.txt", line);

        if (parsed.NonStandardSeparatorLines.Count > 0)
            result.Add(Severity.Warning, "FMS014",
                $"Separator lines should be exactly {Changelog.StandardSeparatorLength} dashes.", "changelog.txt",
                parsed.NonStandardSeparatorLines[0]);

        foreach (var entry in parsed.Entries.Where(e => !ModVersion.TryParse(e.Version, out _)))
            result.Add(Severity.Warning, "FMS014", $"Invalid version \"{entry.Version}\" in changelog.", "changelog.txt", entry.Line);

        var top = parsed.Entries[0];
        if (info.Version is not null && top.Version != info.Version)
            result.Add(Severity.Warning, "FMS013",
                $"Newest changelog entry is {top.Version} but info.json version is {info.Version}.", "changelog.txt", top.Line);
    }
}
