using System.Security.Cryptography;
using System.Text;

namespace FactorioModStudio.Tests;

/// <summary>
/// Builds fake Factorio installations on disk: <c>&lt;parent&gt;/&lt;dir&gt;/bin/x64/factorio</c> (a shell script that records every
/// start in <c>calls.txt</c> next to itself, touches the map file it is asked to <c>--create</c>) and
/// <c>&lt;dir&gt;/data/base/info.json</c> carrying the game version. Shared by the Factorio installation tests.
/// </summary>
internal static class FakeFactorio
{
    public static bool Unsupported => OperatingSystem.IsWindows() || !File.Exists("/bin/sh");

    public const string Script = """
        #!/bin/sh
        D="$(dirname "$0")"
        echo "$$ $*" >> "$D/calls.txt"
        prev=""
        for a in "$@"; do
          if [ "$prev" = "--create" ]; then : > "$a"; fi
          prev="$a"
        done
        echo "   0.100 Info Something.cpp:1: ok"
        exit 0
        """;

    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void WriteScript(string path, string? content = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, (content ?? Script).Replace("\r\n", "\n"));
        MakeExecutable(path);
    }

    public static void WriteInfo(string root, string? version, string? rawOverride = null)
    {
        var dir = Path.Combine(root, "data", "base");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "info.json"),
            rawOverride ?? (version is null
                ? """{"name":"base","title":"Base Mod","author":"Wube"}"""
                : $$"""{"name":"base","version":"{{version}}","title":"Base Mod","author":"Wube"}"""));
    }

    /// <summary>Creates <c>parent/dirName/bin/x64/factorio</c> and (unless <paramref name="version"/> is null and <paramref name="info"/> false) data/base/info.json. Returns the executable path.</summary>
    public static string Make(string parent, string dirName, string? version, bool info = true)
    {
        var root = Path.Combine(parent, dirName);
        var exe = Path.Combine(root, "bin", "x64", "factorio");
        WriteScript(exe);
        if (info) WriteInfo(root, version);
        return exe;
    }

    /// <summary>macOS-like layout: <c>parent/dirName/factorio.app/Contents/MacOS/factorio</c> + <c>.../Contents/data/base/info.json</c>.</summary>
    public static string MakeMac(string parent, string dirName, string? version)
    {
        var contents = Path.Combine(parent, dirName, "factorio.app", "Contents");
        var exe = Path.Combine(contents, "MacOS", "factorio");
        WriteScript(exe);
        WriteInfo(contents, version);
        return exe;
    }

    public static string RootOf(string exe) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", ".."));

    public static bool WasStarted(string exe) => File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "calls.txt"));

    public static string Calls(string exe)
    {
        var p = Path.Combine(Path.GetDirectoryName(exe)!, "calls.txt");
        if (!File.Exists(p)) return "";
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>First 12 lowercase hex chars of SHA-256 over the UTF-8 bytes of the path (contract of FactorioInstallation.Id).</summary>
    public static string Sha12(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..12];

    public static string Norm(string p) => Path.GetFullPath(p);

    public static bool Under(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

/// <summary>Records which executable was started (FakeRunner does not).</summary>
internal sealed class RecordingRunner : FactorioModStudio.Core.IProcessRunner
{
    public List<(string Exe, List<string> Args)> Calls { get; } = new();

    public FactorioModStudio.Core.ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine = null)
    {
        Calls.Add((executable, arguments.ToList()));
        return new FactorioModStudio.Core.ProcessResult(0, false, Array.Empty<string>());
    }
}
