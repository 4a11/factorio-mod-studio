using System.IO.Compression;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <summary>Unpacks an installed mod zip into a cache folder so it can be inspected like a directory.</summary>
public static class ModExtractor
{
    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    private const string CompleteMarker = ".fmstudio-extracted";
    private static readonly Regex InfoEntry = new(@"^(?<root>[^/]+)/info\.json$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts <paramref name="zipPath"/> (without its top-level folder) below <paramref name="cacheRoot"/> and returns the folder.
    /// The result is reused as long as the zip's modification time is unchanged.
    /// </summary>
    public static string ExtractToCache(string zipPath, string cacheRoot)
    {
        var stamp = File.GetLastWriteTimeUtc(zipPath).Ticks;
        var safeName = Regex.Replace(Path.GetFileNameWithoutExtension(zipPath), @"[^A-Za-z0-9_.\- ]", "_");
        var target = Path.GetFullPath(Path.Combine(cacheRoot, $"{safeName}-{stamp}"));
        if (File.Exists(Path.Combine(target, CompleteMarker))) return target;

        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);

        using var zip = ZipFile.OpenRead(zipPath);
        var rootFolder = zip.Entries.Select(e => InfoEntry.Match(e.FullName)).FirstOrDefault(m => m.Success)?.Groups["root"].Value
                         ?? throw new FmsException($"\"{Path.GetFileName(zipPath)}\" is not a Factorio mod archive (no <folder>/info.json).");
        var prefix = rootFolder + "/";

        var targetPrefix = target + Path.DirectorySeparatorChar;
        long total = 0;

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;

            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(targetPrefix, StringComparison.Ordinal)) continue; // "zip slip": entry escapes the folder

            total += entry.Length;
            if (total > MaxTotalBytes) throw new FmsException($"\"{Path.GetFileName(zipPath)}\" is larger than {MaxTotalBytes / 1024 / 1024 / 1024} GB when unpacked; refusing to extract it.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        File.WriteAllText(Path.Combine(target, CompleteMarker), "");
        return target;
    }
}
