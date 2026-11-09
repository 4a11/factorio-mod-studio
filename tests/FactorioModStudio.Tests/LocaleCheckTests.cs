using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactorioModStudio.Cli;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Black-box tests for the FMS040-FMS048 localisation checks, written from the contract only.</summary>
public class LocaleCheckTests
{
    // ---------------------------------------------------------------- helpers

    private static ValidationResult Validate(TempDir mod) => ModValidator.Validate(mod.Path);

    private static List<ValidationIssue> Of(ValidationResult r, string code) => r.Issues.Where(i => i.Code == code).ToList();

    private static List<ValidationIssue> Locale(ValidationResult r) => r.Issues.Where(i => i.Code.StartsWith("FMS04") && i.Code.Length == 6 && i.Code[5] >= '0' && i.Code[5] <= '8').ToList();

    private static string Codes(ValidationResult r) => string.Join(", ", Locale(r).Select(i => $"{i.Code}@{i.File}:{i.Line} {i.Message}"));

    private static void AssertNone(ValidationResult r, string code) =>
        Assert.True(Of(r, code).Count == 0, $"Unexpected {code}: " + string.Join(" | ", Of(r, code).Select(i => $"{i.File}:{i.Line} {i.Message}")));

    private static void AssertNoLocaleIssues(ValidationResult r) =>
        Assert.True(Locale(r).Count == 0, "Unexpected locale issues: " + Codes(r));

    private static TempDir Mod(params (string Path, string Content)[] files)
    {
        var mod = Fixtures.ValidMod();
        foreach (var (path, content) in files) mod.Write(path, content);
        return mod;
    }

    private static string Proto(string type, string name, string extra = "") =>
        $"data:extend({{\n  {{ type = \"{type}\", name = \"{name}\"{extra} }},\n}})\n";

    private static ModOverview Inspect(TempDir mod) => ModInspector.Inspect(mod.Path);

