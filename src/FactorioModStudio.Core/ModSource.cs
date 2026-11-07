using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

public sealed record AvailableMod(
    string Name,
    ModVersion Version,
    string Path,
    bool IsDirectory,
    IReadOnlyList<Dependency> Dependencies,
    string? Title = null);

/// <summary>Scans a folder (typically Factorio's "mods" directory) for installed mods, both zips and unpacked folders.</summary>
public static class ModSource
{
    private static readonly Regex InfoEntry = new(@"^[^/]+/info\.json$", RegexOptions.Compiled);

    public static List<AvailableMod> Scan(string directory)
    {
        var result = new List<AvailableMod>();
        if (!Directory.Exists(directory)) return result;

        foreach (var zipPath in Directory.EnumerateFiles(directory, "*.zip"))
        {
            var mod = TryReadZip(zipPath);
            if (mod is not null) result.Add(mod);
        }

        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            var infoPath = System.IO.Path.Combine(dir, "info.json");
            if (!File.Exists(infoPath)) continue;
            var mod = TryRead(File.ReadAllText(infoPath), dir, isDirectory: true);
            if (mod is not null) result.Add(mod);
        }

        return result;
    }

    private static AvailableMod? TryReadZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => InfoEntry.IsMatch(e.FullName));
            if (entry is null) return null;

            using var reader = new StreamReader(entry.Open());
            return TryRead(reader.ReadToEnd(), zipPath, isDirectory: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null; // not a readable mod archive; ignore it
        }
    }

    private static AvailableMod? TryRead(string infoJson, string path, bool isDirectory)
    {
        try
        {
            var info = ModInfo.Parse(infoJson);
            if (info.Name is null || !ModVersion.TryParse(info.Version, out var version)) return null;
            return new AvailableMod(info.Name, version, path, isDirectory, info.ParsedDependencies.ToList(), info.Title);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
