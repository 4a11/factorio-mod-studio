using System.Text.Json;
using System.Text.Json.Nodes;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>The --dump-data comparison: what a mod adds, changes and removes, copies against their originals, the run itself.</summary>
public class GameDataDumpTests
{
    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    private const string Baseline = """
        {
          "item": {
            "iron-plate": { "type": "item", "name": "iron-plate", "stack_size": 100, "icons": [ { "icon": "a.png" } ] },
            "old-thing": { "type": "item", "name": "old-thing", "stack_size": 1 }
          },
          "capsule": { "grenade": { "type": "capsule", "name": "grenade", "stack_size": 100, "capsule_action": { "type": "throw" } } },
          "container": { "iron-chest": { "type": "container", "name": "iron-chest", "inventory_size": 32, "max_health": 200 } }
        }
        """;

    private const string WithMod = """
        {
          "item": {
            "iron-plate": { "type": "item", "name": "iron-plate", "stack_size": 200, "icons": [ { "icon": "b.png" } ], "fuel_value": "1MJ" }
          },
          "capsule": {
            "grenade": { "type": "capsule", "name": "grenade", "stack_size": 100, "capsule_action": { "type": "throw" } },
            "big-grenade": { "type": "capsule", "name": "big-grenade", "stack_size": 50, "capsule_action": { "type": "throw" } }
          },
          "container": {
            "iron-chest": { "type": "container", "name": "iron-chest", "inventory_size": 32, "max_health": 200 },
            "my-chest": { "type": "container", "name": "my-chest", "inventory_size": 48, "max_health": 200 }
          }
        }
        """;

    [Fact]
    public void Compare_ListsWhatTheModAddsChangesAndRemoves()
    {
        using var before = Doc(Baseline);
        using var after = Doc(WithMod);

        var items = GameDataDump.Compare(before, after);

        Assert.Equal(new[] { "added capsule/big-grenade", "added container/my-chest", "changed item/iron-plate", "removed item/old-thing" },
            items.Select(i => $"{i.Change} {i.Type}/{i.Name}"));
        var plate = items.Single(i => i.Name == "iron-plate");
        Assert.Equal(new[] { "stack_size", "icons[0].icon", "fuel_value" }, plate.Fields.Select(f => f.Path));
        Assert.Equal("100", plate.Fields[0].Before!.ToJsonString());
        Assert.Equal("200", plate.Fields[0].After!.ToJsonString());
        Assert.Null(plate.Fields[2].Before);
        Assert.Equal(200, plate.Value!["stack_size"]!.GetValue<int>());
        Assert.Null(items.Single(i => i.Change == "removed").Value);
        Assert.Empty(items.Single(i => i.Name == "my-chest").Fields); // added, and nothing says what it copies
    }

    [Fact]
    public void Compare_ComparesACopyWithItsOriginal()
    {
        using var before = Doc(Baseline);
        using var after = Doc(WithMod);
        var copies = new Dictionary<string, string> { ["container/my-chest"] = "iron-chest", ["capsule/big-grenade"] = "grenade", ["item/missing"] = "nope" };

        var items = GameDataDump.Compare(before, after, copies);

        var chest = items.Single(i => i.Name == "my-chest");
        Assert.Equal("iron-chest", chest.CopyOf);
        var field = Assert.Single(chest.Fields); // the name is left out: of course a copy has another one
        Assert.Equal(("inventory_size", "32", "48"), (field.Path, field.Before!.ToJsonString(), field.After!.ToJsonString()));
        var grenade = items.Single(i => i.Name == "big-grenade");
        Assert.Equal(("grenade", "stack_size"), (grenade.CopyOf, Assert.Single(grenade.Fields).Path));
    }

    [Fact]
    public void Differences_CompareListsOfTheSameLengthItemByItem_AndStopAtTheLimit()
    {
        using var a = Doc("""{ "list": [1, 2, 3], "other": [1, 2], "deep": { "x": { "y": 1 } } }""");
        using var b = Doc("""{ "list": [1, 5, 3], "other": [1, 2, 3], "deep": { "x": { "y": 2 } } }""");
        var fields = GameDataDump.Differences(a.RootElement, b.RootElement, out var truncated);
        Assert.Equal(new[] { "list[1]", "other", "deep.x.y" }, fields.Select(f => f.Path));
        Assert.False(truncated);

        var many = new JsonObject();
        for (var i = 0; i < GameDataDump.MaxFields + 10; i++) many[$"f{i}"] = i;
        using var empty = Doc("{}");
        using var full = Doc(many.ToJsonString());
        Assert.Equal(GameDataDump.MaxFields, GameDataDump.Differences(empty.RootElement, full.RootElement, out truncated).Count);
        Assert.True(truncated);
    }