    private static JsonElement CoverageJson(TempDir mod)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(Inspect(mod), options));
        return doc.RootElement.GetProperty("localeCoverage").Clone();
    }

    private static string Repo()
    {
        var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("FMS_REPO_ROOT") is { Length: > 0 } root ? root : AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "examples", "hello-mod"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("examples/hello-mod not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ---------------------------------------------------------------- FMS040

    [Fact]
    public void Fms040_KeyBeforeAnySection_IsAnErrorWithFileAndLine()
    {
        using var mod = Mod(("locale/en/a.cfg", "; header comment\n\nstray=value\n[item-name]\nx=X\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS040"));
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Contains("a.cfg", issue.File);
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public void Fms040_LineWithoutEquals_IsAnError()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nx=X\njust some words\ny=Y\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS040"));
        Assert.Contains("a.cfg", issue.File);
        Assert.Equal(3, issue.Line);
    }

    [Theory]
    [InlineData("[item-name")]
    [InlineData("[]")]
    [InlineData("[item-name\n")]
    public void Fms040_BrokenSectionHeader_IsAnError(string header)
    {
        using var mod = Mod(("locale/en/a.cfg", "[entity-name]\nz=Z\n" + header + "\nx=X\n"));
        var issues = Of(Validate(mod), "FMS040");
        Assert.True(issues.Count >= 1, "expected FMS040 for header " + header);
        Assert.True(issues.Any(i => i.Line == 3), "expected an issue on line 3: " + string.Join(",", issues.Select(i => i.Line)));
    }

    [Fact]
    public void Fms040_EmptyKey_IsAnError()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\n=value\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS040"));
        Assert.Equal(2, issue.Line);
    }

    [Fact]
    public void Fms040_ReportsEveryBadLineSeparately_WithRightFile()
    {
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\nok=1\nbad one\n"),
            ("locale/en/b.cfg", "[item-name]\nok2=1\n\nbad two\nbad three\n"));
        var issues = Of(Validate(mod), "FMS040");
        Assert.Equal(3, issues.Count);
        Assert.Contains(issues, i => i.File!.Contains("a.cfg") && i.Line == 3);
        Assert.Contains(issues, i => i.File!.Contains("b.cfg") && i.Line == 4);
        Assert.Contains(issues, i => i.File!.Contains("b.cfg") && i.Line == 5);
    }

    [Fact]
    public void Fms040_NonDefaultLanguage_IsAlsoChecked()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nx=X\n"), ("locale/de/a.cfg", "[item-name]\nx=X\nkaputt\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS040"));
        Assert.Contains("de", issue.File!.Replace('\\', '/'));
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public void Fms040_Negative_CommentsBlankLinesAndEqualsInValuesAreFine()
    {
        var cfg = "; semicolon comment\n# hash comment\n\n[item-name]\n; another\nx=a=b=c\ny=\nz=http://x?a=1&b=2\n# trailing\n\n[entity-name]\nq=Q\n";
        using var mod = Mod(("locale/en/a.cfg", cfg));
        AssertNone(Validate(mod), "FMS040");
    }

    [Fact]
    public void Fms040_Negative_BomAndCrlfFile_IsClean_AndLineNumbersSurviveCrlf()
    {
        var clean = "﻿[item-name]\r\nx=X\r\n; c\r\ny=Y\r\n";
        using var mod = Mod(("locale/en/a.cfg", ""));
        File.WriteAllBytes(mod.Combine("locale", "en", "a.cfg"), new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(clean.TrimStart('﻿'))).ToArray());
        AssertNone(Validate(mod), "FMS040");

        var bad = "[item-name]\r\nx=X\r\n\r\nbroken line\r\n";
        File.WriteAllText(mod.Combine("locale", "en", "a.cfg"), bad);
        Assert.Equal(4, Assert.Single(Of(Validate(mod), "FMS040")).Line);
    }

    [Fact]
    public void Fms040_Negative_NonCfgFilesInLocaleAreIgnored()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nx=X\n"), ("locale/en/readme.txt", "this is not a cfg\nat all\n"));
        AssertNone(Validate(mod), "FMS040");
    }

    // ---------------------------------------------------------------- FMS041

    [Fact]
    public void Fms041_DuplicateInSameFile_IsReportedAtLaterOccurrence_AndNamesFirst()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nfoo=One\nbar=B\nfoo=Two\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS041"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Equal(4, issue.Line);
        Assert.Contains("a.cfg", issue.File);
        Assert.Contains("foo", issue.Message);
        Assert.Contains("2", issue.Message); // first occurrence line mentioned
    }

    [Fact]
    public void Fms041_DuplicateAcrossFilesOfSameLanguage_IsReportedInLaterFile()
    {
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\nfoo=One\n"),
            ("locale/en/b.cfg", "\n\n[item-name]\nfoo=Two\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS041"));
        Assert.Contains("b.cfg", issue.File);
        Assert.Equal(4, issue.Line);
        Assert.Contains("a.cfg", issue.Message);
    }

    [Fact]
    public void Fms041_DuplicateInNonDefaultLanguage_IsReported()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nfoo=One\n"), ("locale/de/a.cfg", "[item-name]\nfoo=Eins\nfoo=Zwei\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS041"));
        Assert.Contains("de", issue.File!.Replace('\\', '/'));
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public void Fms041_SectionReopenedLaterInSameFile_StillCountsAsSameSection()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nfoo=One\n[entity-name]\nx=X\n[item-name]\nfoo=Two\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS041"));
        Assert.Equal(6, issue.Line);
    }

    [Fact]
    public void Fms041_Negative_SameKeyInDifferentSections()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nfoo=One\n[entity-name]\nfoo=Two\n"), ("locale/en/b.cfg", "[fluid-name]\nfoo=Three\n"));
        AssertNone(Validate(mod), "FMS041");
    }

    [Fact]
    public void Fms041_Negative_SameKeyInDifferentLanguages()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nfoo=One\n"), ("locale/de/a.cfg", "[item-name]\nfoo=Eins\n"), ("locale/fr/a.cfg", "[item-name]\nfoo=Un\n"));
        AssertNone(Validate(mod), "FMS041");
    }

    [Fact]
    public void Fms041_Negative_SameFileNameInDifferentLanguageFolders()
    {
        using var mod = Mod(("locale/en/main.cfg", "[item-name]\nfoo=One\nbar=2\n"), ("locale/de/main.cfg", "[item-name]\nfoo=Eins\nbar=2\n"));
        AssertNone(Validate(mod), "FMS041");
    }

    [Fact]
    public void Fms041_Negative_KeysThatMerelyShareAPrefixOrCaseDiffer()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nfoo=1\nfoo2=2\nfoo-2=3\nFoo=4\n"));
        AssertNone(Validate(mod), "FMS041");
    }

    [Fact]
    public void Fms041_Negative_CommentedOutDuplicate()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nfoo=1\n;foo=2\n#foo=3\n"));
        AssertNone(Validate(mod), "FMS041");
    }

    [Fact]
    public void Fms041_ThreeOccurrences_ReportsAtLeastOneAndNeverTheFirst()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nfoo=1\nfoo=2\nfoo=3\n"));
        var issues = Of(Validate(mod), "FMS041");
        Assert.True(issues.Count >= 1);
        Assert.DoesNotContain(issues, i => i.Line == 2);
    }

    // ---------------------------------------------------------------- FMS042

    public static readonly string[] ItemLike =
    {
        "item", "ammo", "capsule", "gun", "module", "tool", "armor", "repair-tool", "rail-planner", "item-with-entity-data",
        "item-with-label", "item-with-inventory", "blueprint", "blueprint-book", "deconstruction-item", "upgrade-item",
        "copy-paste-tool", "selection-tool", "spidertron-remote",
    };

    [Theory]
    [InlineData("item", "item-name")]
    [InlineData("ammo", "item-name")]
    [InlineData("capsule", "item-name")]
    [InlineData("gun", "item-name")]
    [InlineData("module", "item-name")]
    [InlineData("tool", "item-name")]
    [InlineData("armor", "item-name")]
    [InlineData("repair-tool", "item-name")]
    [InlineData("rail-planner", "item-name")]
    [InlineData("item-with-entity-data", "item-name")]
    [InlineData("item-with-label", "item-name")]
    [InlineData("item-with-inventory", "item-name")]
    [InlineData("blueprint", "item-name")]
    [InlineData("blueprint-book", "item-name")]
    [InlineData("deconstruction-item", "item-name")]
    [InlineData("upgrade-item", "item-name")]
    [InlineData("copy-paste-tool", "item-name")]
    [InlineData("selection-tool", "item-name")]
    [InlineData("spidertron-remote", "item-name")]
    [InlineData("fluid", "fluid-name")]
    [InlineData("technology", "technology-name")]
    [InlineData("tile", "tile-name")]
    [InlineData("virtual-signal", "virtual-signal-name")]
    [InlineData("item-group", "item-group-name")]
    [InlineData("solar-panel-equipment", "equipment-name")]
    [InlineData("battery-equipment", "equipment-name")]
    [InlineData("night-vision-equipment", "equipment-name")]
    public void Fms042_MissingName_IsWarned_WithTypeNameAndExpectedKey(string type, string section)
    {
        using var mod = Mod(("data.lua", "-- c\n-- c\n" + Proto(type, "zed-thing")), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS042"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("data.lua", issue.File);
        Assert.Equal(4, issue.Line);
        Assert.Contains(type, issue.Message);
        Assert.Contains("zed-thing", issue.Message);
        Assert.Contains($"{section}.zed-thing", issue.Message);
    }

    [Theory]
    [InlineData("item", "item-name")]
    [InlineData("ammo", "item-name")]
    [InlineData("capsule", "item-name")]
    [InlineData("gun", "item-name")]
    [InlineData("module", "item-name")]
    [InlineData("tool", "item-name")]
    [InlineData("armor", "item-name")]
    [InlineData("repair-tool", "item-name")]
    [InlineData("rail-planner", "item-name")]
    [InlineData("item-with-entity-data", "item-name")]
    [InlineData("item-with-label", "item-name")]
    [InlineData("item-with-inventory", "item-name")]
    [InlineData("blueprint", "item-name")]
    [InlineData("blueprint-book", "item-name")]
    [InlineData("deconstruction-item", "item-name")]
    [InlineData("upgrade-item", "item-name")]
    [InlineData("copy-paste-tool", "item-name")]
    [InlineData("selection-tool", "item-name")]
    [InlineData("spidertron-remote", "item-name")]
    [InlineData("fluid", "fluid-name")]
    [InlineData("technology", "technology-name")]
    [InlineData("tile", "tile-name")]
    [InlineData("virtual-signal", "virtual-signal-name")]
    [InlineData("item-group", "item-group-name")]
    [InlineData("solar-panel-equipment", "equipment-name")]
    [InlineData("battery-equipment", "equipment-name")]
    public void Fms042_Negative_NameProvidedInDefaultLanguage(string type, string section)
    {
        using var mod = Mod(("data.lua", Proto(type, "zed-thing")), ("locale/en/a.cfg", $"[{section}]\nzed-thing=Zed\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Theory]
    [InlineData("item", "fluid-name")]
    [InlineData("fluid", "item-name")]
    [InlineData("technology", "recipe-name")]
    [InlineData("tile", "entity-name")]
    [InlineData("item", "entity-name")] // item types have NO entity fallback
    public void Fms042_NameInWrongSection_DoesNotCount(string type, string wrongSection)
    {
        using var mod = Mod(("data.lua", Proto(type, "zed-thing")), ("locale/en/a.cfg", $"[{wrongSection}]\nzed-thing=Zed\n"));
        Assert.Single(Of(Validate(mod), "FMS042"));
    }

    [Theory]
    [InlineData("assembling-machine")]
    [InlineData("furnace")]
    [InlineData("container")]
    [InlineData("transport-belt")]
    [InlineData("inserter")]
    [InlineData("lab")]
    [InlineData("boiler")]
    [InlineData("generator")]
    [InlineData("radar")]
    public void Fms042_Entity_MissingNames_ExpectsEntityNameKey(string type)
    {
        using var mod = Mod(("data.lua", Proto(type, "zed-machine")), ("locale/en/a.cfg", "[item-name]\nother=O\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS042"));
        Assert.Contains("entity-name.zed-machine", issue.Message);
        Assert.Contains(type, issue.Message);
    }

    [Theory]
    [InlineData("assembling-machine", "entity-name")]
    [InlineData("assembling-machine", "item-name")]
    [InlineData("furnace", "item-name")]
    [InlineData("container", "item-name")]
    [InlineData("transport-belt", "entity-name")]
    [InlineData("inserter", "item-name")]
    [InlineData("lab", "entity-name")]
    [InlineData("boiler", "item-name")]
    [InlineData("generator", "item-name")]
    [InlineData("radar", "entity-name")]
    public void Fms042_Entity_FallsBackToPlacingItemName(string type, string section)
    {
        using var mod = Mod(("data.lua", Proto(type, "zed-machine")), ("locale/en/a.cfg", $"[{section}]\nzed-machine=Z\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Fact]
    public void Fms042_Negative_RecipeAndItemSubgroupAreNotChecked()
    {
        using var mod = Mod(
            ("data.lua", "data:extend({\n  { type = \"recipe\", name = \"r1\", ingredients = {}, results = {} },\n  { type = \"item-subgroup\", name = \"sg1\", group = \"g\", order = \"a\" },\n})\n"),
            ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Fact]
    public void Fms042_Negative_UnknownPrototypeTypesAreSkipped()
    {
        using var mod = Mod(
            ("data.lua", Proto("my-weird-custom-type", "w1") + Proto("sound", "s1") + Proto("noise-layer", "n1")),
            ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Fact]
    public void Fms042_Negative_LocalisedNameSet_SkipsThePrototype()
    {
        var lua = "data:extend({\n"
                + "  { type = \"item\", name = \"a1\", localised_name = {\"mod-name.x\"}, stack_size = 1 },\n"
                + "  { type = \"fluid\", name = \"a2\", localised_name = \"Plain\" },\n"
                + "  {\n    type = \"assembling-machine\",\n    name = \"a3\",\n    localised_name = {\"\", \"x\"},\n  },\n"
                + "})\n";
        using var mod = Mod(("data.lua", lua), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Fact]
    public void Fms042_OnlyThePrototypeWithoutLocalisedName_IsReportedInMixedExtend()
    {
        var lua = "data:extend({\n"
                + "  { type = \"item\", name = \"has-ln\", localised_name = \"Plain\", stack_size = 1 },\n"
                + "  { type = \"item\", name = \"no-ln\", stack_size = 1 },\n"
                + "})\n";
        using var mod = Mod(("data.lua", lua), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS042"));
        Assert.Contains("no-ln", issue.Message);
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public void Fms042_NameDefinedInAnotherEnFile_Counts()
    {
        using var mod = Mod(("data.lua", Proto("item", "zed")), ("locale/en/a.cfg", "[mod-name]\nx=X\n"), ("locale/en/b.cfg", "[item-name]\nzed=Zed\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Fact]
    public void Fms042_NameOnlyInOtherLanguage_StillWarns()
    {
        using var mod = Mod(("data.lua", Proto("item", "zed")), ("locale/en/a.cfg", "[mod-name]\nx=X\n"), ("locale/de/a.cfg", "[item-name]\nzed=Zed\n"));
        Assert.Single(Of(Validate(mod), "FMS042"));
    }

    [Fact]
    public void Fms042_Negative_ModifyingExistingPrototypesViaDataRaw()
    {
        var lua = "data.raw.item[\"iron-plate\"].stack_size = 200\ndata.raw[\"assembling-machine\"][\"assembling-machine-1\"].crafting_speed = 2\n";
        using var mod = Mod(("data.lua", lua), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Fact]
    public void Fms042_Negative_DynamicNameIsNotGuessed()
    {
        var lua = "data:extend({\n  { type = \"item\", name = make_name(\"x\"), stack_size = 1 },\n})\n";
        using var mod = Mod(("data.lua", lua), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Fact]
    public void Fms042_NameBuiltFromLocalStrings_IsChecked()
    {
        // "p-" .. "x" is known without running anything (the scanner follows local strings), so the missing key is reported.
        var lua = "local prefix = \"p-\"\ndata:extend({\n  { type = \"item\", name = prefix .. \"x\", stack_size = 1 },\n})\n";
        using var mod = Mod(("data.lua", lua), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        Assert.Contains(Validate(mod).Issues, i => i.Code == "FMS042" && i.Message.Contains("\"p-x\""));

        using var named = Mod(("data.lua", lua), ("locale/en/a.cfg", "[item-name]\np-x=X\n"));
        AssertNone(Validate(named), "FMS042");
    }

    [Fact]
    public void Fms042_Negative_PrototypeInsideCommentOrString()
    {
        var lua = "-- data:extend({ { type = \"item\", name = \"ghost\" } })\n--[[ { type = \"item\", name = \"ghost2\" } ]]\nlocal s = '{ type = \"item\", name = \"ghost3\" }'\n";
        using var mod = Mod(("data.lua", lua), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        AssertNone(Validate(mod), "FMS042");
    }

    [Fact]
    public void Fms042_MultipleMissing_OneIssueEachWithOwnLine()
    {
        var lua = "data:extend({\n  { type = \"item\", name = \"one\" },\n  { type = \"fluid\", name = \"two\" },\n  { type = \"technology\", name = \"three\" },\n})\n";
        using var mod = Mod(("data.lua", lua), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        var issues = Of(Validate(mod), "FMS042").OrderBy(i => i.Line).ToList();
        Assert.Equal(3, issues.Count);
        Assert.Equal(new[] { 2, 3, 4 }, issues.Select(i => i.Line!.Value).ToArray());
    }

    // ---------------------------------------------------------------- FMS043

    private const string MyLocale = "[my-section]\npresent=Here\nother=Other\n";

    [Fact]
    public void Fms043_MissingKeyInOwnSection_IsWarned_WithFileAndLine()
    {
        using var mod = Mod(("locale/en/a.cfg", MyLocale), ("control.lua", "local x = 1\nplayer.print({\"my-section.missing\"})\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS043"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("control.lua", issue.File);
        Assert.Equal(2, issue.Line);
        Assert.Contains("my-section.missing", issue.Message);
    }

    [Theory]
    [InlineData("{\"my-section.missing\", 3}")]
    [InlineData("{ \"my-section.missing\" , 3, 4 }")]
    [InlineData("{\"my-section.missing\",\n  1}")]
    [InlineData("{\"\", {\"my-section.missing\"}}")]
    [InlineData("{'my-section.missing'}")]
    public void Fms043_VariousShapesOfLocalisedStrings_AreDetected(string expr)
    {
        using var mod = Mod(("locale/en/a.cfg", MyLocale), ("control.lua", $"local s = {expr}\n"));
        Assert.Single(Of(Validate(mod), "FMS043"));
    }

    [Theory]
    [InlineData("{\"my-section.present\"}")]
    [InlineData("{\"my-section.present\", 1}")]
    [InlineData("{ \"my-section.other\" }")]
    [InlineData("{\"?\", {\"my-section.present\"}, \"fallback\"}")]
    [InlineData("{\"base-section.whatever\"}")]
    [InlineData("{\"entity-name.iron-chest\"}")]
    [InlineData("{\"other-mod-section.x\"}")]
    [InlineData("{\"\", \"plain\"}")]
    [InlineData("{\"no dot here\"}")]
    [InlineData("{1, \"my-section.missing\"}")]
    [InlineData("{\"my-section.present\" .. \"\"}")]
    public void Fms043_Negative(string expr)
    {
        using var mod = Mod(("locale/en/a.cfg", MyLocale), ("control.lua", $"local s = {expr}\n"));
        AssertNone(Validate(mod), "FMS043");
    }

    [Theory]
    [InlineData("-- player.print({\"my-section.missing\"})")]
    [InlineData("--[[ player.print({\"my-section.missing\"}) ]]")]
    [InlineData("--[==[\n{\"my-section.missing\"}\n]==]")]
    [InlineData("local s = \"{\\\"my-section.missing\\\"}\"")]
    [InlineData("local s = '{\"my-section.missing\"}'")]
    [InlineData("local s = [[ {\"my-section.missing\"} ]]")]
    [InlineData("local s = \"a\" -- {\"my-section.missing\"}")]
    public void Fms043_Negative_CommentsAndOrdinaryStrings(string source)
    {
        using var mod = Mod(("locale/en/a.cfg", MyLocale), ("control.lua", source + "\n"));
        AssertNone(Validate(mod), "FMS043");
    }

    [Fact]
    public void Fms043_SectionDefinedOnlyInOtherLanguage_IsNotOwnDefaultSection()
    {
        using var mod = Mod(("locale/en/a.cfg", "[mod-name]\nx=X\n"), ("locale/de/a.cfg", "[de-only]\nk=V\n"), ("control.lua", "local s = {\"de-only.k2\"}\n"));
        AssertNone(Validate(mod), "FMS043");
    }

    [Fact]
    public void Fms043_KeyDefinedInSectionOfAnotherEnFile_IsFound()
    {
        using var mod = Mod(("locale/en/a.cfg", "[my-section]\na=1\n"), ("locale/en/b.cfg", "[my-section]\nb=2\n"), ("control.lua", "local s = {\"my-section.b\"}\n"));
        AssertNone(Validate(mod), "FMS043");
    }

    [Fact]
    public void Fms043_FoundInDataAndSubfolderLuaFiles()
    {
        using var mod = Mod(("locale/en/a.cfg", MyLocale), ("scripts/gui/win.lua", "return {\"my-section.nope\"}\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS043"));
        Assert.Contains("win.lua", issue.File);
    }

    [Fact]
    public void Fms043_EachMissingReferenceIsReportedAtItsOwnLine()
    {
        using var mod = Mod(("locale/en/a.cfg", MyLocale), ("control.lua", "local a = {\"my-section.m1\"}\nlocal b = {\"my-section.present\"}\nlocal c = {\"my-section.m2\"}\n"));
        var issues = Of(Validate(mod), "FMS043").OrderBy(i => i.Line).ToList();
        Assert.Equal(new[] { 1, 3 }, issues.Select(i => i.Line!.Value).ToArray());
    }

    [Fact]
    public void Fms043_NoLocaleAtAll_NoReports()
    {
        using var mod = Mod(("control.lua", "local s = {\"my-section.missing\"}\n"));
        AssertNone(Validate(mod), "FMS043");
    }

    // ---------------------------------------------------------------- FMS044 / FMS045

    [Fact]
    public void Fms044_MissingKeysInOtherLanguage_OneIssuePerLanguage_WithCountAndExamples()
    {
        var en = new StringBuilder("[item-name]\n");
        for (var i = 1; i <= 7; i++) en.Append($"k{i}=V{i}\n");
        using var mod = Mod(("locale/en/a.cfg", en.ToString()), ("locale/de/a.cfg", "[item-name]\nk1=E1\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS044"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("de", issue.Message);
        Assert.Contains("6", issue.Message);
        var examples = Regex.Matches(issue.Message, @"item-name\.k\d").Count;
        Assert.True(examples >= 1 && examples <= 5, $"examples shown: {examples} in: {issue.Message}");
        Assert.DoesNotContain("item-name.k1", issue.Message); // present in de, not an example of a MISSING key
    }

    [Fact]
    public void Fms044_TwoLanguages_TwoIssues()
    {
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\na=1\nb=2\n"),
            ("locale/de/a.cfg", "[item-name]\na=1\n"),
            ("locale/fr/a.cfg", "[item-name]\nb=2\n"));
        var issues = Of(Validate(mod), "FMS044");
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("de"));
        Assert.Contains(issues, i => i.Message.Contains("fr"));
    }

    [Fact]
    public void Fms044_Negative_FullCoverage_AndAcrossMultipleFiles()
    {
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\na=1\n"), ("locale/en/b.cfg", "[entity-name]\nb=2\n"),
            ("locale/de/all.cfg", "[item-name]\na=1\n[entity-name]\nb=2\n"));
        var r = Validate(mod);
        AssertNone(r, "FMS044");
        AssertNone(r, "FMS045");
    }

    [Fact]
    public void Fms044_SameKeyNameInDifferentSection_DoesNotCover()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=1\n"), ("locale/de/a.cfg", "[entity-name]\na=1\n"));
        var r = Validate(mod);
        Assert.Single(Of(r, "FMS044"));
        Assert.Single(Of(r, "FMS045"));
    }

    [Fact]
    public void Fms045_ExtraKeysInOtherLanguage_IsInfoAndOnePerLanguage()
    {
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\na=1\n"),
            ("locale/de/a.cfg", "[item-name]\na=1\nx=2\ny=3\n"),
            ("locale/fr/a.cfg", "[item-name]\na=1\nz=3\n"));
        var r = Validate(mod);
        var issues = Of(r, "FMS045");
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(Severity.Info, i.Severity));
        Assert.Contains(issues, i => i.Message.Contains("de") && i.Message.Contains("2"));
        AssertNone(r, "FMS044");
    }

    [Fact]
    public void Fms044_And045_BothReportedWhenBothApply()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=1\nb=2\n"), ("locale/de/a.cfg", "[item-name]\na=1\nc=3\n"));
        var r = Validate(mod);
        Assert.Single(Of(r, "FMS044"));
        Assert.Single(Of(r, "FMS045"));
    }

    [Fact]
    public void Fms044_Negative_DefaultLanguageNeverComparedToItself_AndNoOtherLanguages()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=1\nb=2\n"));
        var r = Validate(mod);
        AssertNone(r, "FMS044");
        AssertNone(r, "FMS045");
        AssertNone(r, "FMS048");
    }

    [Fact]
    public void Fms044_DuplicateKeysAreCountedOnce()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=1\na=2\nb=3\n"), ("locale/de/a.cfg", "[item-name]\na=1\nb=1\n"));
        AssertNone(Validate(mod), "FMS044");
        AssertNone(Validate(mod), "FMS045");
    }

    [Fact]
    public void Fms044_LanguageDirectoryWithoutCfg_DoesNotCrash()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=1\n"), ("locale/de/readme.txt", "x"));
        var r = Validate(mod);
        AssertNone(r, "FMS048");
    }

    // ---------------------------------------------------------------- FMS046

    [Fact]
    public void Fms046_MissingPlaceholder_IsReportedAtOtherLanguageLine()
    {
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\na=Hello __1__ and __2__\n"),
            ("locale/de/a.cfg", "; c\n[item-name]\na=Hallo __1__\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS046"));
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Contains("de", issue.File!.Replace('\\', '/'));
        Assert.Equal(3, issue.Line);
        Assert.Contains("a", issue.Message);
    }

    [Fact]
    public void Fms046_ExtraPlaceholderInOtherLanguage_IsReported()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=Hello __1__\n"), ("locale/de/a.cfg", "[item-name]\na=Hallo __1__ __3__\n"));
        Assert.Single(Of(Validate(mod), "FMS046"));
    }

    [Fact]
    public void Fms046_PlaceholderAbsentInOtherLanguageEntirely_IsReported()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=Hello __1__\n"), ("locale/de/a.cfg", "[item-name]\na=Hallo\n"));
        Assert.Single(Of(Validate(mod), "FMS046"));
    }

    [Fact]
    public void Fms046_Negative_SameSetInDifferentOrder_IsFine()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=__1__ of __2__\n"), ("locale/de/a.cfg", "[item-name]\na=__2__ von __1__\n"));
        AssertNone(Validate(mod), "FMS046");
    }

    [Fact]
    public void Fms046_Negative_SameSetRepeatedDifferentTimes()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=__1__ and __1__\n"), ("locale/de/a.cfg", "[item-name]\na=__1__\n"));
        AssertNone(Validate(mod), "FMS046");
    }

    [Theory]
    [InlineData("Press __CONTROL__inventory__", "Druecke taste")]
    [InlineData("Press __CONTROL__inventory__", "Druecke __CONTROL__build__")]
    [InlineData("Press __ALT_CONTROL__1__inventory__", "Druecke taste")]
    [InlineData("Press __ALT_CONTROL__1__inventory__", "Druecke __ALT_CONTROL__2__inventory__")]
    [InlineData("A __ENTITY__iron-plate__ b", "A b")]
    [InlineData("A __ENTITY__iron-plate__ b", "A __ENTITY__copper-plate__ b")]
    [InlineData("A __ITEM__iron-plate__ b __1__", "A __ITEM__copper-plate__ b __1__")]
    [InlineData("x __1__ __ENTITY__a__", "x __1__")]
    [InlineData("no placeholders", "also none")]
    [InlineData("ten __10__", "zehn")]
    public void Fms046_Negative_NonNumberedForms_AreIgnored(string en, string de)
    {
        using var mod = Mod(("locale/en/a.cfg", $"[item-name]\na={en}\n"), ("locale/de/a.cfg", $"[item-name]\na={de}\n"));
        AssertNone(Validate(mod), "FMS046");
    }

    [Fact]
    public void Fms046_Negative_KeyMissingInOtherLanguage_IsNot046()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=__1__\n"), ("locale/de/a.cfg", "[item-name]\nb=zzz\n"));
        AssertNone(Validate(mod), "FMS046");
    }

    [Fact]
    public void Fms046_ComparesPerSection_NotJustKeyName()
    {
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\na=__1__\n[entity-name]\na=plain\n"),
            ("locale/de/a.cfg", "[item-name]\na=__1__\n[entity-name]\na=schlicht\n"));
        AssertNone(Validate(mod), "FMS046");
    }

    [Fact]
    public void Fms046_Negative_PlaceholderCheckingNeverAppliesToDefaultLanguageAlone()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\na=__1__ __2__\n"));
        AssertNone(Validate(mod), "FMS046");
    }

    // ---------------------------------------------------------------- FMS047

    [Fact]
    public void Fms047_UnusedKeyInCustomSection_IsInfo_PerKey()
    {
        using var mod = Mod(("locale/en/a.cfg", "[my-gui]\nused=U\nunused1=X\nunused2=Y\n"), ("control.lua", "local s = {\"my-gui.used\"}\n"));
        var issues = Of(Validate(mod), "FMS047");
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(Severity.Info, i.Severity));
        Assert.Contains(issues, i => i.Message.Contains("unused1"));
        Assert.Contains(issues, i => i.Message.Contains("unused2"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("my-gui.used"));
    }

    [Fact]
    public void Fms047_ReportedWithFileAndLineOfTheKey()
    {
        using var mod = Mod(("locale/en/a.cfg", "[my-gui]\n\nlonely=L\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS047"));
        Assert.Contains("a.cfg", issue.File);
        Assert.Equal(3, issue.Line);
    }

    [Theory]
    [InlineData("local k = \"my-gui.k\"")]
    [InlineData("local t = {\"my-gui.k\"}")]
    [InlineData("local t = {\"?\", {\"my-gui.k\"}}")]
    [InlineData("gui.caption = {'my-gui.k', 1}")]
    [InlineData("data:extend({ { type = \"x\", name = \"n\", localised_description = \"my-gui.k\" } })")]
    public void Fms047_Negative_KeyReferencedBySomeLuaStringLiteral(string lua)
    {
        using var mod = Mod(("locale/en/a.cfg", "[my-gui]\nk=K\n"), ("scripts/x.lua", lua + "\n"));
        AssertNone(Validate(mod), "FMS047");
    }

    [Theory]
    [InlineData("item-name")]
    [InlineData("item-description")]
    [InlineData("entity-name")]
    [InlineData("entity-description")]
    [InlineData("fluid-name")]
    [InlineData("fluid-description")]
    [InlineData("recipe-name")]
    [InlineData("recipe-description")]
    [InlineData("technology-name")]
    [InlineData("technology-description")]
    [InlineData("equipment-name")]
    [InlineData("equipment-description")]
    [InlineData("tile-name")]
    [InlineData("virtual-signal-name")]
    [InlineData("item-group-name")]
    [InlineData("item-subgroup-name")]
    [InlineData("achievement-name")]
    [InlineData("achievement-description")]
    [InlineData("mod-name")]
    [InlineData("mod-description")]
    [InlineData("controls")]
    [InlineData("string-mod-setting")]
    [InlineData("mod-setting-name")]
    [InlineData("mod-setting-description")]
    [InlineData("shortcut-name")]
    [InlineData("ammo-category-name")]
    [InlineData("damage-type-name")]
    [InlineData("fuel-category-name")]
    [InlineData("resource-category-name")]
    [InlineData("modifier-description")]
    [InlineData("tips-and-tricks-name")]
    [InlineData("tips-and-tricks-description")]
    [InlineData("decorative-name")]
    [InlineData("autoplace-control-names")]
    [InlineData("map-gen-preset-name")]
    [InlineData("map-gen-preset-description")]
    [InlineData("quality-name")]
    [InlineData("quality-description")]
    [InlineData("planet-name")]
    [InlineData("space-location-name")]
    [InlineData("surface-property-name")]
    public void Fms047_Negative_StandardSectionsAreNeverReportedAsUnused(string section)
    {
        using var mod = Mod(("locale/en/a.cfg", $"[{section}]\nnobody-references-me=V\n"));
        AssertNone(Validate(mod), "FMS047");
    }

    [Fact]
    public void Fms047_MoreThan30Unused_ReportsThirtyPlusOneSummary()
    {
        var cfg = new StringBuilder("[my-gui]\n");
        for (var i = 0; i < 40; i++) cfg.Append($"key{i:00}=V\n");
        using var mod = Mod(("locale/en/a.cfg", cfg.ToString()));
        var issues = Of(Validate(mod), "FMS047");
        Assert.Equal(31, issues.Count);
        Assert.Equal(1, issues.Count(i => !Regex.IsMatch(i.Message, @"key\d\d")));
        Assert.Equal(1, issues.Count(i => Regex.IsMatch(i.Message, @"\b10\b")));
    }

    [Fact]
    public void Fms047_FewUnused_NoSummaryIssue()
    {
        using var mod = Mod(("locale/en/a.cfg", "[my-gui]\na=1\nb=2\nc=3\n"));
        Assert.Equal(3, Of(Validate(mod), "FMS047").Count);
    }

    [Fact]
    public void Fms047_KeyPresentInSeveralLanguages_ReportedOnce()
    {
        using var mod = Mod(("locale/en/a.cfg", "[my-gui]\nlonely=L\n"), ("locale/de/a.cfg", "[my-gui]\nlonely=Lx\n"));
        Assert.Single(Of(Validate(mod), "FMS047"));
    }

    [Fact]
    public void Fms047_Negative_ReferenceToOtherKeyOfSameSectionDoesNotCoverUnused()
    {
        using var mod = Mod(("locale/en/a.cfg", "[my-gui]\nk=K\nk2=K2\n"), ("control.lua", "local s = {\"my-gui.k\"}\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS047"));
        Assert.Contains("k2", issue.Message);
    }

    // ---------------------------------------------------------------- FMS048

    [Fact]
    public void Fms048_OtherLanguagesWithoutEn_IsWarning()
    {
        using var mod = Mod(("locale/de/a.cfg", "[item-name]\na=1\n"));
        var issue = Assert.Single(Of(Validate(mod), "FMS048"));
        Assert.Equal(Severity.Warning, issue.Severity);
    }

    [Fact]
    public void Fms048_EnFolderWithoutCfgFiles_CountsAsMissing()
    {
        using var mod = Mod(("locale/en/readme.txt", "hi"), ("locale/de/a.cfg", "[item-name]\na=1\n"));
        Assert.Single(Of(Validate(mod), "FMS048"));
    }

    [Fact]
    public void Fms048_Negative_EnPresent_OrNoLocaleAtAll()
    {
        using var withEn = Mod(("locale/en/a.cfg", "[item-name]\na=1\n"), ("locale/de/a.cfg", "[item-name]\na=1\n"));
        AssertNone(Validate(withEn), "FMS048");
        using var none = Mod();
        AssertNone(Validate(none), "FMS048");
    }

    [Fact]
    public void Fms048_WithoutDefault_NoPairwiseComparisonsAreMade()
    {
        using var mod = Mod(("locale/de/a.cfg", "[item-name]\na=__1__\nb=2\n"), ("locale/fr/a.cfg", "[item-name]\na=1\nc=3\n"));
        var r = Validate(mod);
        Assert.Single(Of(r, "FMS048"));
        AssertNone(r, "FMS044");
        AssertNone(r, "FMS045");
        AssertNone(r, "FMS046");
    }

    // ---------------------------------------------------------------- coverage (ModOverview)

    [Fact]
    public void Coverage_EnFirstThenAlphabetical_WithKeyMissingExtraNumbers()
    {
        using var mod = Mod(
            ("locale/fr/a.cfg", "[item-name]\nx=1\n"),
            ("locale/en/a.cfg", "[item-name]\nx=1\ny=2\n[entity-name]\nz=3\n"),
            ("locale/de/a.cfg", "[item-name]\nx=1\nw=9\n"),
            ("locale/cs/a.cfg", "[item-name]\nx=1\ny=2\n[entity-name]\nz=3\n"));
        var cov = CoverageJson(mod);
        Assert.Equal(JsonValueKind.Array, cov.ValueKind);
        var rows = cov.EnumerateArray().Select(e => (
            e.GetProperty("language").GetString(), e.GetProperty("keys").GetInt32(),
            e.GetProperty("missing").GetInt32(), e.GetProperty("extra").GetInt32())).ToList();
        Assert.Equal(new[] { "en", "cs", "de", "fr" }, rows.Select(r => r.Item1!).ToArray());
        Assert.Equal(("en", 3, 0, 0), rows[0]);
        Assert.Equal(("cs", 3, 0, 0), rows[1]);
        Assert.Equal(("de", 2, 2, 1), rows[2]);
        Assert.Equal(("fr", 1, 2, 0), rows[3]);
    }

    [Fact]
    public void Coverage_KeysAreDistinctSectionKeyPairs_UnionedAcrossFiles_IgnoringMalformedAndCommentLines()
    {
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\nx=1\nx=dup\n; c=1\ngarbage\n[entity-name]\nx=1\n"),
            ("locale/en/b.cfg", "[item-name]\nx=again\ny=2\n"));
        var row = Assert.Single(CoverageJson(mod).EnumerateArray().ToList());
        Assert.Equal("en", row.GetProperty("language").GetString());
        Assert.Equal(3, row.GetProperty("keys").GetInt32()); // item-name.x, item-name.y, entity-name.x
    }

    [Fact]
    public void Coverage_NoLocale_IsEmptyArray()
    {
        using var mod = Mod();
        var cov = CoverageJson(mod);
        Assert.Equal(JsonValueKind.Array, cov.ValueKind);
        Assert.Equal(0, cov.GetArrayLength());
    }

    [Fact]
    public void Coverage_OnlyDefault_HasZeroMissingAndExtra()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nx=1\n"));
        var row = Assert.Single(CoverageJson(mod).EnumerateArray().ToList());
        Assert.Equal(0, row.GetProperty("missing").GetInt32());
        Assert.Equal(0, row.GetProperty("extra").GetInt32());
    }

    [Fact]
    public void Coverage_WithoutDefault_MissingAndExtraAreZeroForAllLanguages()
    {
        using var mod = Mod(("locale/de/a.cfg", "[item-name]\nx=1\ny=2\n"), ("locale/fr/a.cfg", "[item-name]\nq=1\n"));
        var rows = CoverageJson(mod).EnumerateArray().ToList();
        Assert.Equal(new[] { "de", "fr" }, rows.Select(r => r.GetProperty("language").GetString()!).ToArray());
        Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.GetProperty("keys").GetInt32()).ToArray());
        Assert.All(rows, r => { Assert.Equal(0, r.GetProperty("missing").GetInt32()); Assert.Equal(0, r.GetProperty("extra").GetInt32()); });
    }

    [Fact]
    public void Overview_Issues_CarryTheNewCodes()
    {
        using var mod = Mod(("locale/en/a.cfg", "[item-name]\nx=1\nbad line\n"), ("locale/de/a.cfg", "[item-name]\ny=1\n"));
        var codes = Inspect(mod).Issues.Select(i => i.Code).ToList();
        Assert.Contains("FMS040", codes);
        Assert.Contains("FMS044", codes);
        Assert.Contains("FMS045", codes);
    }

    // ---------------------------------------------------------------- example mod

    [Fact]
    public void ExampleHelloMod_HasNoLocaleIssues()
    {
        using var copy = new TempDir();
        CopyDir(Path.Combine(Repo(), "examples", "hello-mod"), copy.Path);
        var r = ModValidator.Validate(copy.Path);
        AssertNoLocaleIssues(r);
        var info = ModInspector.Inspect(copy.Path).Issues.Where(i => i.Code.StartsWith("FMS04"));
        Assert.Empty(info.Where(i => i.Severity != Severity.Info).ToList());
    }

    // ---------------------------------------------------------------- CLI

    private static (int Code, string Out) Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Program.Run(args, output, error, null);
        return (code, output.ToString());
    }

    private static JsonElement CliIssues(string json) => JsonDocument.Parse(json).RootElement.GetProperty("issues").Clone();

    [Fact]
    public void Cli_ValidateJson_CarriesLocaleCodesWithFileAndLine()
    {
        using var mod = Mod(
            ("data.lua", Proto("item", "zed")),
            ("locale/en/a.cfg", "[my-gui]\nk=K\n[item-name]\ndup=1\ndup=2\nbroken\n"),
            ("locale/de/a.cfg", "[my-gui]\nk=K\nextra=E\n"),
            ("control.lua", "local s = {\"my-gui.nope\"}\n"));
        var (code, output) = Cli("validate", mod.Path, "--json");
        Assert.Equal(Program.ExitProblemsFound, code); // FMS040 is an error
        var issues = CliIssues(output).EnumerateArray().ToList();
        string[] codes = issues.Select(i => i.GetProperty("code").GetString()!).ToArray();
        foreach (var expected in new[] { "FMS040", "FMS041", "FMS042", "FMS043", "FMS044", "FMS045" })
            Assert.Contains(expected, codes);
        var f40 = issues.Single(i => i.GetProperty("code").GetString() == "FMS040");
        Assert.Equal(6, f40.GetProperty("line").GetInt32());
        Assert.Contains("a.cfg", f40.GetProperty("file").GetString());
        Assert.Equal("error", f40.GetProperty("severity").GetString());
        var f41 = issues.Single(i => i.GetProperty("code").GetString() == "FMS041");
        Assert.Equal("warning", f41.GetProperty("severity").GetString());
        var f45 = issues.Single(i => i.GetProperty("code").GetString() == "FMS045");
        Assert.Equal("info", f45.GetProperty("severity").GetString());
    }

    [Fact]
    public void Cli_Validate_WarningsOnly_ExitsZero_AndTextOutputShowsCode()
    {
        using var mod = Mod(("data.lua", Proto("item", "zed")), ("locale/en/a.cfg", "[mod-name]\nx=X\n"));
        var (code, output) = Cli("validate", mod.Path);
        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("FMS042", output);
    }

    [Fact]
    public void Cli_Validate_ExampleHelloMod_HasNoLocaleCodes()
    {
        using var copy = new TempDir();
        CopyDir(Path.Combine(Repo(), "examples", "hello-mod"), copy.Path);
        var (_, output) = Cli("validate", copy.Path, "--json");
        var codes = CliIssues(output).EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToList();
        Assert.Empty(codes.Where(c => c.StartsWith("FMS04")).ToList());
    }

    // ---------------------------------------------------------------- performance + robustness

    [Fact]
    public void Performance_5000Keys_200LuaFiles_IsFast()
    {
        using var warm = Mod(("locale/en/a.cfg", "[my-gui]\nk=1\n"), ("control.lua", "local s = {\"my-gui.k\"}\n"));
        Validate(warm);

        var en = new StringBuilder();
        var de = new StringBuilder();
        for (var s = 0; s < 50; s++)
        {
            en.Append($"[sec-{s}]\n");
            de.Append($"[sec-{s}]\n");
            for (var k = 0; k < 100; k++)
            {
                en.Append($"key{k}=Value __1__ {s}\n");
                if (k % 2 == 0) de.Append($"key{k}=Wert __1__ {s}\n");
            }
        }
        using var mod = Mod(("locale/en/big.cfg", en.ToString()), ("locale/de/big.cfg", de.ToString()));
        for (var f = 0; f < 200; f++)
        {
            var lua = new StringBuilder();
            for (var j = 0; j < 25; j++) lua.Append($"local v{j} = {{\"sec-{(f + j) % 50}.key{(f * 7 + j) % 100}\"}}\nlocal q{j} = {{\"sec-{(f + j) % 50}.nokey{j}\"}}\n");
            mod.Write($"scripts/s{f}.lua", lua.ToString());
        }
        var sw = Stopwatch.StartNew();
        var result = Validate(mod);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"validate took {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Single(Of(result, "FMS044"));
        Assert.True(Of(result, "FMS043").Count > 0);
        AssertNone(result, "FMS041");
        AssertNone(result, "FMS040");
    }

    [Fact]
    public void Performance_ManyPrototypes_AndOneHugeSection_IsLinear()
    {
        var lua = new StringBuilder("data:extend({\n");
        for (var i = 0; i < 2000; i++) lua.Append($"  {{ type = \"item\", name = \"it-{i}\", stack_size = 1 }},\n");
        lua.Append("})\n");
        var cfg = new StringBuilder("[item-name]\n");
        for (var i = 0; i < 2000; i += 2) cfg.Append($"it-{i}=Item {i}\n");
        using var mod = Mod(("data.lua", lua.ToString()), ("locale/en/a.cfg", cfg.ToString()));
        var sw = Stopwatch.StartNew();
        var result = Validate(mod);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"validate took {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(1000, Of(result, "FMS042").Count);
    }

    [Fact]
    public void Robustness_EmptyBadUtf8AndNulBytes_NeverThrow()
    {
        using var mod = Mod(("locale/en/empty.cfg", ""), ("locale/de/empty.cfg", ""), ("control.lua", ""), ("scripts/empty.lua", ""));
        File.WriteAllBytes(mod.Combine("locale", "en", "bad.cfg"), new byte[] { 0x5B, 0xFF, 0xFE, 0x5D, 0x0A, 0xC3, 0x28, 0x3D, 0xA0, 0xA1, 0x0A });
        File.WriteAllBytes(mod.Combine("locale", "en", "nul.cfg"), new byte[] { 0x5B, 0x61, 0x00, 0x5D, 0x0A, 0x6B, 0x00, 0x3D, 0x00, 0x0A, 0x00, 0x00, 0x0A });
        File.WriteAllBytes(mod.Combine("locale", "de", "nul.cfg"), new byte[] { 0x00, 0x00, 0x00 });
        File.WriteAllBytes(mod.Combine("control.lua"), new byte[] { 0x7B, 0x22, 0xFF, 0x00, 0x2E, 0x22, 0x7D, 0x0A, 0x2D, 0x2D, 0x5B, 0x5B, 0x00 });
        File.WriteAllBytes(mod.Combine("data.lua"), new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x7B });
        File.WriteAllBytes(mod.Combine("locale", "en", "bom-only.cfg"), new byte[] { 0xEF, 0xBB, 0xBF });
        var result = Validate(mod);
        Assert.NotNull(result);
        Assert.NotNull(Inspect(mod));
    }

    [Fact]
    public void Robustness_OneMegabyteLines_NeverThrow_AndStayFast()
    {
        var huge = new string('x', 1024 * 1024);
        using var mod = Mod(
            ("locale/en/a.cfg", "[item-name]\nk=" + huge + "\n" + huge + "\n=" + huge + "\n[" + huge + "\n"),
            ("locale/de/a.cfg", "[item-name]\nk=" + huge + " __1__\n"),
            ("control.lua", "local s = {\"" + huge + ".y\"}\n" + "-- " + huge + "\n" + "local t = \"" + huge + "\"\n"));
        var sw = Stopwatch.StartNew();
        var result = Validate(mod);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed.TotalSeconds:F1}s");
        Assert.True(Of(result, "FMS040").Count >= 2);
        Assert.NotNull(Inspect(mod));
    }

    [Fact]
    public void Robustness_FuzzedLocaleAndLua_FixedSeed_NeverThrows()
    {
        var rng = new Random(20260612);
        string[] cfgPieces = { "[", "]", "[item-name]", "[a]", "[entity-name]", "=", "k=v", "k=", "=v", ";c", "#c", "__1__", "__ENTITY__x__", "__CONTROL__y__", "__ALT_CONTROL__1__z__", "\r", "\n", "\r\n", "﻿", "é", "\0", " ", "\t", "key=a=b", "k", "[]" };
        string[] luaPieces = { "{", "}", "\"", "'", "[[", "]]", "--", "--[[", "--[==[", "]==]", "\"a.b\"", "{\"a.k\"}", "{\"?\"", "type", "=", "name", "data:extend(", ")", ",", "\n", "\\", "localised_name", "\"item\"", "{ type = \"item\", name = \"q\" }", "\0", "é", " " };
        for (var iter = 0; iter < 150; iter++)
        {
            using var mod = Mod();
            string Make(string[] pieces)
            {
                var sb = new StringBuilder();
                var n = rng.Next(0, 60);
                for (var i = 0; i < n; i++) sb.Append(pieces[rng.Next(pieces.Length)]);
                return sb.ToString();
            }
            mod.Write("locale/en/f.cfg", Make(cfgPieces));
            mod.Write("locale/en/g.cfg", "[a]\nk=v\n" + Make(cfgPieces));
            mod.Write("locale/de/f.cfg", Make(cfgPieces));
            mod.Write("locale/xx/f.cfg", Make(cfgPieces));
            mod.Write("data.lua", Make(luaPieces));
            mod.Write("control.lua", Make(luaPieces));
            var bytes = new byte[rng.Next(0, 200)];
            rng.NextBytes(bytes);
            File.WriteAllBytes(mod.Combine("locale", "fr.cfg"), bytes);
            File.WriteAllBytes(mod.Combine("locale", "de", "bin.cfg"), bytes);
            File.WriteAllBytes(mod.Combine("scripts.lua"), bytes);
            try
            {
                Validate(mod);
                Inspect(mod);
            }
            catch (Exception ex)
            {
                throw new Exception($"iteration {iter} threw {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
    }

    [Fact]
    public void Robustness_DeeplyNestedLuaTables_DoNotOverflowTheStack()
    {
        using var mod = Mod(("locale/en/a.cfg", "[my-gui]\nk=1\n"), ("control.lua", new string('{', 20000) + "\"my-gui.k\"" + new string('}', 20000) + "\n"));
        var result = Validate(mod);
        Assert.NotNull(result);
    }
}
