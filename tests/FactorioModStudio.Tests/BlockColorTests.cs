using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>The colour of a building block: chosen when it is added, previewed, and changed later with its icons.</summary>
public class BlockColorTests
{
    private static TempDir Mod()
    {
        var mod = new TempDir();
        mod.Write("info.json", """{ "name": "my-mod", "version": "0.1.0", "title": "My mod", "factorio_version": "2.1" }""");
        return mod;
    }

    /// <summary>Factorio's data folder with grey stand-ins for a few base-game pictures.</summary>
    private static TempDir GameData(params string[] relativePngs)
    {
        var dir = new TempDir();
        var rgba = new byte[8 * 4 * 4];
        for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = 128; rgba[i + 1] = 128; rgba[i + 2] = 128; rgba[i + 3] = 255; }
        foreach (var rel in relativePngs)
        {
            var full = dir.Combine(("base/" + rel).Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, PngEncoder.Encode(8, 4, rgba));
        }
        return dir;
    }

    private static string Read(TempDir dir, string path) => File.ReadAllText(dir.Combine(path.Split('/')));

    private static (byte R, byte G, byte B) FirstPixel(byte[] png)
    {
        var image = IconRecolor.Decode(png)!;
        return (image.Rgba[0], image.Rgba[1], image.Rgba[2]);
    }

    [Theory]
    [InlineData("#ff0000", 1.0, 0.0, 0.0, "r = 1.0, g = 0.0, b = 0.0")]
    [InlineData("4cccff", 0.3, 0.8, 1.0, "r = 0.3, g = 0.8, b = 1.0")]
    [InlineData(" #4DCCFF ", 0.3, 0.8, 1.0, "r = 0.3, g = 0.8, b = 1.0")]
    public void BlockColor_ReadsHex_AndWritesTheLuaNumbers(string hex, double r, double g, double b, string lua)
    {
        var color = BlockColor.FromHex(hex)!;
        Assert.Equal((r, g, b), color.Tuple);
        Assert.Equal(lua, color.LuaParts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#fff")]
    [InlineData("red")]
    [InlineData("#gg0000")]
    public void BlockColor_RefusesAnythingButSixHexDigits(string? text) => Assert.Null(BlockColor.FromHex(text));

    [Fact]
    public void Blocks_SayTheirColourAndIcons()
    {
        var gun = ModContent.Blocks.Single(b => b.Id == "gun");
        Assert.Equal("#ff8c4d", gun.Color); // { r = 1.0, g = 0.55, b = 0.3 }
        Assert.Equal(new[] { "", "-ammo" }, gun.Icons);
    }

    [Fact]
    public void Apply_WithAColour_WritesItsTint_AndRecoloursTheIconsToIt()
    {
        using var mod = Mod();
        using var game = GameData("graphics/icons/iron-chest.png", "graphics/entity/iron-chest/iron-chest.png");
        ModContent.Apply(mod.Path, new ContentRequest("chest", "c1", Color: BlockColor.FromHex("#ff0000")), game.Path);

        Assert.Contains("local tint = { r = 1.0, g = 0.0, b = 0.0 }", Read(mod, "prototypes/c1.lua"));
        var (r, g, b) = FirstPixel(File.ReadAllBytes(mod.Combine("graphics", "icons", "c1.png")));
        Assert.True(r > g && g == b, $"expected a red pixel, got {r},{g},{b}");
        var (er, eg, eb) = FirstPixel(File.ReadAllBytes(mod.Combine("graphics", "entity", "c1.png")));
        Assert.True(er > eg && eg == eb, "the picture on the map takes the colour too");
    }

    [Fact]
    public void Apply_KeepsTheAlphaOfATint()
    {
        using var mod = Mod();
        ModContent.Apply(mod.Path, new ContentRequest("enemy", "bug", Color: BlockColor.FromHex("#0000ff")));
        Assert.Contains("local tint = { r = 0.0, g = 0.0, b = 1.0, a = 1 }", Read(mod, "prototypes/bug.lua"));
    }

    [Fact]
    public void PreviewIcon_IsWhatAddingWouldWrite()
    {
        using var mod = Mod();
        using var game = GameData("graphics/icons/submachine-gun.png", "graphics/icons/firearm-magazine.png");
        var color = BlockColor.FromHex("#33cc66")!;
        ModContent.Apply(mod.Path, new ContentRequest("gun", "g1", Color: color), game.Path);

        Assert.Equal(File.ReadAllBytes(mod.Combine("graphics", "icons", "g1-ammo.png")), ModContent.PreviewIcon(game.Path, "gun", "-ammo", color));
        Assert.Null(ModContent.PreviewIcon(game.Path, "gun", "-nothing", color));
        Assert.Null(ModContent.PreviewIcon(game.Path, "no-such-block", "", color));
        Assert.Null(ModContent.PreviewIcon(null, "gun", "", color));
    }

    [Fact]
    public void Recolor_ChangesTheTint_AndRedoesTheIconFiles_WithUndo()
    {
        using var mod = Mod();
        using var game = GameData("graphics/icons/submachine-gun.png", "graphics/icons/firearm-magazine.png");
        ModContent.Apply(mod.Path, new ContentRequest("gun", "g1", "Mine"), game.Path);
        var before = File.ReadAllBytes(mod.Combine("graphics", "icons", "g1.png"));
        var luaBefore = Read(mod, "prototypes/g1.lua");
        Assert.Equal("#ff8c4d", BlockOperations.ColorOf(luaBefore)!.Hex);

        var plan = BlockOperations.PlanRecolor(mod.Path, "prototypes/g1.lua", BlockColor.FromHex("#0000ff")!, game.Path);
        Assert.Equal("recolor", plan.Operation);
        Assert.Equal(new[] { "graphics/icons/g1-ammo.png", "graphics/icons/g1.png", "prototypes/g1.lua" }, plan.Changes.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Empty(plan.Warnings);
        var undo = BlockOperations.Apply(mod.Path, plan);

        var lua = Read(mod, "prototypes/g1.lua");
        Assert.Contains("local tint = { r = 0.0, g = 0.0, b = 1.0 }", lua);
        Assert.Equal(luaBefore.Replace("r = 1.0, g = 0.55, b = 0.3", "r = 0.0, g = 0.0, b = 1.0"), lua); // nothing else changes
        var (r, g, b) = FirstPixel(File.ReadAllBytes(mod.Combine("graphics", "icons", "g1.png")));
        Assert.True(b > r && r == g, $"expected a blue pixel, got {r},{g},{b}");

        BlockOperations.Restore(mod.Path, undo);
        Assert.Equal(before, File.ReadAllBytes(mod.Combine("graphics", "icons", "g1.png")));
        Assert.Equal(luaBefore, Read(mod, "prototypes/g1.lua"));
    }

    [Fact]
    public void Recolor_WithoutTheGame_ChangesOnlyTheTint_AndSaysSo()
    {
        using var mod = Mod();
        using var game = GameData("graphics/icons/iron-chest.png");
        ModContent.Apply(mod.Path, new ContentRequest("chest", "c1"), game.Path);

        var plan = BlockOperations.PlanRecolor(mod.Path, "prototypes/c1.lua", BlockColor.FromHex("#00ff00")!, gameDataDirectory: null);

        Assert.Equal(new[] { "prototypes/c1.lua" }, plan.Changes.Select(c => c.Path));
        Assert.Contains(plan.Warnings, w => w.Contains("No Factorio installation"));
    }

    [Fact]
    public void Recolor_RefusesAnOldBlock()
    {
        using var mod = Mod();
        ModContent.Apply(mod.Path, new ContentRequest("item", "thing"));
        mod.Write("prototypes/thing.lua", Read(mod, "prototypes/thing.lua").Replace("prototypes.fms-copy-v2", "prototypes.fms-copy-v1"));

        var error = Assert.Throws<FmsException>(() => BlockOperations.PlanRecolor(mod.Path, "prototypes/thing.lua", BlockColor.FromHex("#00ff00")!, null));
        Assert.Contains("icons of its own first", error.Message);
    }
}
