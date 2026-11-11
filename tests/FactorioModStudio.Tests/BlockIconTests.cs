using System.IO.Compression;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Replacing a building block's icons with pictures of your own, and going back to the recoloured originals.</summary>
public class BlockIconTests
{
    private static TempDir Mod()
    {
        var mod = new TempDir();
        mod.Write("info.json", """{ "name": "my-mod", "version": "0.1.0", "title": "My mod", "factorio_version": "2.1" }""");
        return mod;
    }

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

    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a; }
        return PngEncoder.Encode(width, height, rgba);
    }

    /// <summary>A PNG written by hand: any colour type and depth, rows unfiltered (CRCs are left zero; the reader does not check them).</summary>
    private static byte[] RawPng(int width, int height, byte depth, byte colour, byte[] rows, byte[]? palette = null, byte[]? transparency = null)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            output.Write(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
            output.Write(System.Text.Encoding.ASCII.GetBytes(type));
            output.Write(data);
            output.Write(new byte[4]);
        }
        Chunk("IHDR", new byte[] { 0, 0, 0, (byte)width, 0, 0, 0, (byte)height, depth, colour, 0, 0, 0 });
        if (palette is not null) Chunk("PLTE", palette);
        if (transparency is not null) Chunk("tRNS", transparency);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(rows);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static IconRecolor.Image Image(byte[] png) => IconRecolor.Decode(png)!;

    [Fact]
    public void FitIcon_ScalesASquarePicture_To64()
    {
        var fitted = IconRecolor.FitIcon(Solid(128, 128, 10, 200, 30), 64, out var problem, out var size)!;
        Assert.Null(problem);
        Assert.Equal((128, 128), size);
        var image = Image(fitted);
        Assert.Equal((64, 64), (image.Width, image.Height));
        Assert.Equal(new byte[] { 10, 200, 30, 255 }, image.Rgba[..4]);
    }

    [Fact]
    public void FitIcon_TakesTheLeftSquareOfAnIconWithMipmaps()
    {
        // 64×64 red with green "mipmaps" to its right, like the game's icon files (120×64).
        var rgba = new byte[120 * 64 * 4];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 120; x++)
        {
            var i = (y * 120 + x) * 4;
            rgba[i] = (byte)(x < 64 ? 255 : 0); rgba[i + 1] = (byte)(x < 64 ? 0 : 255); rgba[i + 3] = 255;
        }
        var image = Image(IconRecolor.FitIcon(PngEncoder.Encode(120, 64, rgba), 64, out _, out _)!);
        Assert.Equal((64, 64), (image.Width, image.Height));
        Assert.All(Enumerable.Range(0, 64 * 64), p => Assert.Equal(255, image.Rgba[p * 4]));
    }

    [Fact]
    public void FitIcon_RefusesATallPicture_AndWhatIsNotAPng()
    {
        Assert.Null(IconRecolor.FitIcon(Solid(32, 64, 1, 2, 3), 64, out var problem, out _));
        Assert.Contains("square", problem);
        Assert.Null(IconRecolor.FitIcon(new byte[] { 1, 2, 3 }, 64, out problem, out _));
        Assert.Contains("not a PNG", problem);
    }

    [Fact]
    public void Decode_ReadsPalettesWithTransparency_GreyAndGreyAlpha()
    {
        // 2×1, 4-bit palette: index 0 red and transparent, index 1 blue.
        var palette = RawPng(2, 1, 4, 3, new byte[] { 0, 0x01 }, palette: new byte[] { 255, 0, 0, 0, 0, 255 }, transparency: new byte[] { 0 });
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 0, 255, 255 }, Image(palette).Rgba);

        var grey = RawPng(2, 1, 8, 0, new byte[] { 0, 0, 200 });
        Assert.Equal(new byte[] { 0, 0, 0, 255, 200, 200, 200, 255 }, Image(grey).Rgba);

        var greyAlpha = RawPng(1, 1, 8, 4, new byte[] { 0, 90, 128 });
        Assert.Equal(new byte[] { 90, 90, 90, 128 }, Image(greyAlpha).Rgba);

        var oneBit = RawPng(8, 1, 1, 0, new byte[] { 0, 0b1000_0001 });
        Assert.Equal(255, Image(oneBit).Rgba[0]);
        Assert.Equal(0, Image(oneBit).Rgba[4]);
        Assert.Equal(255, Image(oneBit).Rgba[28]);
    }

    [Fact]
    public void PlanIcon_ReplacesTheBlocksIconFile_AndSaysItWasScaled()
    {
        using var mod = Mod();
        using var game = GameData("graphics/icons/submachine-gun.png", "graphics/icons/firearm-magazine.png");
        ModContent.Apply(mod.Path, new ContentRequest("gun", "g1"), game.Path);
        var lua = File.ReadAllText(mod.Combine("prototypes", "g1.lua"));

        var plan = BlockOperations.PlanIcon(mod.Path, "prototypes/g1.lua", "-ammo", Solid(256, 256, 0, 0, 255), game.Path);
        Assert.Equal("icon", plan.Operation);
        Assert.Equal(new[] { "graphics/icons/g1-ammo.png" }, plan.Changes.Select(c => c.Path));
        Assert.Contains(plan.Warnings, w => w.Contains("256×256"));
        var undo = BlockOperations.Apply(mod.Path, plan);

        var image = Image(File.ReadAllBytes(mod.Combine("graphics", "icons", "g1-ammo.png")));
        Assert.Equal((64, 64, (byte)0, (byte)255), (image.Width, image.Height, image.Rgba[0], image.Rgba[2]));
        Assert.Equal(lua, File.ReadAllText(mod.Combine("prototypes", "g1.lua")));
        var icons = BlockOperations.IconsOf(mod.Path, "prototypes/g1.lua", game.Path);
        Assert.True(icons.Single(i => i.Suffix == "-ammo").Custom);
        Assert.False(icons.Single(i => i.Suffix == "").Custom);

        BlockOperations.Restore(mod.Path, undo);
        Assert.False(BlockOperations.IconsOf(mod.Path, "prototypes/g1.lua", game.Path).Single(i => i.Suffix == "-ammo").Custom);
    }

    [Fact]
    public void PlanIcon_GivesAnIconFileToAPrototypeThatHadNone()
    {
        using var mod = Mod();
        ModContent.Apply(mod.Path, new ContentRequest("chest", "c1")); // no game: the icon is only tinted in the game
        Assert.Null(BlockOperations.IconsOf(mod.Path, "prototypes/c1.lua").Single().Path);

        BlockOperations.Apply(mod.Path, BlockOperations.PlanIcon(mod.Path, "prototypes/c1.lua", "", Solid(64, 64, 1, 2, 3), null));

        Assert.Contains("[\"c1\"] = \"__my-mod__/graphics/icons/c1.png\"", File.ReadAllText(mod.Combine("prototypes", "c1.lua")));
        Assert.True(File.Exists(mod.Combine("graphics", "icons", "c1.png")));
        Assert.Equal("graphics/icons/c1.png", BlockOperations.IconsOf(mod.Path, "prototypes/c1.lua").Single().Path);
    }

    [Fact]
    public void PlanIcon_WithoutAPicture_PutsTheRecolouredOriginalBack()
    {
        using var mod = Mod();
        using var game = GameData("graphics/icons/iron-chest.png");
        ModContent.Apply(mod.Path, new ContentRequest("chest", "c1"), game.Path);
        var original = File.ReadAllBytes(mod.Combine("graphics", "icons", "c1.png"));
        BlockOperations.Apply(mod.Path, BlockOperations.PlanIcon(mod.Path, "prototypes/c1.lua", "", Solid(64, 64, 1, 2, 3), game.Path));

        BlockOperations.Apply(mod.Path, BlockOperations.PlanIcon(mod.Path, "prototypes/c1.lua", "", null, game.Path));

        Assert.Equal(original, File.ReadAllBytes(mod.Combine("graphics", "icons", "c1.png")));
        Assert.Throws<FmsException>(() => BlockOperations.PlanIcon(mod.Path, "prototypes/c1.lua", "", null, null));
        Assert.Throws<FmsException>(() => BlockOperations.PlanIcon(mod.Path, "prototypes/c1.lua", "-nothing", Solid(64, 64, 1, 2, 3), null));
    }

    [Fact]
    public void Recolor_KeepsAnIconOfYourOwn()
    {
        using var mod = Mod();
        using var game = GameData("graphics/icons/submachine-gun.png", "graphics/icons/firearm-magazine.png");
        ModContent.Apply(mod.Path, new ContentRequest("gun", "g1"), game.Path);
        BlockOperations.Apply(mod.Path, BlockOperations.PlanIcon(mod.Path, "prototypes/g1.lua", "", Solid(64, 64, 9, 9, 9), game.Path));
        var mine = File.ReadAllBytes(mod.Combine("graphics", "icons", "g1.png"));

        var plan = BlockOperations.PlanRecolor(mod.Path, "prototypes/g1.lua", BlockColor.FromHex("#0000ff")!, game.Path);

        Assert.DoesNotContain(plan.Changes, c => c.Path == "graphics/icons/g1.png");
        Assert.Contains(plan.Changes, c => c.Path == "graphics/icons/g1-ammo.png");
        Assert.Contains(plan.Warnings, w => w.Contains("picture of your own"));
        BlockOperations.Apply(mod.Path, plan);
        Assert.Equal(mine, File.ReadAllBytes(mod.Combine("graphics", "icons", "g1.png")));
    }
}
