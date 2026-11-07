using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Finding and reading factorio-current.log (FactorioLog).</summary>
public class FactorioLogTests
{
    private static string FakeInstall(TempDir dir, string? configPath)
    {
        dir.Write("data/base/info.json", """{ "name": "base", "version": "2.1.0" }""");
        if (configPath is not null) dir.Write("config-path.cfg", configPath);
        return dir.Write(OperatingSystem.IsWindows() ? "bin/x64/factorio.exe" : "bin/x64/factorio", "");
    }

    [Fact]
    public void PortableInstall_KeepsItsLogNextToTheGame()
    {
        using var dir = new TempDir();
        var exe = FakeInstall(dir, "config-path=__PATH__executable__/../..\nuse-system-read-write-data-directories=false\n");
        Assert.Equal(dir.Combine(FactorioLog.FileName), FactorioLog.CurrentLogPath(exe));
    }

    [Fact]
    public void RegularInstall_KeepsItsLogInTheUsersDataFolder()
    {
        using var dir = new TempDir();
        var exe = FakeInstall(dir, "config-path=__PATH__system-write-data__\nuse-system-read-write-data-directories=true\n");
        var path = FactorioLog.CurrentLogPath(exe)!;
        Assert.EndsWith(FactorioLog.FileName, path);
        Assert.False(path.StartsWith(dir.Path, StringComparison.OrdinalIgnoreCase));
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        Assert.Contains(folder, new[] { "Factorio", "factorio", ".factorio" });
    }

    [Fact]
    public void SomethingThatIsNoInstall_HasNoLog()
    {
        using var dir = new TempDir();
        Assert.Null(FactorioLog.CurrentLogPath(dir.Write("factorio.exe", "")));
    }

    [Fact]
    public void ReadIfWrittenSince_SkipsAnOldLog_AndReadsAFreshOne_EvenWhileTheGameHoldsItOpen()
    {
        using var dir = new TempDir();
        var log = dir.Write("factorio-current.log", "   0.1 Info Main.cpp:1: old run\n");
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddHours(-1));
        Assert.Null(FactorioLog.ReadIfWrittenSince(log, DateTime.UtcNow));
        Assert.Null(FactorioLog.ReadIfWrittenSince(dir.Combine("missing.log"), DateTime.UtcNow));
        Assert.Null(FactorioLog.ReadIfWrittenSince(null, DateTime.UtcNow));

        var since = DateTime.UtcNow;
        using (var game = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            using var writer = new StreamWriter(game);
            writer.Write("   0.1 Info Main.cpp:1: new run\n   0.3 Error Script.cpp:2: boom\n");
            writer.Flush();
            var lines = FactorioLog.ReadIfWrittenSince(log, since);
            Assert.NotNull(lines);
            Assert.Equal(2, lines!.Count);
            Assert.Equal(1, LogParser.Parse(lines).ErrorCount);
        }
    }
}
