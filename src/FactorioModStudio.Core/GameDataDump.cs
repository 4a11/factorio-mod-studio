using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FactorioModStudio.Core;

/// <summary>One value that differs: <see cref="Path"/> like <c>max_health</c> or <c>results[0].amount</c>; null Before / After
/// means the field is missing on that side.</summary>
public sealed record GameDataField(string Path, JsonNode? Before, JsonNode? After);

/// <param name="Change">"added", "changed" or "removed" (compared with the game and the dependencies without this mod).</param>
/// <param name="Value">The prototype as the game has it with the mod (null for a removed one).</param>
/// <param name="Fields">For a changed prototype: what the mod changed. For an added copy (<see cref="CopyOf"/>): how it differs
/// from the original. Empty otherwise.</param>
/// <param name="CopyOf">For an added prototype the mod's files copy from an existing one: that original's name.</param>
/// <param name="FieldsTruncated">More fields differ than <see cref="GameDataDump.MaxFields"/>.</param>
public sealed record GameDataItem(string Type, string Name, string Change, JsonNode? Value, IReadOnlyList<GameDataField> Fields,
    string? CopyOf = null, bool FieldsTruncated = false);

/// <summary>What a mod really does to the game's data, from two <c>--dump-data</c> runs: without and with the mod.</summary>
public sealed class GameDataReport
{
    public int Format { get; init; } = GameDataDump.ReportFormat;
    public string ModName { get; init; } = "";
    public string? ModVersion { get; init; }
    public string? GameVersion { get; init; }
    public string GamePath { get; init; } = "";
    public DateTime CreatedUtc { get; init; }

    /// <summary><see cref="GameDataDump.Fingerprint"/> of the mod's files when the dump was made: another value means it is outdated.</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>The mods that were enabled (base, dependencies, this mod).</summary>
    public IReadOnlyList<string> Mods { get; init; } = Array.Empty<string>();

    public IReadOnlyList<GameDataItem> Items { get; init; } = Array.Empty<GameDataItem>();
}

/// <summary>
/// Runs Factorio with <c>--dump-data</c> and compares dumps. The game gets a config.ini of its own (read-data: the game's data,
/// write-data: a work folder), so the dump never touches the player's settings, saves or lock file; the result is
/// <c>script-output/data-raw-dump.json</c> in that folder: <c>data.raw</c> after every data stage, as JSON.
/// </summary>
public static class GameDataDump
{
    public const int ReportFormat = 1;
    public const int MaxFields = 300;
    public const string DumpFileName = "data-raw-dump.json";

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>The game's data folder (with base/), next to the executable the way Windows, Linux and macOS builds lay it out.</summary>
    public static string? DataDirectoryFor(string? factorioExecutable)
    {
        if (factorioExecutable is null) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(factorioExecutable));
        if (dir is null) return null;