    [Fact]
    public void SetEnabled_SwitchesAModAndAddsAMissingOne()
    {
        using var dir = new TempDir();
        var list = dir.Write("mod-list.json", """{ "mods": [ { "name": "base", "enabled": true }, { "name": "my-mod", "enabled": true } ] }""");

        GameDataDump.SetEnabled(list, "my-mod", false);
        GameDataDump.SetEnabled(list, "other", true);

        var mods = JsonNode.Parse(File.ReadAllText(list))!["mods"]!.AsArray();
        Assert.Equal(new[] { "base:True", "my-mod:False", "other:True" }, mods.Select(m => $"{m!["name"]}:{m["enabled"]}"));
    }

    [Fact]
    public void Fingerprint_ChangesWithTheModsFiles()
    {
        using var mod = Fixtures.ValidMod();
        var first = GameDataDump.Fingerprint(mod.Path);
        Assert.Equal(first, GameDataDump.Fingerprint(mod.Path));

        mod.Write("data.lua", "data:extend({ { type = \"item\", name = \"x\", stack_size = 1 } })\n-- longer now\n");
        Assert.NotEqual(first, GameDataDump.Fingerprint(mod.Path));
    }

    [Fact]
    public void Dump_GivesTheGameAConfigOfItsOwn_AndFindsTheDump()
    {
        using var game = new TempDir();
        var exe = game.Write("bin/x64/factorio.exe", "");
        Directory.CreateDirectory(game.Combine("data", "base"));
        using var work = new TempDir();
        var runner = new DumpingRunner("""{ "item": {} }""");

        var (process, dump) = GameDataDump.Dump(exe, work.Combine("mods"), work.Combine("dump"), runner, TimeSpan.FromMinutes(1));

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(work.Combine("dump", "write", "script-output", GameDataDump.DumpFileName), dump);
        Assert.Equal(new[] { "--config", work.Combine("dump", "config.ini"), "--mod-directory", work.Combine("mods"), "--dump-data" }, runner.Arguments);
        var config = File.ReadAllText(work.Combine("dump", "config.ini"));
        Assert.Contains($"read-data={Path.GetFullPath(game.Combine("data"))}\n", config);
        Assert.Contains($"write-data={work.Combine("dump", "write")}\n", config);
    }

    [Fact]
    public void Dump_ReportsNoFileWhenTheGameWroteNone_AndFailsWithoutDataFolder()
    {
        using var game = new TempDir();
        var exe = game.Write("bin/x64/factorio.exe", "");
        using var work = new TempDir();
        Assert.Throws<FmsException>(() => GameDataDump.Dump(exe, work.Path, work.Combine("dump"), new DumpingRunner(null), TimeSpan.FromMinutes(1)));

        Directory.CreateDirectory(game.Combine("data", "base"));
        var (_, dump) = GameDataDump.Dump(exe, work.Path, work.Combine("dump"), new DumpingRunner(null), TimeSpan.FromMinutes(1));
        Assert.Null(dump);
    }

    [Fact]
    public void Report_SurvivesSaveAndLoad()
    {
        using var dir = new TempDir();
        using var before = Doc(Baseline);
        using var after = Doc(WithMod);
        var report = new GameDataReport { ModName = "m", GameVersion = "2.1.24", Fingerprint = "abc", Mods = new[] { "base", "m" }, Items = GameDataDump.Compare(before, after) };
        var path = dir.Combine("r.json");

        GameDataDump.Save(report, path);
        var loaded = GameDataDump.Load(path)!;

        Assert.Equal(("m", "2.1.24", "abc", 4), (loaded.ModName, loaded.GameVersion, loaded.Fingerprint, loaded.Items.Count));
        Assert.Equal("200", loaded.Items.Single(i => i.Name == "iron-plate").Fields[0].After!.ToJsonString());
        Assert.Null(GameDataDump.Load(dir.Combine("missing.json")));
    }

    /// <summary>Stands in for Factorio: reads write-data from the config it is given and writes the dump there.</summary>
    private sealed class DumpingRunner(string? dump) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine = null)
        {
            Arguments = arguments;
            var config = File.ReadAllLines(arguments[1]);
            var write = config.Single(l => l.StartsWith("write-data=", StringComparison.Ordinal))["write-data=".Length..];
            if (dump is not null)
            {
                Directory.CreateDirectory(Path.Combine(write, "script-output"));
                File.WriteAllText(Path.Combine(write, "script-output", GameDataDump.DumpFileName), dump);
            }
            return new ProcessResult(0, false, new[] { "   9.998 Goodbye" });
        }
    }
}
