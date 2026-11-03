using System.IO.Compression;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public class ModInspectorTests
{
    private static TempDir ModWithContent()
    {
        var mod = Fixtures.ValidMod();
        mod.Write("data.lua", """
            data:extend({
              { type = "item", name = "my-item", icon = "__my-mod__/graphics/item.png", icon_size = 64, stack_size = 50 },
              {
                type = "recipe", name = "my-item", energy_required = 3, category = "crafting",
                ingredients = { {type = "item", name = "iron-plate", amount = 2}, {"copper-plate", 1} },
                results = { {type = "item", name = "my-item", amount = 1} },
              },
              { type = "recipe", name = "old-style", ingredients = { {"iron-plate", 4} }, result = "my-item", result_count = 3 },
              { type = "recipe", name = "legacy-normal", normal = { ingredients = { {"iron-plate", 9} }, result = "my-item" } },
            })
            data.raw.recipe["iron-plate"].energy_required = 1
            """);
        mod.Write("control.lua", "script.on_init(function() end)\n");
        mod.Write("settings.lua", "data:extend({ { type = \"bool-setting\", name = \"my-flag\", setting_type = \"startup\", default_value = true } })\n");
        mod.Write("migrations/1.2.3.lua", "-- migrate\n");
        mod.Write("graphics/item.png", "png");
        mod.Write("sounds/beep.ogg", "ogg");
        mod.Write("locale/en/strings.cfg", "[item-name]\nmy-item=My Item\n\n[recipe-name]\nold-style=Old style\n; comment\n[mod-setting-name]\nmy-flag=Flag\n");
        mod.Write("locale/de/strings.cfg", "[item-name]\nmy-item=Mein Ding\n");
        return mod;
    }

    [Fact]
    public void Inspect_CategorizesFilesAndLabelsEntryPoints()
    {
        using var mod = ModWithContent();
        var overview = ModInspector.Inspect(mod.Path);

        string CategoryOf(string path) => overview.Files.Single(f => f.Path == path).Category;
        Assert.Equal("script", CategoryOf("data.lua"));
        Assert.Equal("graphics", CategoryOf("graphics/item.png"));
        Assert.Equal("sound", CategoryOf("sounds/beep.ogg"));
        Assert.Equal("locale", CategoryOf("locale/en/strings.cfg"));
        Assert.Equal("data", CategoryOf("info.json"));
        Assert.Equal("text", CategoryOf("changelog.txt"));

        Assert.Equal("Data stage: define prototypes", overview.Files.Single(f => f.Path == "data.lua").Role);
        Assert.Equal("Runtime: event handlers and game logic", overview.Files.Single(f => f.Path == "control.lua").Role);
        Assert.Equal("Migration script", overview.Files.Single(f => f.Path == "migrations/1.2.3.lua").Role);
        Assert.Null(overview.Files.Single(f => f.Path == "graphics/item.png").Role);
    }

    [Fact]
    public void Inspect_ListsPrototypesWithDisplayNamesAndIcons()
    {
        using var mod = ModWithContent();
        var overview = ModInspector.Inspect(mod.Path);

        var item = overview.Prototypes.Single(p => p.Type == "item");
        Assert.Equal("my-item", item.Name);
        Assert.Equal("My Item", item.DisplayName);
        Assert.Equal("graphics/item.png", item.Icon);
        Assert.Equal("data.lua", item.File);
        Assert.Equal(2, item.Line);

        Assert.Contains(overview.Prototypes, p => p.Type == "bool-setting" && p.File == "settings.lua");
        Assert.Equal(new[] { "de", "en" }, overview.Languages);
    }

    [Fact]
    public void Inspect_ReadsBothRecipeStyles()
    {
        using var mod = ModWithContent();
        var recipes = ModInspector.Inspect(mod.Path).Recipes.ToDictionary(r => r.Name);

        var modern = recipes["my-item"];
        Assert.Equal("crafting", modern.Category);
        Assert.Equal(3.0, modern.EnergyRequired);
        Assert.Equal(new[] { "iron-plate", "copper-plate" }, modern.Ingredients.Select(i => i.Name));
        Assert.Equal(new double?[] { 2, 1 }, modern.Ingredients.Select(i => i.Amount));
        Assert.Equal("my-item", modern.Results.Single().Name);

        var old = recipes["old-style"];
        Assert.Equal("Old style", old.DisplayName);
        Assert.Equal(4.0, old.Ingredients.Single().Amount);
        Assert.Equal(3.0, old.Results.Single().Amount);

        var normal = recipes["legacy-normal"];
        Assert.Equal(9.0, normal.Ingredients.Single().Amount);
        Assert.Equal("my-item", normal.Results.Single().Name);
    }

    [Fact]
    public void Inspect_ReportsTouchedPrototypesAndSkipsNothingSilently()
    {
        using var mod = ModWithContent();
        mod.Write("data-updates.lua", "data:extend({ build_something() })\n");
        var overview = ModInspector.Inspect(mod.Path);

        Assert.Contains(overview.Touched, t => t.Type == "recipe" && t.Name == "iron-plate" && t.File == "data.lua");
        Assert.Equal(1, overview.DynamicEntries);
        Assert.Contains(overview.Notes, n => n.Contains("could not be resolved statically"));
    }

    [Fact]
    public void Inspect_IncludesValidationIssues_WhenRequested()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "icon = \"__my-mod__/missing.png\"\n");

        Assert.Contains(ModInspector.Inspect(mod.Path).Issues, i => i.Code == "FMS030");
        Assert.Empty(ModInspector.Inspect(mod.Path, validate: false).Issues);
    }

    [Fact]
    public void Inspect_WorksWithoutInfoJson()
    {
        using var dir = new TempDir();
        dir.Write("data.lua", "data:extend({ { type = \"item\", name = \"x\" } })");
        var overview = ModInspector.Inspect(dir.Path);
        Assert.Null(overview.Info);
        Assert.Single(overview.Prototypes);
    }

    [Fact]
    public void Inspect_ExposesSummaryFromInfoJson()
    {
        using var mod = Fixtures.ValidMod();
        var info = ModInspector.Inspect(mod.Path).Info!;
        Assert.Equal("my-mod", info.Name);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal(new[] { "base >= 2.0.0" }, info.Dependencies);
    }

    // --- locale ------------------------------------------------------------

    [Fact]
    public void Locale_ParsesSectionsKeysAndComments()
    {
        var locale = new LocaleData();
        locale.Merge(new[] { "; comment", "[item-name]", "a=Alpha", "b = Beta = still beta", "", "[other]", "x=y", "=broken", "orphan" });

        Assert.Equal("Alpha", locale.Find("item-name", "a"));
        Assert.Equal("Beta = still beta", locale.Find("item-name", "b"));
        Assert.Equal("y", locale.Find("other", "x"));
        Assert.Null(locale.Find("item-name", "missing"));
        Assert.Equal(3, locale.KeyCount);
    }

    // --- zip extraction ----------------------------------------------------

    [Fact]
    public void Extractor_StripsTopFolderAndReusesCache()
    {
        using var installed = new TempDir();
        using var cache = new TempDir();
        Fixtures.WriteModZip(installed.Path, "zipped", "1.0.0", "base");
        var zip = Path.Combine(installed.Path, "zipped_1.0.0.zip");

        var first = ModExtractor.ExtractToCache(zip, cache.Path);
        Assert.True(File.Exists(Path.Combine(first, "info.json")));
        Assert.True(File.Exists(Path.Combine(first, "data.lua")));

        File.WriteAllText(Path.Combine(first, "marker.txt"), "still here");
        var second = ModExtractor.ExtractToCache(zip, cache.Path);
        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(second, "marker.txt"))); // not extracted again
    }

    [Fact]
    public void Extractor_IgnoresEntriesThatEscapeTheTargetFolder()
    {
        using var installed = new TempDir();
        using var cache = new TempDir();
        var zipPath = Path.Combine(installed.Path, "evil_1.0.0.zip");

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "evil_1.0.0/info.json", "evil_1.0.0/../../escaped.txt", "evil_1.0.0/ok.lua" })
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
                    writer.Write(name.EndsWith("info.json") ? "{}" : "x");
        }

        var target = ModExtractor.ExtractToCache(zipPath, cache.Path);

        Assert.True(File.Exists(Path.Combine(target, "ok.lua")));
        Assert.False(File.Exists(Path.Combine(cache.Path, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(cache.Path)!, "escaped.txt")));
    }

    [Fact]
    public void Extractor_RejectsArchivesThatAreNotMods()
    {
        using var dir = new TempDir();
        using var cache = new TempDir();
        var zipPath = Path.Combine(dir.Path, "notamod.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open())) writer.Write("hi");

        Assert.Throws<FmsException>(() => ModExtractor.ExtractToCache(zipPath, cache.Path));
    }
}
