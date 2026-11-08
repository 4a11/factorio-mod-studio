using System.Text.Json.Nodes;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>The properties panel of building blocks: reading the numbers of a block, the originals, writing values back.</summary>
public class BlockPropertiesTests
{
    private static (TempDir Mod, string Source) Block(string id, string name)
    {
        var mod = new TempDir();
        mod.Write("info.json", """{ "name": "my-mod", "version": "0.1.0", "title": "My mod", "factorio_version": "2.1" }""");
        mod.Write("data.lua", "-- data\n");
        ModContent.Apply(mod.Path, new ContentRequest(id, name, "Mine"));
        return (mod, File.ReadAllText(mod.Combine("prototypes", name + ".lua")));
    }

    [Fact]
    public void Read_FindsTheNumbersOfABlock_WithTheirPrototypeAndComment()
    {
        var (mod, source) = Block("chest", "my-chest");
        using var _ = mod;

        Assert.True(BlockProperties.IsBlock(source));
        var block = BlockProperties.Read("prototypes/my-chest.lua", source);

        Assert.Equal("Mine", block.Title);
        Assert.Equal(new[] { "inventory_size=48", "max_health=300" }, block.Properties.Select(p => $"{p.Path}={p.Value}"));
        var slots = block.Properties[0];
        Assert.Equal(("value", "number", "container", "my-chest", "iron-chest"), (slots.Kind, slots.ValueType, slots.PrototypeType, slots.PrototypeName, slots.CopyOf));
        Assert.Equal("the iron chest holds 32 stacks", slots.Comment);
    }

    [Fact]
    public void Read_KnowsFactorsDamageAndNumbersKeptInLocals()
    {
        var (belt, beltSource) = Block("belt", "fast-belt");
        using var a = belt;
        var speed = BlockProperties.Read("b.lua", beltSource).Properties.Single(p => p.Path == "speed");
        Assert.Equal(("0.125", "fast-belt"), (speed.Value, speed.PrototypeName));
        Assert.Equal(new[] { "fast-belt-underground-belt", "fast-belt-splitter" }, speed.AlsoFor);

        var (inserter, inserterSource) = Block("inserter", "quick");
        using var b = inserter;
        Assert.Equal(new[] { "multiplier rotation_speed 1.5", "multiplier extension_speed 1.5" },
            BlockProperties.Read("i.lua", inserterSource).Properties.Select(p => $"{p.Kind} {p.Path} {p.Value}"));

        var (grenade, grenadeSource) = Block("grenade", "boom");
        using var c = grenade;
        var damage = Assert.Single(BlockProperties.Read("g.lua", grenadeSource).Properties);
        Assert.Equal(("damage", "2", "projectile"), (damage.Kind, damage.Value, damage.PrototypeType));
    }

    [Fact]
    public void Apply_ReplacesOnlyTheLiterals()
    {
        var (mod, source) = Block("solar", "sun");
        using var _ = mod;
        var block = BlockProperties.Read("s.lua", source);
        var power = block.Properties.Single(p => p.Path == "production");
        var capacity = block.Properties.Single(p => p.Path == "energy_source.buffer_capacity");

        var updated = BlockProperties.Apply(source, new Dictionary<string, string> { [power.Key] = "150kW", [capacity.Key] = "20MJ" });

        Assert.Contains("panel.production = \"150kW\"", updated);
        Assert.Contains("accumulator.energy_source.buffer_capacity = \"20MJ\"", updated);
        Assert.Equal(source.Replace("\"100kW\"", "\"150kW\"").Replace("\"10MJ\"", "\"20MJ\""), updated);
        Assert.Equal(new[] { "150kW", "20MJ" }, BlockProperties.Read("s.lua", updated).Properties.Select(p => p.Value));
    }

    [Fact]
    public void Apply_RefusesBadValuesAndUnknownKeys()
    {
        var (mod, source) = Block("wall", "w");
        using var _ = mod;
        var health = BlockProperties.Read("w.lua", source).Properties.Single();

        Assert.Throws<FmsException>(() => BlockProperties.Apply(source, new Dictionary<string, string> { [health.Key] = "lots" }));
        Assert.Throws<FmsException>(() => BlockProperties.Apply(source, new Dictionary<string, string> { [health.Key] = "1e999" }));
        Assert.Throws<FmsException>(() => BlockProperties.Apply(source, new Dictionary<string, string> { ["99:x.y:value"] = "1" }));
        Assert.Contains("wall.max_health = 1000", BlockProperties.Apply(source, new Dictionary<string, string> { [health.Key] = " 1000 " }));

        var (armor, armorSource) = Block("armor", "a");
        using var b = armor;
        var grid = BlockProperties.Read("a.lua", armorSource).Properties.Single(p => p.Path == "equipment_grid");
        Assert.Throws<FmsException>(() => BlockProperties.Apply(armorSource, new Dictionary<string, string> { [grid.Key] = "x\" .. os.exit() .. \"" }));
    }

    [Fact]
    public void WithGameData_AddsTheOriginalAndTheValueInTheGame()
    {
        var (mod, source) = Block("inserter", "quick");
        using var _ = mod;
        var value = JsonNode.Parse("""{ "type": "inserter", "name": "quick", "rotation_speed": 0.06, "extension_speed": 0.15 }""");
        var report = new GameDataReport
        {
            Items = new[]
            {
                new GameDataItem("inserter", "quick", "added", value, new[] { new GameDataField("rotation_speed", JsonValue.Create(0.04), JsonValue.Create(0.06)) }, "fast-inserter")
            }
        };

        var block = BlockProperties.WithGameData(BlockProperties.Read("i.lua", source), report);

        var rotation = block.Properties.Single(p => p.Path == "rotation_speed");
        Assert.Equal((0.04, 0.06), (rotation.Original!.GetValue<double>(), rotation.Game!.GetValue<double>()));
        var extension = block.Properties.Single(p => p.Path == "extension_speed"); // not among the differences: as the original
        Assert.Equal((0.15, 0.15), (extension.Original!.GetValue<double>(), extension.Game!.GetValue<double>()));
        Assert.Same(block, BlockProperties.WithGameData(block, null));
    }

    [Fact]
    public void IsBlock_NeedsTheHeaderAndTheHelpers()
    {
        Assert.False(BlockProperties.IsBlock("local x = 1\n"));
        Assert.False(BlockProperties.IsBlock("-- Thing (x), added by Factorio Mod Studio.\nlocal x = 1\n"));
        Assert.True(BlockProperties.IsBlock("-- Thing (x), added by Factorio Mod Studio.\nlocal copy = require(\"prototypes.fms-copy-v1\")\n"));
        Assert.Equal("Thing", BlockProperties.TitleOf("-- Thing (x), added by Factorio Mod Studio.\n"));
    }
}
