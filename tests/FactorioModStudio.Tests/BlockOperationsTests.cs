using System.Text;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Renaming, removing and upgrading a building block with everything that belongs to it.</summary>
public class BlockOperationsTests
{
    private static TempDir ModWith(params (string Block, string Name)[] blocks)
    {
        var mod = new TempDir();
        mod.Write("info.json", """{ "name": "my-mod", "version": "0.1.0", "title": "My mod", "factorio_version": "2.1" }""");
        mod.Write("data.lua", "-- data\n");
        mod.Write("locale/en/my-mod.cfg", "[mod-name]\nmy-mod=My mod\n");
        mod.Write("locale/ru/my-mod.cfg", "[item-name]\nmy-gun=Моё ружьё\nmy-gun-ammo=Патроны\n");
        foreach (var (block, name) in blocks) ModContent.Apply(mod.Path, new ContentRequest(block, name, "Mine"));
        return mod;
    }

    private static Dictionary<string, string> Snapshot(TempDir dir) =>
        Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir.Path, f).Replace('\\', '/'), f => Convert.ToBase64String(File.ReadAllBytes(f)));

    private static string Read(TempDir dir, string path) => File.ReadAllText(dir.Combine(path.Split('/')));

    [Fact]
    public void Rename_MovesTheBlockAndEverythingThatNamesIt()
    {
        using var mod = ModWith(("gun", "my-gun"));
        mod.Write("graphics/icons/my-gun-ammo.png", "png");
        mod.Write("control.lua", "local wanted = { \"my-gun\", \"my-gun-ammo\", \"my-gun-other\" }\n");
        // Make the block name the icon file the way a block with game data does.
        var file = Read(mod, "prototypes/my-gun.lua").Replace("copy.use_icons({})", "copy.use_icons({ [\"my-gun-ammo\"] = \"__my-mod__/graphics/icons/my-gun-ammo.png\" })");
        mod.Write("prototypes/my-gun.lua", file);

        var plan = BlockOperations.PlanRename(mod.Path, "prototypes/my-gun.lua", "rifle");
        BlockOperations.Apply(mod.Path, plan);

        Assert.Equal(new Dictionary<string, string> { ["my-gun"] = "rifle", ["my-gun-ammo"] = "rifle-ammo" }, plan.Renamed);
        Assert.False(File.Exists(mod.Combine("prototypes", "my-gun.lua")));
        var lua = Read(mod, "prototypes/rifle.lua");
        Assert.Contains("local name = \"rifle\"", lua);
        Assert.StartsWith("-- Mine (rifle), added by Factorio Mod Studio.", lua);
        Assert.Contains("[\"rifle-ammo\"] = \"__my-mod__/graphics/icons/rifle-ammo.png\"", lua);
        Assert.True(File.Exists(mod.Combine("graphics", "icons", "rifle-ammo.png")));
        Assert.False(File.Exists(mod.Combine("graphics", "icons", "my-gun-ammo.png")));
        Assert.Contains("require(\"prototypes.rifle\")", Read(mod, "data.lua"));
        Assert.DoesNotContain("my-gun", Read(mod, "data.lua"));
        Assert.Contains("rifle=Mine", Read(mod, "locale/en/my-mod.cfg"));
        Assert.Contains("rifle-ammo=Патроны", Read(mod, "locale/ru/my-mod.cfg"));
        Assert.Equal("local wanted = { \"rifle\", \"rifle-ammo\", \"my-gun-other\" }\n", Read(mod, "control.lua")); // only exact names
        var migration = Read(mod, "migrations/my-mod-rename-my-gun-to-rifle.json");
        Assert.Contains("\"item\"", migration);
        Assert.Contains("\"recipe\"", migration);
    }

    [Fact]
    public void Apply_ThenRestore_LeavesTheModAsItWas()
    {
        using var mod = ModWith(("chest", "box"), ("wall", "fence"));
        var before = Snapshot(mod);

        var undo = BlockOperations.Apply(mod.Path, BlockOperations.PlanRename(mod.Path, "prototypes/box.lua", "crate"));
        Assert.NotEqual(before, Snapshot(mod));
        BlockOperations.Restore(mod.Path, undo);

        Assert.Equal(before, Snapshot(mod));
        Assert.False(Directory.Exists(mod.Combine("migrations"))); // the folder the rename made is gone again
    }

    [Fact]
    public void Rename_RefusesBadAndTakenNames()
    {
        using var mod = ModWith(("chest", "box"), ("wall", "fence"));
        Assert.Throws<FmsException>(() => BlockOperations.PlanRename(mod.Path, "prototypes/box.lua", "two words"));
        Assert.Throws<FmsException>(() => BlockOperations.PlanRename(mod.Path, "prototypes/box.lua", "box"));
        Assert.Throws<FmsException>(() => BlockOperations.PlanRename(mod.Path, "prototypes/box.lua", "fence"));
        Assert.Throws<FmsException>(() => BlockOperations.PlanRename(mod.Path, "data.lua", "x"));
        Assert.Throws<FmsException>(() => BlockOperations.PlanRename(mod.Path, "../box.lua", "x"));
    }

    [Fact]
    public void Remove_TakesTheBlockOut_AndTheHelpersWithTheLastOne()
    {
        using var mod = ModWith(("chest", "box"), ("wall", "fence"));
        mod.Write("control.lua", "script.on_init(function() game.print(\"fence\") end)\n");

        var first = BlockOperations.PlanRemove(mod.Path, "prototypes/fence.lua");
        BlockOperations.Apply(mod.Path, first);
        Assert.False(File.Exists(mod.Combine("prototypes", "fence.lua")));
        Assert.DoesNotContain("prototypes.fence", Read(mod, "data.lua"));
        Assert.Contains("prototypes.box", Read(mod, "data.lua"));
        Assert.DoesNotContain("fence=", Read(mod, "locale/en/my-mod.cfg"));
        Assert.True(File.Exists(mod.Combine("prototypes", "fms-copy-v2.lua"))); // the chest still needs them
        Assert.Contains(first.Warnings, w => w.Contains("control.lua") && w.Contains("fence"));

        BlockOperations.Apply(mod.Path, BlockOperations.PlanRemove(mod.Path, "prototypes/box.lua"));
        Assert.False(File.Exists(mod.Combine("prototypes", "fms-copy-v2.lua")));
    }

    [Fact]
    public void Upgrade_MovesAnOldBlockToTheCurrentHelpers()
    {
        using var mod = ModWith(("enemy", "bug"));
        // The same block as the first helpers wrote it: v1, no icon registration, no set_icon for the hand-made copy.
        var v1 = Read(mod, "prototypes/bug.lua")
            .Replace("prototypes.fms-copy-v2", "prototypes.fms-copy-v1")
            .Replace("copy.use_icons({})\n", "").Replace("copy.use_sprites({})\n", "")
            .Replace("copy.set_icon(biter, name, tint)\n", "");
        mod.Write("prototypes/bug.lua", v1);
        File.Move(mod.Combine("prototypes", "fms-copy-v2.lua"), mod.Combine("prototypes", "fms-copy-v1.lua"));
        Assert.True(BlockOperations.NeedsUpgrade(v1));

        var plan = BlockOperations.PlanUpgrade(mod.Path, "prototypes/bug.lua", gameDataDirectory: null);
        BlockOperations.Apply(mod.Path, plan);

        var lua = Read(mod, "prototypes/bug.lua");
        Assert.Contains("require(\"prototypes.fms-copy-v2\")", lua);
        Assert.Contains("copy.use_icons({})", lua);
        Assert.Contains("biter.name = name\ncopy.set_icon(biter, name, tint)\n", lua);
        Assert.True(File.Exists(mod.Combine("prototypes", "fms-copy-v2.lua")));
        Assert.Contains(plan.Warnings, w => w.Contains("No Factorio installation"));
        Assert.False(BlockOperations.NeedsUpgrade(lua));
        Assert.Throws<FmsException>(() => BlockOperations.PlanUpgrade(mod.Path, "prototypes/bug.lua", null));
    }
}
