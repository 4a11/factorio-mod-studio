using System.Text.Json;
using System.Text.Json.Nodes;
using FactorioModStudio.Core;

namespace FactorioModStudio.Server;

/// <summary>
/// The Factorio installations registered in the web UI, stored per project in <c>.fmstudio/settings.json</c> as
/// <c>"factorioInstallations": [ { "path", "label" } ]</c> (never in fmstudio.json: paths are personal).
/// Safety: whatever is saved here later gets EXECUTED, so only a file named <c>factorio</c> / <c>factorio.exe</c> is accepted,
/// both when saving and when the stored value is used (<see cref="FactorioInstallations.Candidates"/> re-checks). The file is
/// never run while saving. The legacy single <c>"factorioPath"</c> is read as one registered installation and dropped on the next write.
/// </summary>
internal sealed class FactorioSettings
{
    public const string FileName = "settings.json";
    public const string ListProperty = "factorioInstallations";
    public const string LegacyProperty = "factorioPath";
    public const int MaxPathLength = FactorioInstallations.MaxPathLength;
    public const int MaxInstallations = 50;
    public const int MaxLabelLength = 80;

    private readonly string _file;
    private readonly object _gate = new();

    public FactorioSettings(string stateDirectory) => _file = Path.Combine(stateDirectory, FileName);

    /// <summary>The registered installations (empty when there are none or the file is missing / corrupt).</summary>
    public IReadOnlyList<RegisteredInstallation> Read()
    {
        lock (_gate) return ReadList(ReadObject());
    }

    /// <summary>Adds an installation (already validated) or updates the label of the one with the same path. Returns true when it was new.</summary>
    public bool Upsert(string resolvedPath, string? label)
    {
        lock (_gate)
        {
            var obj = ReadObject() ?? new JsonObject();
            var list = ReadList(obj).ToList();
            var index = list.FindIndex(r => SamePath(r.Path, resolvedPath));
            var isNew = index < 0;
            if (isNew)
            {
                if (list.Count >= MaxInstallations) throw new InvalidOperationException($"At most {MaxInstallations} installations can be registered.");
                list.Add(new RegisteredInstallation(resolvedPath, label));
            }
            else list[index] = new RegisteredInstallation(list[index].Path, label);
            Save(obj, list);
            return isNew;
        }
    }

    /// <summary>Removes the registered installation with this id. Returns false when there is none.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var obj = ReadObject() ?? new JsonObject();
            var list = ReadList(obj).ToList();
            var removed = list.RemoveAll(r => string.Equals(FactorioInstallations.IdOf(r.Path), id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Save(obj, list);
            return true;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void Save(JsonObject obj, IReadOnlyList<RegisteredInstallation> list)
    {
        obj.Remove(LegacyProperty);
        var array = new JsonArray();
        foreach (var item in list)
        {
            var entry = new JsonObject { ["path"] = item.Path };
            if (item.Label is not null) entry["label"] = item.Label;
            array.Add(entry);
        }
        obj[ListProperty] = array;

        var directory = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _file, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static List<RegisteredInstallation> ReadList(JsonObject? obj)
    {
        var list = new List<RegisteredInstallation>();
        if (obj is null) return list;

        if (obj[ListProperty] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (list.Count >= MaxInstallations) break;
                if (node is not JsonObject item || item["path"] is not JsonValue p || !p.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength) continue;
                string? label = null;
                if (item["label"] is JsonValue l && l.TryGetValue<string>(out var text)) label = CleanLabel(text);
                if (!list.Any(r => SamePath(r.Path, path))) list.Add(new RegisteredInstallation(path, label));
            }
        }

        // Legacy single path from the previous version of this setting.
        if (obj[LegacyProperty] is JsonValue legacy && legacy.TryGetValue<string>(out var old) && !string.IsNullOrWhiteSpace(old)
            && old.Length <= MaxPathLength && list.Count < MaxInstallations && !list.Any(r => SamePath(r.Path, old)))
            list.Add(new RegisteredInstallation(old, null));
        return list;
    }

    private JsonObject? ReadObject()
    {
        try
        {
            if (!File.Exists(_file)) return null;
            var info = new FileInfo(_file);
            if (info.Length > 64 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return JsonNode.Parse(File.ReadAllText(_file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Trims; null when empty or unusable (control characters, longer than 80 characters).</summary>
    public static string? CleanLabel(string? label)
    {
        var text = label?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > MaxLabelLength || text.Any(char.IsControl) ? null : text;
    }

    /// <summary>Returns an error message for a label that cannot be stored, else null (null / blank labels are fine).</summary>
    public static string? CheckLabel(string? label)
    {
        var text = label?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Any(char.IsControl)) return "The label contains control characters.";
        if (text.Length > MaxLabelLength) return $"The label is too long (limit {MaxLabelLength} characters).";
        return null;
    }

    public static string? Validate(string? input, out string? executable) => FactorioInstallations.CheckExecutable(input, out executable);

    public static bool IsFactorioName(string name) => FactorioInstallations.IsFactorioName(name);
}
