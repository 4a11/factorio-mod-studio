using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Building blocks added to an existing mod (ModContent) and to a new one (ModScaffolder with Content).</summary>
public class ModContentTests
{
    private static TempDir Mod(string factorioVersion = "2.1", string? dataLua = "-- data\n", string? locale = "[mod-name]\nmy-mod=My mod\n")
    {
        var dir = new TempDir();
        dir.Write("info.json", $$"""{ "name": "my-mod", "version": "0.1.0", "title": "My mod", "factorio_version": "{{factorioVersion}}" }""");
        if (dataLua is not null) dir.Write("data.lua", dataLua);
        if (locale is not null) dir.Write("locale/en/my-mod.cfg", locale);
        return dir;
    }

    private static string Read(TempDir dir, string relative) => File.ReadAllText(dir.Combine(relative.Split('/')));

    private static Dictionary<string, string> Snapshot(TempDir dir) =>
        Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir.Path, f).Replace('\\', '/'), File.ReadAllText);

    private static ContentError ErrorOf(Action action) => Assert.Throws<ContentException>(action).Kind;

    [Fact]
    public void Apply_AddsThePrototypeFile_TheHelper_ARequire_AndTheEnglishNames()
    {
        using var mod = Mod();
        var plan = ModContent.Apply(mod.Path, new ContentRequest("armor", "my-armor", "Shiny armor"));

        Assert.Equal(new[] { "prototypes/my-armor.lua", ModContent.HelperPath }, plan.Create);
        Assert.Equal(new[] { "data.lua", "locale/en/my-mod.cfg" }, plan.Change);
        Assert.Equal(new[] { "my-armor" }, plan.Prototypes);
        Assert.Equal("Shiny armor", plan.Title);

        var lua = Read(mod, "prototypes/my-armor.lua");
        Assert.Contains("local name = \"my-armor\"", lua);
        Assert.Contains("require(\"prototypes.fms-copy-v1\")", lua);
        Assert.DoesNotContain("@@", lua);
        Assert.Contains("function copy.item(", Read(mod, ModContent.HelperPath));
        Assert.Equal("-- data\nrequire(\"prototypes.my-armor\")\n", Read(mod, "data.lua"));
        var locale = Read(mod, "locale/en/my-mod.cfg");
        Assert.Contains("[item-name]\nmy-armor=Shiny armor", locale);
        Assert.Contains("[mod-name]\nmy-mod=My mod", locale);
    }

    [Fact]
    public void Apply_ASecondBlock_ReusesTheHelper_AndAddsToTheSameSections()
    {
        using var mod = Mod();
        ModContent.Apply(mod.Path, new ContentRequest("turret", "t1"));
        var helper = Read(mod, ModContent.HelperPath);
        var plan = ModContent.Apply(mod.Path, new ContentRequest("wall", "w1"));

        Assert.Equal(new[] { "prototypes/w1.lua" }, plan.Create);
        Assert.Equal(helper, Read(mod, ModContent.HelperPath));
        Assert.Equal("-- data\nrequire(\"prototypes.t1\")\nrequire(\"prototypes.w1\")\n", Read(mod, "data.lua"));
        var locale = Read(mod, "locale/en/my-mod.cfg");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(locale, @"^\[entity-name\]", System.Text.RegularExpressions.RegexOptions.Multiline));
        Assert.Contains("t1=T1\nw1=W1", locale);
    }

    [Fact]
    public void Apply_DoesNotRepeatARequire_OrReplaceALocaleKey_ThatIsAlreadyThere()
    {
        using var mod = Mod(dataLua: "require(\"prototypes.car1\")\n", locale: "[entity-name]\ncar1=My own name\n");
        var plan = ModContent.Apply(mod.Path, new ContentRequest("car", "car1"));

        Assert.DoesNotContain("data.lua", plan.Change);
        Assert.Equal("require(\"prototypes.car1\")\n", Read(mod, "data.lua"));
        var locale = Read(mod, "locale/en/my-mod.cfg");
        Assert.Contains("car1=My own name", locale);
        Assert.DoesNotContain("car1=Car1", locale);
        Assert.Contains("[entity-description]\ncar1=", locale);
    }

    [Fact]
    public void Apply_CreatesDataLuaAndTheLocaleFile_WhenTheModHasNone()
    {
        using var mod = Mod(dataLua: null, locale: null);
        var plan = ModContent.Apply(mod.Path, new ContentRequest("pole", "p1"));

        Assert.Contains("data.lua", plan.Create);
        Assert.Contains("locale/en/my-mod.cfg", plan.Create);
        Assert.Empty(plan.Change);
        Assert.EndsWith("require(\"prototypes.p1\")\n", Read(mod, "data.lua"));
        Assert.StartsWith("[entity-name]\np1=P1\n", Read(mod, "locale/en/my-mod.cfg"));
    }

    [Fact]
    public void Apply_KeepsTheLineEndingsOfFilesItChanges()
    {
        using var mod = Mod(dataLua: "-- data\r\n", locale: "[mod-name]\r\nmy-mod=My mod\r\n");
        ModContent.Apply(mod.Path, new ContentRequest("drill", "d1"));

        Assert.Equal("-- data\r\nrequire(\"prototypes.d1\")\r\n", Read(mod, "data.lua"));
        var locale = Read(mod, "locale/en/my-mod.cfg");
        Assert.Contains("[entity-name]\r\nd1=D1\r\n", locale);
        Assert.DoesNotContain("\r\r", locale);
        Assert.DoesNotMatch(@"[^\r]\n", locale); // every line break is \r\n
    }

    [Fact]
    public void Plan_WritesNothing()
    {
        using var mod = Mod();
        var before = Snapshot(mod);
        var plan = ModContent.Plan(mod.Path, new ContentRequest("belt", "fast-belt"));

        Assert.Equal(before, Snapshot(mod));
        Assert.Equal(new[] { "fast-belt", "fast-belt-underground-belt", "fast-belt-splitter" }, plan.Prototypes);
    }

    [Fact]
    public void Apply_AnExistingPrototypeFile_IsAConflict_AndChangesNothing()
    {
        using var mod = Mod();
        mod.Write("prototypes/Taken.lua", "-- mine");
        var before = Snapshot(mod);

        var ex = Assert.Throws<ContentException>(() => ModContent.Apply(mod.Path, new ContentRequest("armor", "taken")));
        Assert.Equal(ContentError.Exists, ex.Kind);
        Assert.Equal("prototypes/Taken.lua", ex.ExistingPath);
        Assert.Equal(before, Snapshot(mod));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("dot.ted")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    public void Apply_InvalidName_IsRejected(string name)
    {
        using var mod = Mod();
        var before = Snapshot(mod);
        Assert.Equal(ContentError.InvalidName, ErrorOf(() => ModContent.Apply(mod.Path, new ContentRequest("armor", name))));
        Assert.Equal(before, Snapshot(mod));
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("tab\there")]
    public void Apply_TitleWithControlCharacters_IsRejected(string title)
    {
        using var mod = Mod();
        Assert.Equal(ContentError.InvalidTitle, ErrorOf(() => ModContent.Apply(mod.Path, new ContentRequest("armor", "a1", title))));
    }

    [Fact]
    public void Apply_ChecksTheBlock_TheGameVersion_AndThatItIsAMod()
    {
        using var mod = Mod();
        Assert.Equal(ContentError.UnknownBlock, ErrorOf(() => ModContent.Apply(mod.Path, new ContentRequest("dragon", "d"))));

        using var old = Mod(factorioVersion: "1.1");
        Assert.Equal(ContentError.UnsupportedVersion, ErrorOf(() => ModContent.Apply(old.Path, new ContentRequest("armor", "a"))));
        Assert.False(Directory.Exists(old.Combine("prototypes")));

        using var empty = new TempDir();
        Assert.Equal(ContentError.NotAMod, ErrorOf(() => ModContent.Apply(empty.Path, new ContentRequest("armor", "a"))));
    }

    [Fact]
    public void EveryBlock_RendersWithoutLeftoverTokens_AndNamesItsPrototypes()
    {
        Assert.Equal(18, ModContent.Blocks.Count);
        Assert.Equal(new[] { "combat", "logistics", "production", "vehicles" }, ModContent.Blocks.Select(b => b.Category).Distinct());
        foreach (var block in ModContent.Blocks)
        {
            using var mod = Mod();
            var plan = ModContent.Apply(mod.Path, new ContentRequest(block.Id, "x-" + block.DefaultName));
            var lua = Read(mod, $"prototypes/x-{block.DefaultName}.lua");
            Assert.DoesNotContain("@@", lua);
            Assert.DoesNotContain("@@", Read(mod, "locale/en/my-mod.cfg"));
            Assert.Contains($"\"x-{block.DefaultName}\"", lua);
            Assert.All(plan.Prototypes, p => Assert.StartsWith("x-" + block.DefaultName, p));
            Assert.Equal(new[] { "2.0", "2.1" }, block.SupportedVersions);
        }
    }

    [Fact]
    public void NewMod_WithContent_GetsEveryBlock_NamedAfterTheMod()
    {
        using var parent = new TempDir();
        var result = ModScaffolder.Create(new ScaffoldRequest("blocky", FactorioVersion: "2.1", Title: "Blocky", Content: new[] { "car", "belt" }), parent.Path);

        Assert.Contains("prototypes/blocky-car.lua", result.Files);
        Assert.Contains("prototypes/blocky-belt.lua", result.Files);
        Assert.Contains(ModContent.HelperPath, result.Files);
        var data = File.ReadAllText(Path.Combine(result.Folder, "data.lua"));
        Assert.Contains("require(\"prototypes.blocky-car\")", data);
        Assert.Contains("blocky-belt=Blocky belt", File.ReadAllText(Path.Combine(result.Folder, "locale", "en", "blocky.cfg")));
        Assert.Equal(Directory.EnumerateFiles(result.Folder, "*", SearchOption.AllDirectories).Count(), result.Files.Count);
    }

    [Fact]
    public void NewMod_WithContent_ForAnOlderGame_OrAnUnknownBlock_IsNotCreated()
    {
        using var parent = new TempDir();
        var old = Assert.Throws<ScaffoldException>(() => ModScaffolder.Create(new ScaffoldRequest("old", FactorioVersion: "1.1", Content: new[] { "car" }), parent.Path));
        Assert.Equal(ScaffoldError.UnsupportedVersion, old.Kind);
        var unknown = Assert.Throws<ScaffoldException>(() => ModScaffolder.Create(new ScaffoldRequest("odd", FactorioVersion: "2.0", Content: new[] { "dragon" }), parent.Path));
        Assert.Equal(ScaffoldError.UnknownTemplate, unknown.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Path));
    }
}
