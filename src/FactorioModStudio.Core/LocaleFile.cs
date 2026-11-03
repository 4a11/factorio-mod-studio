namespace FactorioModStudio.Core;

/// <summary>Reader for Factorio's locale .cfg files: <c>[section]</c> headers followed by <c>key=value</c> lines.</summary>
public sealed class LocaleData
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Dictionary<string, string>> Sections => _sections;
    public int KeyCount => _sections.Values.Sum(s => s.Count);

    public string? Find(string section, string key) =>
        _sections.TryGetValue(section, out var keys) && keys.TryGetValue(key, out var value) ? value : null;

    public void Merge(IEnumerable<string> lines)
    {
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var section = line[1..^1];
                if (!_sections.TryGetValue(section, out current))
                    _sections[section] = current = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
    }

    /// <summary>Loads every .cfg under <c>locale/&lt;language&gt;</c>.</summary>
    public static LocaleData Load(string modDirectory, string language)
    {
        var data = new LocaleData();
        var dir = Path.Combine(modDirectory, "locale", language);
        if (!Directory.Exists(dir)) return data;

        foreach (var file in Directory.EnumerateFiles(dir, "*.cfg", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try { data.Merge(File.ReadLines(file)); }
            catch (IOException) { /* unreadable file: skip */ }
        }

        return data;
    }

    public static IReadOnlyList<string> Languages(string modDirectory)
    {
        var dir = Path.Combine(modDirectory, "locale");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
