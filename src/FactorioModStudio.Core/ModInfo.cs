using System.Text.Json;

namespace FactorioModStudio.Core;

/// <summary>Lenient view of a mod's info.json. Fields with a wrong type or missing are null.</summary>
public sealed class ModInfo
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public string? FactorioVersion { get; init; }

    /// <summary>Raw dependency strings. Factorio treats a missing list as <c>["base"]</c>.</summary>
    public IReadOnlyList<string> RawDependencies { get; init; } = new[] { "base" };

    public IEnumerable<Dependency> ParsedDependencies =>
        RawDependencies.Select(s => Dependency.TryParse(s, out var d) ? d : null).OfType<Dependency>();

    public static ModInfo FromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("info.json must contain a JSON object.");

        var deps = new List<string> { "base" };
        if (root.TryGetProperty("dependencies", out var d))
        {
            deps.Clear();
            if (d.ValueKind == JsonValueKind.Array)
                deps.AddRange(d.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        }

        return new ModInfo
        {
            Name = Str(root, "name"),
            Version = Str(root, "version"),
            Title = Str(root, "title"),
            Author = Str(root, "author"),
            Description = Str(root, "description"),
            FactorioVersion = Str(root, "factorio_version"),
            RawDependencies = deps
        };
    }

    public static ModInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    public static ModInfo Load(string path) => Parse(File.ReadAllText(path));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
