using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using FactorioModStudio.Core;

namespace FactorioModStudio.Tests;

public class AuditRegressionTests
{
    [Theory]
    [InlineData("../escaped")]
    [InlineData("..\\escaped")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("bad:name")]
    [InlineData("bad\n")]
    public void ForcedPack_RejectsUnsafeName(string name)
    {
        using var mod = Fixtures.ValidMod(JsonSerializer.Serialize(new { name, version = "1.2.3", factorio_version = "2.0", title = "Test", author = "Test" }));
        using var output = new TempDir();
        var result = ModPackager.Pack(mod.Path, output.Path, force: true);
        Assert.False(result.Success);
        Assert.Null(result.ArchivePath);
        Assert.Empty(Directory.GetFiles(output.Path));
    }

    [Theory]
    [InlineData(1970, 1980)]
    [InlineData(2108, 2107)]
    public void Pack_ClampsBothZipDateBounds(int year, int expected)
    {
        using var mod = Fixtures.ValidMod();
        using var output = new TempDir();
        File.SetLastWriteTime(mod.Combine("data.lua"), new DateTime(year, 1, 1));
        var result = ModPackager.Pack(mod.Path, output.Path);
        Assert.True(result.Success);
        using var zip = ZipFile.OpenRead(result.ArchivePath!);
        Assert.Equal(expected, zip.GetEntry("my-mod_1.2.3/data.lua")!.LastWriteTime.Year);
    }

    private static void LinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Enumeration_ExcludesExternalDirectoryAndCycle()
    {
        using var mod = Fixtures.ValidMod();
        using var outside = new TempDir();
        outside.Write("secret.txt", "private");
        var link = mod.Combine("external");
        var cycle = mod.Combine("cycle");
        LinkDirectory(link, outside.Path);
        try
        {
            LinkDirectory(cycle, mod.Path);
            try
            {
                var files = ModFiles.Enumerate(mod.Path);
                Assert.DoesNotContain(files, f => f.StartsWith("external/") || f.StartsWith("cycle/"));
                Assert.Contains("data.lua", files);
            }
            finally { Directory.Delete(cycle); }
            using var output = new TempDir();
            var result = ModPackager.Pack(mod.Path, output.Path);
            Assert.True(result.Success);
            using var zip = ZipFile.OpenRead(result.ArchivePath!);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("secret.txt"));
        }
        finally { Directory.Delete(link); }
        Assert.Equal("private", File.ReadAllText(outside.Combine("secret.txt")));
    }

    [Fact]
    public void Enumeration_ExcludesFileSymlink()
    {
        // File symlinks require privileges on Windows; the Linux CI job covers this case.
        if (OperatingSystem.IsWindows()) return;
        using var mod = Fixtures.ValidMod();
        using var outside = new TempDir();
        var secret = outside.Write("secret.txt", "private");
        var link = mod.Combine("linked.txt");
        File.CreateSymbolicLink(link, secret);
        try { Assert.DoesNotContain("linked.txt", ModFiles.Enumerate(mod.Path)); }
        finally { File.Delete(link); }
    }

    [Fact]
    public void CreateLanguage_RejectsLinkedLocaleWithoutExternalWrites()
    {
        using var mod = new TempDir();
        using var outside = new TempDir();
        outside.Write("en/strings.cfg", "[item-name]\nx=X\n");
        var link = mod.Combine("locale");
        LinkDirectory(link, outside.Path);
        try
        {
            Assert.Throws<FmsException>(() => LocaleEditor.CreateLanguage(mod.Path, "ru"));
            Assert.False(Directory.Exists(outside.Combine("ru")));
            Assert.Equal("[item-name]\nx=X\n", File.ReadAllText(outside.Combine("en", "strings.cfg")));
        }
        finally { Directory.Delete(link); }
    }
}
