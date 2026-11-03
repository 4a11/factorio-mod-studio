using System.IO.Compression;

namespace FactorioModStudio.Core;

public sealed record PackResult(bool Success, string? ArchivePath, ValidationResult Validation, int FileCount, long ArchiveBytes);

public static class ModPackager
{
    /// <summary>
    /// Validates the mod and, if there are no errors (or <paramref name="force"/> is set), writes
    /// <c>{name}_{version}.zip</c> into <paramref name="outputDirectory"/>. Inside the archive everything
    /// lives under a top-level folder <c>{name}_{version}/</c>, as Factorio and the mod portal expect.
    /// </summary>
    public static PackResult Pack(string modDirectory, string outputDirectory, bool force = false)
    {
        var validation = ModValidator.Validate(modDirectory);
        var info = validation.Info;

        if (info?.Name is null || !ModVersion.TryParse(info.Version, out var version))
            return new PackResult(false, null, validation, 0, 0);
        if (validation.HasErrors && !force)
            return new PackResult(false, null, validation, 0, 0);

        Directory.CreateDirectory(outputDirectory);

        var folder = $"{info.Name}_{version}";
        var archivePath = Path.GetFullPath(Path.Combine(outputDirectory, folder + ".zip"));
        var temp = archivePath + ".tmp";
        var files = ModFiles.Enumerate(modDirectory, outputDirectory);

        try
        {
            using (var zip = new ZipArchive(File.Create(temp), ZipArchiveMode.Create))
            {
                foreach (var relative in files)
                {
                    var source = Path.Combine(modDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                    var entry = zip.CreateEntry($"{folder}/{relative}", CompressionLevel.Optimal);
                    entry.LastWriteTime = ClampZipTime(File.GetLastWriteTime(source));

                    using var input = File.OpenRead(source);
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
            }

            File.Move(temp, archivePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        return new PackResult(true, archivePath, validation, files.Count, new FileInfo(archivePath).Length);
    }

    private static DateTimeOffset ClampZipTime(DateTime time)
    {
        var min = new DateTime(1980, 1, 1);
        return time < min ? min : time;
    }
}
