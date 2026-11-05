using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>
/// Black-box tests of the static reference analysis (Files tab v2 contract, "Core: ModReferences").
/// Everything goes through <see cref="ModInspector.Inspect"/>: the contract fixes the observable result
/// (issues in <see cref="ModOverview.Issues"/>, <c>ModFileEntry.Unused</c>), not the shape of ModReferences itself.
/// The fixture mod is named "my-mod" (see <see cref="Fixtures.ValidInfo"/>).
/// </summary>
public class ModReferencesTests
{
    private static ModOverview Inspect(TempDir mod) => ModInspector.Inspect(mod.Path);

    private static List<ValidationIssue> Issues(ModOverview o, string code) => o.Issues.Where(i => i.Code == code).ToList();

    private static bool? UnusedOf(ModOverview o, string path) => o.Files.Single(f => f.Path == path).Unused;

    private static void Png(TempDir mod, string relative)
    {
        var full = mod.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Fixtures.WritePngHeader(full, 64, 64);
    }

    // ------------------------------------------------------------------ assets

    [Fact]
    public void ExistingOwnAsset_IsNotReported_AndCountsAsUsed()
    {
        using var mod = Fixtures.ValidMod();
        Png(mod, "graphics/item.png");
        mod.Write("data.lua", "data:extend({ { type = \"item\", name = \"x\", icon = \"__my-mod__/graphics/item.png\", icon_size = 64, stack_size = 1 } })\n");

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS030"));
        Assert.Empty(Issues(o, "FMS031"));
        Assert.False(UnusedOf(o, "graphics/item.png"));
    }

