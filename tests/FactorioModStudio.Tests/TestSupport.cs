using System.IO.Compression;
using FactorioModStudio.Core;

namespace FactorioModStudio.Tests;

/// <summary>A throw-away directory that is deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fmstudio-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string Write(string relativePath, string content)
    {
        var full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class Fixtures
{
    public const string ValidInfo = """
        {
          "name": "my-mod", "version": "1.2.3", "title": "My Mod", "author": "Someone",
          "factorio_version": "2.0", "dependencies": ["base >= 2.0.0"]
        }
        """;

    /// <summary>Writes just enough bytes (signature + IHDR width/height) for the validator's PNG size check.</summary>
    public static void WritePngHeader(string path, int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13; // IHDR length
        "IHDR"u8.ToArray().CopyTo(bytes, 12);
        for (var i = 0; i < 4; i++)
        {
            bytes[16 + i] = (byte)(width >> (24 - 8 * i));
            bytes[20 + i] = (byte)(height >> (24 - 8 * i));
        }
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Creates a mod that passes validation without any issue.</summary>
    public static TempDir ValidMod(string info = ValidInfo)
    {
        var dir = new TempDir();
        dir.Write("info.json", info);
        dir.Write("changelog.txt", new string('-', 99) + "\nVersion: 1.2.3\nDate: 2026-01-01\n  Features:\n    - Initial release.\n");
        dir.Write("data.lua", "data:extend({})\n");
        WritePngHeader(dir.Combine("thumbnail.png"), 144, 144);
        return dir;
    }

    /// <summary>Creates an installed-mod zip (<c>name_version.zip</c> with a single top-level folder) in <paramref name="directory"/>.</summary>
    public static void WriteModZip(string directory, string name, string version, params string[] dependencies)
    {
        Directory.CreateDirectory(directory);
        var deps = string.Join(", ", dependencies.Select(d => $"\"{d}\""));
        var info = $$"""{"name":"{{name}}","version":"{{version}}","title":"{{name}}","author":"x","factorio_version":"2.0","dependencies":[{{deps}}]}""";

        using var zip = ZipFile.Open(System.IO.Path.Combine(directory, $"{name}_{version}.zip"), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry($"{name}_{version}/info.json").Open())) writer.Write(info);
        using (var writer = new StreamWriter(zip.CreateEntry($"{name}_{version}/data.lua").Open())) writer.Write("");
    }
}

/// <summary>Scriptable stand-in for Factorio.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    private readonly Func<IReadOnlyList<string>, ProcessResult> _behavior;

    public FakeRunner(Func<IReadOnlyList<string>, ProcessResult> behavior) => _behavior = behavior;

    public List<IReadOnlyList<string>> Calls { get; } = new();

    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine = null)
    {
        Calls.Add(arguments.ToList());
        var result = _behavior(arguments);
        if (onLine is not null) foreach (var line in result.Lines) onLine(line);
        return result;
    }
}
