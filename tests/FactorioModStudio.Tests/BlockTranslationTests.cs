using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>A new building block's names go into every language of the mod, marked as untranslated until they are translated.</summary>
public class BlockTranslationTests
{
    private static readonly string Marked = LocaleEditor.UntranslatedLine + "\n";

    private static TempDir Mod()
    {
        var mod = new TempDir();
        mod.Write("info.json", """{ "name": "my-mod", "version": "0.1.0", "title": "My mod", "factorio_version": "2.1" }""");
        mod.Write("data.lua", "-- data\n");
        mod.Write("locale/en/my-mod.cfg", "[mod-name]\nmy-mod=My mod\n");
        mod.Write("locale/ru/my-mod.cfg", "[mod-name]\nmy-mod=Мой мод\n");
        return mod;
    }

    private static string Read(TempDir dir, string path) => File.ReadAllText(dir.Combine(path.Split('/')));

    [Fact]
    public void Apply_WritesTheNamesIntoEveryLanguage_MarkedAsUntranslatedOutsideEnglish()
    {
        using var mod = Mod();
        var plan = ModContent.Apply(mod.Path, new ContentRequest("item", "thing", "Thing"));

        Assert.Contains("locale/ru/my-mod.cfg", plan.Change);
        var en = Read(mod, "locale/en/my-mod.cfg");
        var ru = Read(mod, "locale/ru/my-mod.cfg");
        Assert.Contains("thing=Thing", en);
        Assert.DoesNotContain(LocaleEditor.UntranslatedMark, en);
        Assert.Contains(Marked + "thing=Thing\n", ru);
        Assert.StartsWith("[mod-name]\nmy-mod=Мой мод\n", ru); // what was there stays
    }

    [Fact]
    public void Apply_KeepsATranslationThatIsAlreadyThere()
    {
        using var mod = Mod();
        mod.Write("locale/ru/my-mod.cfg", "[item-name]\nthing=Штука\n");
        ModContent.Apply(mod.Path, new ContentRequest("item", "thing", "Thing"));

        var ru = Read(mod, "locale/ru/my-mod.cfg");
        Assert.Contains("thing=Штука", ru);
        Assert.DoesNotContain("thing=Thing", ru);
        Assert.DoesNotContain($"{Marked}thing=", ru);
    }

    [Fact]
    public void Apply_CreatesTheFileInALanguageFolderWithoutOne()
    {
        using var mod = Mod();
        Directory.CreateDirectory(mod.Combine("locale", "de"));
        var plan = ModContent.Apply(mod.Path, new ContentRequest("item", "thing", "Thing"));

        Assert.Contains("locale/de/my-mod.cfg", plan.Create);
        Assert.Contains(Marked + "thing=Thing", Read(mod, "locale/de/my-mod.cfg"));
    }

    [Fact]
    public void Localization_ListsMarkedKeysAsUntranslated_AndSavingATranslationRemovesTheMark()
    {
        using var mod = Mod();
        ModContent.Apply(mod.Path, new ContentRequest("item", "thing", "Thing"));

        var table = LocaleEditor.Build(mod.Path, "ru");
        var row = table.Rows.Single(r => r.Key == "thing" && r.Section == "item-name");
        Assert.Equal(LocaleRowStatus.Untranslated, row.Status);
        Assert.True(row.Marked);
        Assert.Null(row.Target);
        Assert.False(row.Identical);
        Assert.Equal("locale/ru/my-mod.cfg", row.TargetFile);
        Assert.True(table.Summary.Untranslated >= 1);

        var result = LocaleEditor.Apply(mod.Path, "ru", new[] { new LocaleChange("item-name", "thing", "Штука") }, table.Revision);
        Assert.Equal(1, result.Updated);
        var ru = Read(mod, "locale/ru/my-mod.cfg");
        Assert.Contains("thing=Штука", ru);
        Assert.DoesNotContain(LocaleEditor.UntranslatedMark, ru);
        var again = LocaleEditor.Build(mod.Path, "ru").Rows.Single(r => r.Key == "thing" && r.Section == "item-name");
        Assert.Equal(LocaleRowStatus.Translated, again.Status);
        Assert.False(again.Marked);
    }

    [Fact]
    public void Localization_SavingTheEnglishTextOnPurpose_StillRemovesTheMark()
    {
        using var mod = Mod();
        ModContent.Apply(mod.Path, new ContentRequest("item", "thing", "Thing"));
        var table = LocaleEditor.Build(mod.Path, "ru");

        var result = LocaleEditor.Apply(mod.Path, "ru", new[] { new LocaleChange("item-name", "thing", "Thing") }, table.Revision);

        Assert.Equal(1, result.Updated);
        Assert.DoesNotContain(LocaleEditor.UntranslatedMark, Read(mod, "locale/ru/my-mod.cfg"));
        Assert.True(LocaleEditor.Build(mod.Path, "ru").Rows.Single(r => r.Key == "thing" && r.Section == "item-name").Identical);
    }

    [Fact]
    public void RemovingTheBlock_TakesTheMarkWithTheKey_AndRenamingKeepsIt()
    {
        using var mod = Mod();
        ModContent.Apply(mod.Path, new ContentRequest("item", "thing", "Thing"));

        BlockOperations.Apply(mod.Path, BlockOperations.PlanRename(mod.Path, "prototypes/thing.lua", "gadget", migration: false));
        Assert.Contains(Marked + "gadget=Thing", Read(mod, "locale/ru/my-mod.cfg"));

        BlockOperations.Apply(mod.Path, BlockOperations.PlanRemove(mod.Path, "prototypes/gadget.lua"));
        var ru = Read(mod, "locale/ru/my-mod.cfg");
        Assert.DoesNotContain("gadget", ru);
        Assert.DoesNotContain(LocaleEditor.UntranslatedMark, ru);
        Assert.Contains("my-mod=Мой мод", ru);
    }
}