    [Fact]
    public void MissingOwnAsset_IsAnError_WithFileAndLineOfTheLiteral()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "data:extend({\n  {\n    type = \"item\", name = \"x\", icon = \"__my-mod__/graphics/nope.png\", icon_size = 64, stack_size = 1\n  }\n})\n");

        var issue = Assert.Single(Issues(Inspect(mod), "FMS030"));
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Equal("data.lua", issue.File);
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public void MissingAsset_InNestedScript_ReportsThatScriptsPath()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "require(\"prototypes.items\")\n");
        mod.Write("prototypes/items.lua", "-- items\nlocal s = \"__my-mod__/sound/missing.ogg\"\n");

        var issue = Assert.Single(Issues(Inspect(mod), "FMS030"));
        Assert.Equal("prototypes/items.lua", issue.File);
        Assert.Equal(2, issue.Line);
    }

    [Fact]
    public void AssetThatExistsOnlyWithDifferentCase_IsCaseMismatchError_NotMissing()
    {
        using var mod = Fixtures.ValidMod();
        Png(mod, "graphics/Item.png");
        mod.Write("data.lua", "local icon = \"__my-mod__/graphics/item.png\"\n");

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS030"));
        var issue = Assert.Single(Issues(o, "FMS031"));
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Equal("data.lua", issue.File);
        Assert.Equal(1, issue.Line);
    }

    [Fact]
    public void FolderCaseMismatch_IsAlsoReported()
    {
        using var mod = Fixtures.ValidMod();
        Png(mod, "Graphics/item.png");
        mod.Write("data.lua", "local icon = \"__my-mod__/graphics/item.png\"\n");

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS030"));
        Assert.Single(Issues(o, "FMS031"));
    }

    [Fact]
    public void DynamicPrefix_IsNeverReportedMissing_AndMarksEverythingUnderItAsUsed()
    {
        using var mod = Fixtures.ValidMod();
        Png(mod, "graphics/icons/a.png");
        Png(mod, "graphics/icons/sub/b.png");
        Png(mod, "graphics/other.png");
        mod.Write("data.lua", """
            for _, name in pairs({"a", "sub/b"}) do
              local icon = "__my-mod__/graphics/icons/" .. name .. ".png"
            end
            local nothing_there = "__my-mod__/graphics/does-not-exist/" .. "x.png"
            """);

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS030"));
        Assert.Empty(Issues(o, "FMS031"));
        Assert.False(UnusedOf(o, "graphics/icons/a.png"));
        Assert.False(UnusedOf(o, "graphics/icons/sub/b.png"));
        Assert.True(UnusedOf(o, "graphics/other.png"));
    }

    [Fact]
    public void LiteralOnTheRightOfConcatenation_IsNotAPrefix()
    {
        // Only the LEFT part of a `..` is a prefix; a complete literal elsewhere is still checked.
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "local p = \"__my-mod__/graphics/\" .. \"x.png\"\nlocal q = \"__my-mod__/graphics/missing.png\"\n");

        var issue = Assert.Single(Issues(Inspect(mod), "FMS030"));
        Assert.Equal(2, issue.Line);
    }

    [Fact]
    public void BaseCoreAndOtherModReferences_AreIgnored()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", """
            local a = "__base__/graphics/icons/iron-plate.png"
            local b = "__core__/graphics/cursor.png"
            local c = "__other-mod__/graphics/x.png"
            local d = "__my-mod-extra__/graphics/x.png"
            local e = "__space-age__/sound/y.ogg"
            local f = "__base__/graphics/icons/" .. name .. ".png"
            """);

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS030"));
        Assert.Empty(Issues(o, "FMS031"));
    }

    [Fact]
    public void AssetLikeTextInComments_IsIgnored()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "-- icon = \"__my-mod__/graphics/gone.png\"\n--[[ \"__my-mod__/graphics/gone2.png\" ]]\n");

        Assert.Empty(Issues(Inspect(mod), "FMS030"));
    }

    [Fact]
    public void ReferenceIssues_DoNotDuplicateWithinOneInspection()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "local a = \"__my-mod__/graphics/nope.png\"\nlocal b = \"__my-mod__/graphics/nope.png\"\n");

        var issues = Issues(Inspect(mod), "FMS030");
        Assert.Equal(new[] { 1, 2 }, issues.Select(i => i.Line ?? 0).OrderBy(x => x).ToArray());
    }

    // ------------------------------------------------------------------ require

    [Theory]
    [InlineData("require(\"scripts.util\")")]
    [InlineData("require \"scripts/util\"")]
    [InlineData("require('scripts.util')")]
    [InlineData("require 'scripts.util'")]
    [InlineData("local u = require(\"scripts/util\")")]
    public void Require_LiteralForms_ResolveFromModRoot(string line)
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("control.lua", line + "\n");
        mod.Write("scripts/util.lua", "return {}\n");

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS032"));
        Assert.False(UnusedOf(o, "scripts/util.lua"));
    }

    [Fact]
    public void Require_ResolvesRelativeToTheRequiringFileFirst()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("control.lua", "require(\"scripts.a\")\n");
        mod.Write("scripts/a.lua", "require(\"c\")\n");
        mod.Write("scripts/c.lua", "return 1\n");
        mod.Write("c.lua", "return 2\n");

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS032"));
        Assert.False(UnusedOf(o, "scripts/c.lua"));
        Assert.True(UnusedOf(o, "c.lua"));
    }

    [Fact]
    public void Require_FallsBackToModRoot()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("control.lua", "require(\"scripts.a\")\n");
        mod.Write("scripts/a.lua", "require(\"lib.shared\")\n");
        mod.Write("lib/shared.lua", "return {}\n");

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS032"));
        Assert.False(UnusedOf(o, "lib/shared.lua"));
    }

    [Fact]
    public void Require_OwnModPrefix_ResolvesFromModRoot()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "require(\"prototypes.items\")\n");
        mod.Write("prototypes/items.lua", "require(\"__my-mod__.lib.x\")\n");
        mod.Write("lib/x.lua", "return {}\n");

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS032"));
        Assert.False(UnusedOf(o, "lib/x.lua"));
    }

    [Fact]
    public void Require_Missing_IsAnErrorWithFileAndLine()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("control.lua", "-- header\nlocal x = 1\nrequire(\"scripts.nope\")\n");

        var issue = Assert.Single(Issues(Inspect(mod), "FMS032"));
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Equal("control.lua", issue.File);
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public void Require_OwnModPrefixMissing_IsReported()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("control.lua", "require(\"__my-mod__.lib.gone\")\n");

        Assert.Single(Issues(Inspect(mod), "FMS032"));
    }

    [Fact]
    public void Require_OtherModsBaseAndNonLiteral_AreIgnored()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", """
            require("__base__.prototypes.entity.pipecovers")
            require("__core__.lualib.util")
            require("__other-mod__.lib.thing")
            require("__flib__/data-util")
            local name = "x"
            require(name)
            require("scripts." .. name)
            -- require("commented.out")
            local s = "require('inside.a.string')"
            """);

        Assert.Empty(Issues(Inspect(mod), "FMS032"));
    }

    [Fact]
    public void Require_UnknownBareName_IsMissing()
    {
        // A bare name that is neither next to the requiring file nor at the mod root is missing.
        using var mod = Fixtures.ValidMod();
        mod.Write("control.lua", "require(\"definitely_not_here\")\n");

        Assert.Single(Issues(Inspect(mod), "FMS032"));
    }

    // ------------------------------------------------------------------ Unused

    [Fact]
    public void Unused_FlagsUnreferencedGraphicsSoundsAndScripts()
    {
        using var mod = Fixtures.ValidMod();
        Png(mod, "graphics/used.png");
        Png(mod, "graphics/orphan.png");
        mod.Write("sound/used.ogg", "OggS");
        mod.Write("sound/orphan.ogg", "OggS");
        mod.Write("lib/orphan.lua", "return {}\n");
        mod.Write("data.lua", "local a = \"__my-mod__/graphics/used.png\"\nlocal b = \"__my-mod__/sound/used.ogg\"\n");

        var o = Inspect(mod);
        Assert.False(UnusedOf(o, "graphics/used.png"));
        Assert.True(UnusedOf(o, "graphics/orphan.png"));
        Assert.False(UnusedOf(o, "sound/used.ogg"));
        Assert.True(UnusedOf(o, "sound/orphan.ogg"));
        Assert.True(UnusedOf(o, "lib/orphan.lua"));
    }

    [Fact]
    public void Unused_IsNullForCategoriesOtherThanGraphicsSoundScript()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("README.md", "# hi\n");
        mod.Write("data/table.json", "{}\n");
        mod.Write("misc/blob.bin", "x");
        mod.Write("locale/en/strings.cfg", "[a]\nb=c\n");

        var o = Inspect(mod);
        Assert.Null(UnusedOf(o, "info.json"));
        Assert.Null(UnusedOf(o, "changelog.txt"));
        Assert.Null(UnusedOf(o, "README.md"));
        Assert.Null(UnusedOf(o, "data/table.json"));
        Assert.Null(UnusedOf(o, "misc/blob.bin"));
        Assert.Null(UnusedOf(o, "locale/en/strings.cfg"));
    }

    [Fact]
    public void Unused_EntryPointsAndSpecialFoldersAreNeverUnused()
    {
        using var mod = Fixtures.ValidMod();
        foreach (var entry in new[] { "data.lua", "data-updates.lua", "data-final-fixes.lua", "control.lua",
                     "settings.lua", "settings-updates.lua", "settings-final-fixes.lua" })
            mod.Write(entry, "-- " + entry + "\n");
        mod.Write("migrations/1.0.0.lua", "-- m\n");
        mod.Write("scenarios/sc/control.lua", "-- s\n");
        Png(mod, "scenarios/sc/image.png");
        mod.Write("campaigns/c/a.lua", "-- c\n");
        Png(mod, "campaigns/c/b.png");
        mod.Write("tutorials/t/a.lua", "-- t\n");
        mod.Write("tutorials/t/sound.ogg", "OggS");
        Png(mod, "locale/en/flag.png");

        var o = Inspect(mod);
        foreach (var path in new[]
                 {
                     "data.lua", "data-updates.lua", "data-final-fixes.lua", "control.lua", "settings.lua", "settings-updates.lua",
                     "settings-final-fixes.lua", "thumbnail.png", "migrations/1.0.0.lua", "scenarios/sc/control.lua",
                     "scenarios/sc/image.png", "campaigns/c/a.lua", "campaigns/c/b.png", "tutorials/t/a.lua", "tutorials/t/sound.ogg",
                     "locale/en/flag.png"
                 })
            Assert.True(UnusedOf(o, path) != true, path + " must never be unused");

        // Entry points are scripts, so the flag applies and is false (not null).
        Assert.False(UnusedOf(o, "control.lua"));
        Assert.False(UnusedOf(o, "data.lua"));
        Assert.Null(UnusedOf(o, "info.json"));
        Assert.Null(UnusedOf(o, "changelog.txt"));
    }

    [Fact]
    public void Unused_RequireReachabilityIsTransitive_AndCountsRequiresFromAnyLuaFile()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "require(\"prototypes.items\")\n");
        mod.Write("prototypes/items.lua", "require(\"prototypes.helpers\")\n");
        mod.Write("prototypes/helpers.lua", "return {}\n");
        // An orphan script is itself unused, but whatever it requires is "reachable via require from any .lua".
        mod.Write("lib/orphan.lua", "require(\"lib.dep\")\n");
        mod.Write("lib/dep.lua", "return {}\n");

        var o = Inspect(mod);
        Assert.False(UnusedOf(o, "prototypes/items.lua"));
        Assert.False(UnusedOf(o, "prototypes/helpers.lua"));
        Assert.True(UnusedOf(o, "lib/orphan.lua"));
        Assert.False(UnusedOf(o, "lib/dep.lua"));
    }

    [Fact]
    public void Unused_AssetReferencedViaCaseMismatch_IsNotAlsoMissing()
    {
        using var mod = Fixtures.ValidMod();
        Png(mod, "graphics/Shout.png");
        mod.Write("data.lua", "local x = \"__my-mod__/graphics/shout.png\"\n");

        var o = Inspect(mod);
        Assert.Empty(Issues(o, "FMS030"));
        Assert.Single(Issues(o, "FMS031"));
    }

    [Fact]
    public void CleanMod_HasNoReferenceIssues_AndOnlyEntryPoints()
    {
        using var mod = Fixtures.ValidMod();
        var o = Inspect(mod);
        Assert.DoesNotContain(o.Issues, i => i.Code is "FMS030" or "FMS031" or "FMS032");
        Assert.False(UnusedOf(o, "data.lua"));
        Assert.False(UnusedOf(o, "thumbnail.png"));
    }
}
