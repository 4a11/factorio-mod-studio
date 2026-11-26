using System.Text.Json;

namespace FactorioModStudio.Core;

public sealed record RuntimeApiParameter(string Name, string? Type, string? Description, bool Optional);
public sealed record RuntimeApiMethod(string? Description, IReadOnlyList<RuntimeApiParameter> Parameters);

/// <summary>Completion and inline help from the selected installation's runtime API documentation.</summary>
public sealed record FactorioRuntimeApi(string Version, IReadOnlyList<string> Globals, IReadOnlyList<string> Events)
{
    public IReadOnlyDictionary<string, string> EventDescriptions { get; init; } = new Dictionary<string, string>();
    public RuntimeApiMethod? OnEvent { get; init; }

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
            var events = Sorted(Names(defines, "values"));
            var eventNames = events.ToHashSet(StringComparer.Ordinal);
            var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in Entries(root, "events"))
            {
                var name = StringProperty(item, "name");
                if (name is not null && eventNames.Contains(name) && StringProperty(item, "description") is { Length: > 0 } description)
                    descriptions.TryAdd(name, description);
            }

            var bootstrap = Entries(root, "classes").FirstOrDefault(item => StringProperty(item, "name") == "LuaBootstrap");
            var method = Entries(bootstrap, "methods").FirstOrDefault(item => StringProperty(item, "name") == "on_event");
            var parameters = Entries(method, "parameters").Where(item => StringProperty(item, "name") is not null)
                .OrderBy(item => item.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out var n) ? n : int.MaxValue)
                .Select(item => new RuntimeApiParameter(StringProperty(item, "name")!,
                    item.TryGetProperty("type", out var type) ? FormatType(type) : null, StringProperty(item, "description"),
                    item.TryGetProperty("optional", out var optional) && optional.ValueKind == JsonValueKind.True)).ToArray();
            return new(version!, Sorted(globals), events)
            {
                EventDescriptions = descriptions,
                OnEvent = parameters.Length == 0 ? null : new(StringProperty(method, "description"), parameters)
            };
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

    private static IEnumerable<JsonElement> Entries(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static IEnumerable<string> Names(JsonElement root, string key) =>
        Entries(root, key).Select(item => StringProperty(item, "name")).OfType<string>().Where(IsIdentifier);

    private static string? FormatType(JsonElement type) => type.ValueKind == JsonValueKind.String ? type.GetString() : StringProperty(type, "complex_type") switch
    {
        "union" => string.Join(" | ", Entries(type, "options").Select(FormatType).OfType<string>()),
        "array" when type.TryGetProperty("value", out var value) => $"{FormatType(value)}[]",
        "function" => "function",
        _ => null
    };

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string[] Sorted(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
