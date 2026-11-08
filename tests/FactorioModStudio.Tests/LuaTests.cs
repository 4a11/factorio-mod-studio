using FactorioModStudio.Core.Lua;
using Xunit;

namespace FactorioModStudio.Tests;

public class LuaTests
{
    private static LuaTable ParseTable(string source) =>
        Assert.IsType<LuaTable>(new LuaParser(LuaLexer.Tokenize(source)).ParseExpression());

    // --- lexer -------------------------------------------------------------

    [Fact]
    public void Lexer_SkipsCommentsAndTracksLines()
    {
        var tokens = LuaLexer.Tokenize("-- line comment\n--[[ block\ncomment ]] a\n--[==[ x ]] y ]==] b");
        Assert.Equal(new[] { "a", "b" }, tokens.Select(t => t.Text));
        Assert.Equal(new[] { 3, 4 }, tokens.Select(t => t.Line));
    }

    [Fact]
    public void Lexer_ReadsStringsWithEscapesAndLongStrings()
    {
        var tokens = LuaLexer.Tokenize("""
            x = "a\"b\n\65" y = 'it''s' z = [[
            long]]
            """);
        var strings = tokens.Where(t => t.Kind == TokenKind.String).Select(t => t.Text).ToList();
        Assert.Equal("a\"b\nA", strings[0]);
        Assert.Equal("it", strings[1]);
        Assert.Equal("long", strings.Last());
    }

    [Fact]
    public void Lexer_ReadsNumbers()
    {
        var numbers = LuaLexer.Tokenize("1 2.5 .5 1e3 2E-2 0xFF").Select(t => t.Text).ToList();
        Assert.Equal(new[] { "1", "2.5", ".5", "1e3", "2E-2", "0xFF" }, numbers);
        Assert.True(LuaLexer.TryParseNumber("0xFF", out var hex));
        Assert.Equal(255.0, hex);
    }

    [Fact]
    public void Lexer_DoesNotConfuseIndexingWithLongBrackets()
    {
        var tokens = LuaLexer.Tokenize("data.raw[\"a\"][\"b\"]");
        Assert.Equal(new[] { "data", ".", "raw", "[", "a", "]", "[", "b", "]" }, tokens.Select(t => t.Text));
    }

    [Fact]
    public void Lexer_SurvivesGarbage()
    {
        var tokens = LuaLexer.Tokenize("\"unterminated\n@#$ [[ never closed");
        Assert.NotEmpty(tokens);
    }

    // --- parser ------------------------------------------------------------

    [Fact]
    public void Parser_ReadsNamedPositionalAndKeyedFields()
    {
        var table = ParseTable("""{ type = "item", ["stack-size"] = 50, 7, "x", flag = true, nothing = nil, neg = -2 }""");

        Assert.Equal("item", table.String("type"));
        Assert.Equal(50.0, table.Number("stack-size"));
        Assert.Equal(-2.0, table.Number("neg"));
        Assert.Equal(2, table.Items.Count);
        Assert.IsType<LuaBool>(table.Fields["flag"]);
        Assert.IsType<LuaNil>(table.Fields["nothing"]);
    }

    [Fact]
    public void Parser_TurnsDynamicValuesIntoUnknownWithoutLosingNeighbours()
    {
        var table = ParseTable("""
            {
              name = "a" .. suffix,
              icon = util.path("x.png"),
              handler = function(e) if e then return {1, 2} end end,
              stack_size = 10,
              nested = { type = "item", name = "kept" },
            }
            """);

        Assert.IsType<LuaUnknown>(table.Fields["name"]);
        Assert.IsType<LuaUnknown>(table.Fields["icon"]);
        Assert.IsType<LuaUnknown>(table.Fields["handler"]);
        Assert.Equal(10.0, table.Number("stack_size"));
        Assert.Equal("kept", table.Table("nested")!.String("name"));
    }

    [Fact]
    public void Parser_HandlesNestedTablesAndSemicolons()
    {
        var table = ParseTable("{ {1, 2}; {3}; { a = { b = { c = \"deep\" } } } }");
        Assert.Equal(3, table.Items.Count);
        Assert.Equal("deep", ((LuaTable)((LuaTable)((LuaTable)table.Items[2]).Fields["a"]).Fields["b"]).String("c"));
    }

    [Fact]
    public void Parser_TerminatesOnMalformedInput()
    {
        var table = ParseTable("{ a = , = = 5 ) ] x");
        Assert.NotNull(table);
    }

    // --- prototype scanner ------------------------------------------------

    [Fact]
    public void Scanner_FindsPrototypesInExtendCalls()
    {
        var result = PrototypeScanner.Scan("""
            data:extend({
              { type = "item", name = "a", stack_size = 1 },
              { type = "recipe", name = "b", ingredients = {{type="item", name="iron-plate", amount=1}} },
            })
            data:extend{ { type = "fluid", name = "c" } }
            """);

        Assert.Equal(new[] { "a", "b", "c" }, result.Prototypes.Select(p => p.Name));
        Assert.Equal(new[] { 2, 3, 5 }, result.Prototypes.Select(p => p.Line));
    }

    [Fact]
    public void Scanner_DoesNotMistakeIngredientEntriesForPrototypes()
    {
        var result = PrototypeScanner.Scan("""
            local ingredients = { {type = "item", name = "iron-plate", amount = 2} }
            local single = {type = "fluid", name = "water", amount = 5}
            """);
        Assert.Empty(result.Prototypes);
    }

