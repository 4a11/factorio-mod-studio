using System.Text.Json;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public class ProfileBuilderTests
{
    private static ProjectContext Project(TempDir root, string configJson = "{}") =>
        new(root.Path, ProjectConfig.Parse(configJson), configFileExists: true);

    private static string[] EnabledInModList(string modsDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modsDir, "mod-list.json")));
        return doc.RootElement.GetProperty("mods").EnumerateArray()
            .Where(m => m.GetProperty("enabled").GetBoolean())
            .Select(m => m.GetProperty("name").GetString()!)
            .ToArray();
    }

    [Fact]
    public void Prepare_CopiesProjectModAndWritesModList()
    {
        using var root = Fixtures.ValidMod();
        var prepared = ProfileBuilder.Prepare(Project(root), "default");

        Assert.Equal(new[] { "base", "my-mod" }, prepared.EnabledMods);
        Assert.True(File.Exists(Path.Combine(prepared.ModsDirectory, "my-mod_1.2.3", "info.json")));
        Assert.Equal(new[] { "base", "my-mod" }, EnabledInModList(prepared.ModsDirectory));
    }

    [Fact]
    public void Prepare_ResolvesTransitiveDependenciesAndPicksHighestVersion()
    {
        using var root = Fixtures.ValidMod("""
            { "name": "my-mod", "version": "1.2.3", "title": "t", "author": "a", "factorio_version": "2.0",
              "dependencies": ["base >= 2.0.0", "flib >= 0.14.0", "? optional-thing"] }
            """);
        using var installed = new TempDir();
        Fixtures.WriteModZip(installed.Path, "flib", "0.14.0", "base");
        Fixtures.WriteModZip(installed.Path, "flib", "0.15.0", "base", "stdlib >= 1.0.0");
        Fixtures.WriteModZip(installed.Path, "stdlib", "1.4.0", "base");

        var prepared = ProfileBuilder.Prepare(Project(root, $$"""{ "modsSource": "{{installed.Path.Replace("\\", "\\\\")}}" }"""), "default");

        Assert.Equal(new[] { "base", "my-mod", "flib", "stdlib" }, prepared.EnabledMods);
        Assert.True(File.Exists(Path.Combine(prepared.ModsDirectory, "flib_0.15.0.zip")));
        Assert.False(File.Exists(Path.Combine(prepared.ModsDirectory, "flib_0.14.0.zip")));
        Assert.True(File.Exists(Path.Combine(prepared.ModsDirectory, "stdlib_1.4.0.zip")));
        Assert.Empty(prepared.Warnings);
    }

    [Fact]
    public void Prepare_EnablesExtraModsFromProfile()
    {
        using var root = Fixtures.ValidMod();
        using var installed = new TempDir();
        Fixtures.WriteModZip(installed.Path, "helper", "2.0.0", "base");
        var config = $$"""{ "modsSource": "{{installed.Path.Replace("\\", "\\\\")}}", "profiles": { "dev": { "mods": ["helper >= 2.0.0"] } } }""";

        var prepared = ProfileBuilder.Prepare(Project(root, config), "dev");

        Assert.Contains("helper", prepared.EnabledMods);
    }

    [Fact]
    public void Prepare_MissingDependency_Throws()
    {
        using var root = Fixtures.ValidMod("""
            { "name": "my-mod", "version": "1.0.0", "title": "t", "author": "a", "factorio_version": "2.0",
              "dependencies": ["base", "nowhere-to-be-found"] }
            """);
        var ex = Assert.Throws<FmsException>(() => ProfileBuilder.Prepare(Project(root), "default"));
        Assert.Contains("nowhere-to-be-found", ex.Message);
    }

    [Fact]
    public void Prepare_WarnsWhenOnlyAnOutdatedVersionIsAvailable()
    {
        using var root = Fixtures.ValidMod("""
            { "name": "my-mod", "version": "1.0.0", "title": "t", "author": "a", "factorio_version": "2.0",
              "dependencies": ["base", "flib >= 0.15.0"] }
            """);
        using var installed = new TempDir();
        Fixtures.WriteModZip(installed.Path, "flib", "0.14.0", "base");

        var prepared = ProfileBuilder.Prepare(Project(root, $$"""{ "modsSource": "{{installed.Path.Replace("\\", "\\\\")}}" }"""), "default");

        Assert.Single(prepared.Warnings);
        Assert.Contains("0.14.0", prepared.Warnings[0]);
    }

    [Fact]
    public void Prepare_DisablesBundledDlcForFactorio2_ButNotWhenRequested()
    {
        using var root = Fixtures.ValidMod();
        var plain = ProfileBuilder.Prepare(Project(root), "default");
        var listed = File.ReadAllText(Path.Combine(plain.ModsDirectory, "mod-list.json"));
        Assert.Contains("space-age", listed);
        Assert.DoesNotContain("space-age", EnabledInModList(plain.ModsDirectory));

        var config = """{ "profiles": { "dlc": { "mods": ["space-age"] } } }""";
        var withDlc = ProfileBuilder.Prepare(Project(root, config), "dlc");
        Assert.Contains("space-age", EnabledInModList(withDlc.ModsDirectory));
        Assert.DoesNotContain("quality", EnabledInModList(withDlc.ModsDirectory));
    }

    [Fact]
    public void Prepare_KeepsModSettings_AndRemovesStaleMods()
    {
        using var root = Fixtures.ValidMod();
        var first = ProfileBuilder.Prepare(Project(root), "default");
        File.WriteAllText(Path.Combine(first.ModsDirectory, "mod-settings.dat"), "settings");
        File.WriteAllText(Path.Combine(first.ModsDirectory, "stale_1.0.0.zip"), "old");

        var second = ProfileBuilder.Prepare(Project(root), "default");

        Assert.True(File.Exists(Path.Combine(second.ModsDirectory, "mod-settings.dat")));
        Assert.False(File.Exists(Path.Combine(second.ModsDirectory, "stale_1.0.0.zip")));
    }

    [Fact]
    public void Prepare_DoesNotRecurseIntoItsOwnStateDirectory()
    {
        using var root = Fixtures.ValidMod();
        ProfileBuilder.Prepare(Project(root), "default");
        var again = ProfileBuilder.Prepare(Project(root), "default");

        Assert.False(Directory.Exists(Path.Combine(again.ModsDirectory, "my-mod_1.2.3", ".fmstudio")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void GetProfile_RejectsUnsafeNames(string name)
    {
        using var root = Fixtures.ValidMod();
        Assert.Throws<FmsException>(() => Project(root).GetProfile(name));
    }

    [Fact]
    public void GetProfile_UnknownNamedProfile_Throws()
    {
        using var root = Fixtures.ValidMod();
        Assert.Throws<FmsException>(() => Project(root).GetProfile("nope"));
    }

    [Fact]
    public void Config_AllowsCommentsAndTrailingCommas()
    {
        var config = ProjectConfig.Parse("""
            {
              // a comment
              "modPath": "src/mod",
              "profiles": { "a": { "mods": ["x",], }, },
            }
            """);
        Assert.Equal("src/mod", config.ModPath);
        Assert.Equal(new[] { "x" }, config.Profiles["a"].Mods);
    }

    [Fact]
    public void ConfigTemplate_IsValid()
    {
        var config = ProjectConfig.Parse(ProjectConfig.Template);
        Assert.Contains("default", config.Profiles.Keys);
    }
}
