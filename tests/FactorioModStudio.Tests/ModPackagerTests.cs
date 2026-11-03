using System.IO.Compression;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public class ModPackagerTests
{
    private static List<string> EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Pack_CreatesZipWithVersionedRootFolder()
    {
        using var mod = Fixtures.ValidMod();
        using var output = new TempDir();

        var result = ModPackager.Pack(mod.Path, output.Path);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(output.Path, "my-mod_1.2.3.zip"), result.ArchivePath);
        var names = EntryNames(result.ArchivePath!);
        Assert.Contains("my-mod_1.2.3/info.json", names);
        Assert.Contains("my-mod_1.2.3/data.lua", names);
        Assert.All(names, n => Assert.StartsWith("my-mod_1.2.3/", n));
    }

    [Fact]
    public void Pack_ExcludesToolingAndPreviousBuilds()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write(".git/config", "x");
        mod.Write(".github/workflows/ci.yml", "x");
        mod.Write(".gitignore", "x");
        mod.Write("fmstudio.json", "{}");
        mod.Write("old_0.0.1.zip", "x");
        mod.Write("locale/en/strings.cfg", "[a]\nb=c\n");

        // The output folder lives inside the mod, as `fmstudio pack` does by default.
        var output = mod.Combine("dist");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "stale.txt"), "x");

        var result = ModPackager.Pack(mod.Path, output);
        var names = EntryNames(result.ArchivePath!);

        Assert.DoesNotContain(names, n => n.Contains(".git"));
        Assert.DoesNotContain(names, n => n.EndsWith(".zip"));
        Assert.DoesNotContain(names, n => n.Contains("fmstudio.json"));
        Assert.DoesNotContain(names, n => n.Contains("dist/"));
        Assert.Contains("my-mod_1.2.3/locale/en/strings.cfg", names);
    }

    [Fact]
    public void Pack_RefusesWhenValidationFails_UnlessForced()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "icon = \"__my-mod__/missing.png\"\n");
        using var output = new TempDir();

        var refused = ModPackager.Pack(mod.Path, output.Path);
        Assert.False(refused.Success);
        Assert.Empty(Directory.GetFiles(output.Path));

        var forced = ModPackager.Pack(mod.Path, output.Path, force: true);
        Assert.True(forced.Success);
        Assert.True(File.Exists(forced.ArchivePath));
    }

    [Fact]
    public void Pack_WithoutInfoJson_Fails()
    {
        using var empty = new TempDir();
        using var output = new TempDir();
        var result = ModPackager.Pack(empty.Path, output.Path, force: true);
        Assert.False(result.Success);
        Assert.Null(result.ArchivePath);
    }
}
