using System.Text.Json;

namespace FactorioModStudio.Core;

/// <summary>Names for completion from the selected installation's runtime API documentation.</summary>
public sealed record FactorioRuntimeApi(string Version, IReadOnlyList<string> Globals, IReadOnlyList<string> Events)
{
    public static FactorioRuntimeApi? Read(string executable, string? expectedVersion)
    {
        var data = GameDataDump.DataDirectoryFor(executable);
        if (data is null) return null;

        var path = Path.GetFullPath(Path.Combine(data, "..", "doc-html", "runtime-api.json"));
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 8 * 1024 * 1024) return null;
            using var document = JsonDocument.Parse(file);
            var root = document.RootElement;
            var version = StringProperty(root, "application_version");
            if (StringProperty(root, "application") != "factorio" || StringProperty(root, "stage") != "runtime" ||
                !ModVersion.TryParse(version, out var parsed) ||
                (expectedVersion is not null && (!ModVersion.TryParse(expectedVersion, out var expected) || expected != parsed)))
                return null;

            var globals = Names(root, "global_objects").Concat(Names(root, "global_functions"));
            var defines = root.TryGetProperty("defines", out var groups) && groups.ValueKind == JsonValueKind.Array
                ? groups.EnumerateArray().FirstOrDefault(d => StringProperty(d, "name") == "events")
                : default;
            var events = Names(defines, "values");
            return new(version!, Sorted(globals), Sorted(events));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<string> Names(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(item => StringProperty(item, "name")).OfType<string>().Where(IsIdentifier)
            : [];

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string[] Sorted(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