    [Fact]
    public void Scanner_FindsPrototypeAssignedToLocalOrReturned()
    {
        var result = PrototypeScanner.Scan("""
            local item = { type = "item", name = "from-local", stack_size = 1 }
            return { { type = "recipe", name = "from-return" } }
            """);
        Assert.Equal(new[] { "from-local", "from-return" }, result.Prototypes.Select(p => p.Name));
    }

    [Fact]
    public void Scanner_CountsUnresolvableExtendEntries()
    {
        var result = PrototypeScanner.Scan("""
            data:extend({ make_item("x"), { type = "item", name = "static" } })
            data:extend(generated)
            """);
        Assert.Single(result.Prototypes);
        Assert.Equal(2, result.DynamicEntries);
    }

    [Fact]
    public void Scanner_RecordsDataRawReferences()
    {
        var result = PrototypeScanner.Scan("""
            data.raw.recipe["iron-plate"].energy_required = 5
            data.raw["assembling-machine"]["assembling-machine-2"].speed = 1
            local x = data.raw[kind][name]
            """);
        Assert.Equal(new[] { "recipe/iron-plate", "assembling-machine/assembling-machine-2" },
            result.Touched.Select(t => $"{t.Type}/{t.Name}"));
    }

    [Fact]
    public void Scanner_IgnoresPrototypesInComments()
    {
        var result = PrototypeScanner.Scan("-- data:extend({ { type = \"item\", name = \"ghost\" } })\n--[[ x = { type=\"item\", name=\"ghost2\" } ]]");
        Assert.Empty(result.Prototypes);
    }

    [Fact]
    public void Scanner_FollowsTheCopiesOfBuildingBlocks()
    {
        var result = PrototypeScanner.Scan("""
            local copy = require("prototypes.fms-copy-v2")
            copy.use_icons({ ["my-chest"] = "__my-mod__/graphics/icons/my-chest.png" })

            local name = "my-chest"
            local chest = copy.entity("container", "iron-chest", name)
            chest.inventory_size = 48
            local gun = copy.item("submachine-gun", name .. "-gun", { r = 1 })

            data:extend({ chest, gun, copy.item("iron-chest", name, { r = 1 }, name), copy.recipe("iron-chest", name) })
            copy.unlock("logistics", name)
            """);

        Assert.Equal(new[] { "container/my-chest", "gun/my-chest-gun", "item/my-chest", "recipe/my-chest" },
            result.Prototypes.Select(p => $"{p.Type}/{p.Name}"));
        Assert.Equal(new[] { "iron-chest", "submachine-gun", "iron-chest", "iron-chest" }, result.Prototypes.Select(p => p.CopyOf));
        Assert.Equal(new[] { 5, 7, 9, 9 }, result.Prototypes.Select(p => p.Line));
        var chest = result.Prototypes[0].Table;
        Assert.Equal(48.0, chest.Number("inventory_size"));
        Assert.Equal("__my-mod__/graphics/icons/my-chest.png", chest.String("icon"));
        Assert.Equal("my-chest", result.Prototypes[2].Table.String("place_result"));
        Assert.Null(result.Prototypes[1].Table.String("icon")); // no icon file of its own: the original's icon, tinted
        Assert.Equal(0, result.DynamicEntries);
    }

    [Fact]
    public void Scanner_FollowsDeepcopiesOfDataRaw_AndStillRecordsWhatTheyRead()
    {
        var result = PrototypeScanner.Scan("""
            local name = "big-biter-2"
            local biter = table.deepcopy(data.raw.unit["small-biter"])
            biter.name = name
            biter.max_health = 75
            local unused = table.deepcopy(data.raw.item["iron-plate"])
            data:extend({ biter })
            """);

        var biter = Assert.Single(result.Prototypes);
        Assert.Equal(("unit", "big-biter-2", "small-biter", 2), (biter.Type, biter.Name, biter.CopyOf, biter.Line));
        Assert.Equal(75.0, biter.Table.Number("max_health"));
        Assert.Equal(new[] { "unit/small-biter", "item/iron-plate" }, result.Touched.Select(t => $"{t.Type}/{t.Name}"));
    }

    [Fact]
    public void Scanner_ResolvesLocalStrings_AndListsALocalPrototypeOnce()
    {
        var result = PrototypeScanner.Scan("""
            local name = "machine"
            local category = name .. "-crafting"
            local example = { type = "recipe", name = name .. "-gears", ingredients = {} }
            data:extend({ { type = "recipe-category", name = category }, example })
            """);

        Assert.Equal(new[] { "machine-gears", "machine-crafting" }, result.Prototypes.Select(p => p.Name));
        Assert.All(result.Prototypes, p => Assert.Null(p.CopyOf));
        Assert.Equal(0, result.DynamicEntries);
    }

    [Fact]
    public void Scanner_StillLooksInsideExpressionsItCannotRead()
    {
        var result = PrototypeScanner.Scan("""
            local fix = make(function() data.raw.recipe["iron-plate"].enabled = true end)
            local other = helper.copy("x")
            other.name = "not-a-prototype"
            data:extend({ other })
            """);

        Assert.Equal(new[] { "recipe/iron-plate" }, result.Touched.Select(t => $"{t.Type}/{t.Name}"));
        Assert.Empty(result.Prototypes);
        Assert.Equal(1, result.DynamicEntries);
    }

    [Fact]
    public void Scanner_KnowsTheHelpersOnlyFromTheirRequire()
    {
        var result = PrototypeScanner.Scan("""
            local copy = require("my-own-helpers")
            data:extend({ copy.item("iron-chest", "x") })
            """);

        Assert.Empty(result.Prototypes);
        Assert.Equal(1, result.DynamicEntries);
    }
}