        // bin/x64/factorio (Windows, Linux), factorio.app/Contents/MacOS/factorio, other nestings; last a data folder right next to it.
        foreach (var relative in new[] { Path.Combine("..", "..", "data"), Path.Combine("..", "data"), Path.Combine("..", "..", "..", "data"), "data" })
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, relative));
            if (Directory.Exists(Path.Combine(candidate, "base"))) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Dumps the game's data with the mods in <paramref name="modsDirectory"/> (and its mod-list.json). Returns the process result
    /// and the dump file, which is null when the game did not write one.
    /// </summary>
    public static (ProcessResult Process, string? DumpPath) Dump(string factorioExecutable, string modsDirectory, string workDirectory,
        IProcessRunner runner, TimeSpan timeout, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        var config = IsolatedFactorioConfig.Write(factorioExecutable, workDirectory, "--dump-data")
                     ?? throw new FmsException($"The data folder of \"{factorioExecutable}\" was not found (expected data/base next to the game).");
        var output = Path.Combine(IsolatedFactorioConfig.WriteDirectory(workDirectory), "script-output", DumpFileName);
        if (File.Exists(output)) File.Delete(output);

        string[] args = ["--config", config, "--mod-directory", modsDirectory, "--dump-data"];
        var result = runner is ICancellableProcessRunner cancellable
            ? cancellable.Run(factorioExecutable, args, timeout, onLine, cancellationToken)
            : runner.Run(factorioExecutable, args, timeout, onLine);
        return (result, File.Exists(output) ? output : null);
    }

    /// <summary>Switches one mod on or off in a mod-list.json, adding it when it is missing.</summary>
    public static void SetEnabled(string modListPath, string modName, bool enabled)
    {
        var root = JsonNode.Parse(File.ReadAllText(modListPath)) as JsonObject ?? new JsonObject();
        var mods = root["mods"] as JsonArray ?? new JsonArray();
        root["mods"] = mods;
        var entry = mods.OfType<JsonObject>().FirstOrDefault(m => (string?)m["name"] == modName);
        if (entry is null) mods.Add(new JsonObject { ["name"] = modName, ["enabled"] = enabled });
        else entry["enabled"] = enabled;
        File.WriteAllText(modListPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    /// <summary>A hash of the mod's file list, sizes and change times: it changes whenever a file of the mod does.</summary>
    public static string Fingerprint(string modDirectory)
    {
        var builder = new StringBuilder();
        foreach (var relative in ModFiles.Enumerate(modDirectory).OrderBy(p => p, StringComparer.Ordinal))
        {
            var info = new FileInfo(Path.Combine(modDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists) continue;
            builder.Append(relative).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32].ToLowerInvariant();
    }

    /// <summary>
    /// What <paramref name="withMod"/> adds, changes and removes compared with <paramref name="baseline"/> (both data-raw dumps).
    /// <paramref name="copies"/> maps "type/name" of a prototype the mod's files copy to the original's name (from
    /// <see cref="ModInspector"/>); an added copy is then compared with that original.
    /// </summary>
    public static List<GameDataItem> Compare(JsonDocument baseline, JsonDocument withMod, IReadOnlyDictionary<string, string>? copies = null)
    {
        var before = baseline.RootElement;
        var after = withMod.RootElement;
        var items = new List<GameDataItem>();

        foreach (var type in after.EnumerateObject())
        {
            var hadType = before.TryGetProperty(type.Name, out var oldType) && oldType.ValueKind == JsonValueKind.Object;
            if (type.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var prototype in type.Value.EnumerateObject())
            {
                if (hadType && oldType.TryGetProperty(prototype.Name, out var old))
                {
                    if (JsonElement.DeepEquals(old, prototype.Value)) continue;
                    var fields = Differences(old, prototype.Value, out var truncated);
                    items.Add(new GameDataItem(type.Name, prototype.Name, "changed", JsonNode.Parse(prototype.Value.GetRawText()), fields, null, truncated));
                    continue;
                }

                string? original = null;
                List<GameDataField> copyFields = new();
                var copyTruncated = false;
                if (copies is not null && copies.TryGetValue($"{type.Name}/{prototype.Name}", out var source)
                    && FindOriginal(before, type.Name, source) is { } originalValue)
                {
                    original = source;
                    copyFields = Differences(originalValue, prototype.Value, out copyTruncated, skipName: true);
                }
                items.Add(new GameDataItem(type.Name, prototype.Name, "added", JsonNode.Parse(prototype.Value.GetRawText()), copyFields, original, copyTruncated));
            }
        }

        foreach (var type in before.EnumerateObject())
        {
            if (type.Value.ValueKind != JsonValueKind.Object) continue;
            var hasType = after.TryGetProperty(type.Name, out var newType) && newType.ValueKind == JsonValueKind.Object;
            foreach (var prototype in type.Value.EnumerateObject())
                if (!hasType || !newType.TryGetProperty(prototype.Name, out _))
                    items.Add(new GameDataItem(type.Name, prototype.Name, "removed", null, Array.Empty<GameDataField>()));
        }

        return items
            .OrderBy(i => i.Change switch { "added" => 0, "changed" => 1, _ => 2 })
            .ThenBy(i => i.Type, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();
    }

    // The original of a copy: the same type first; items are copied across item types (copy.item finds the item whatever its type).
    private static JsonElement? FindOriginal(JsonElement baseline, string type, string name)
    {
        if (baseline.TryGetProperty(type, out var sameType) && sameType.ValueKind == JsonValueKind.Object && sameType.TryGetProperty(name, out var found))
            return found;
        foreach (var other in baseline.EnumerateObject())
            if (other.Value.ValueKind == JsonValueKind.Object && other.Value.TryGetProperty(name, out var any)
                && any.ValueKind == JsonValueKind.Object && any.TryGetProperty("stack_size", out _))
                return any;
        return null;
    }

    /// <summary>The leaf values that differ, objects compared field by field and lists of the same length item by item.</summary>
    public static List<GameDataField> Differences(JsonElement before, JsonElement after, out bool truncated, bool skipName = false)
    {
        var fields = new List<GameDataField>();
        var full = false;
        Walk(before, after, "");
        truncated = full;
        return fields;

        void Add(string path, JsonElement? b, JsonElement? a)
        {
            if (fields.Count >= MaxFields) { full = true; return; }
            fields.Add(new GameDataField(path, b is { } x ? JsonNode.Parse(x.GetRawText()) : null, a is { } y ? JsonNode.Parse(y.GetRawText()) : null));
        }

        void Walk(JsonElement b, JsonElement a, string path)
        {
            if (full) return;
            if (b.ValueKind == JsonValueKind.Object && a.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in a.EnumerateObject())
                {
                    if (skipName && path.Length == 0 && property.Name is "name") continue;
                    var child = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    if (b.TryGetProperty(property.Name, out var old)) { if (!JsonElement.DeepEquals(old, property.Value)) Walk(old, property.Value, child); }
                    else Add(child, null, property.Value);
                }
                foreach (var property in b.EnumerateObject())
                    if (!a.TryGetProperty(property.Name, out _))
                        Add(path.Length == 0 ? property.Name : $"{path}.{property.Name}", property.Value, null);
                return;
            }

            if (b.ValueKind == JsonValueKind.Array && a.ValueKind == JsonValueKind.Array && b.GetArrayLength() == a.GetArrayLength())
            {
                var i = 0;
                var left = b.EnumerateArray(); // not in a using: a read-only copy of the struct enumerator would never move
                foreach (var right in a.EnumerateArray())
                {
                    left.MoveNext();
                    if (!JsonElement.DeepEquals(left.Current, right)) Walk(left.Current, right, $"{path}[{i}]");
                    i++;
                }
                return;
            }

            Add(path, b, a);
        }
    }

    public static void Save(GameDataReport report, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(report, ReportJson), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The saved report, or null when there is none, it cannot be read or it is of another format.</summary>
    public static GameDataReport? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var report = JsonSerializer.Deserialize<GameDataReport>(File.ReadAllText(path), ReportJson);
            return report?.Format == ReportFormat ? report : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
