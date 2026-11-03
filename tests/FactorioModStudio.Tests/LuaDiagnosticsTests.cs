using System.Diagnostics;
using System.Text;
using FactorioModStudio.Core.Lua;
using Xunit;

namespace FactorioModStudio.Tests;

public class LuaDiagnosticsTests
{
    private static void Fail(string message) => Assert.True(false, message);

    private static string Fmt(IReadOnlyList<LuaDiagnostic> d) =>
        string.Join(", ", d.Select(x => $"{x.Code}@{x.Line}:{x.Column}"));

    private static string Codes(string text) => Fmt(LuaDiagnostics.Check(text));

    private static void Clean(string text)
    {
        var d = LuaDiagnostics.Check(text);
        if (d.Count != 0)
            Fail($"Expected no diagnostics but got [{Fmt(d)}] ({string.Join(" | ", d.Select(x => x.Message))}) for:\n{text}");
    }

    private static LuaDiagnostic One(string text)
    {
        var d = LuaDiagnostics.Check(text);
        if (d.Count != 1) Fail($"Expected exactly 1 diagnostic, got [{Fmt(d)}] for:\n{text}");
        return d[0];
    }

    // --- basics -------------------------------------------------------------

    [Fact]
    public void Empty_And_Whitespace_AreClean()
    {
        Clean("");
        Clean("   \n\t\r\n  ");
        Clean("-- only a comment");
    }

    [Fact]
    public void NullInput_DoesNotThrow()
    {
        var r = LuaDiagnostics.Check(null!);
        Assert.NotNull(r);
    }

    [Fact]
    public void AllDiagnostics_AreErrors_WithMessage_AndOneBasedPositions()
    {
        var d = LuaDiagnostics.Check("x = \"abc\nfunction f() \n(");
        Assert.NotEmpty(d);
        foreach (var x in d)
        {
            Assert.Equal("error", x.Severity);
            Assert.True(x.Line >= 1 && x.Column >= 1, "positions are 1-based");
            Assert.True(!string.IsNullOrWhiteSpace(x.Message));
            Assert.StartsWith("LUA00", x.Code);
        }
    }

    // --- LUA001 -------------------------------------------------------------

    [Theory]
    [InlineData("x = \"abc", 1, 5)]
    [InlineData("x = 'abc", 1, 5)]
    [InlineData("a = 1\nx = \"abc\ny = 2", 2, 5)]
    [InlineData("\"", 1, 1)]
    [InlineData("  'it", 1, 3)]
    public void Lua001_UnterminatedString_AtOpeningQuote(string text, int line, int col)
    {
        var d = One(text);
        Assert.Equal("LUA001", d.Code);
        Assert.Equal(line, d.Line);
        Assert.Equal(col, d.Column);
    }

    [Fact]
    public void Lua001_ReportedAtNewline_AndScanningContinuesOnNextLine()
    {
        // string broken by newline; the following line is valid code and must not be swallowed
        var d = LuaDiagnostics.Check("x = \"abc\nif y then z() end");
        Assert.Equal("LUA001@1:5", Fmt(d));
    }

    [Fact]
    public void Lua001_EscapedQuoteAtEnd_IsStillUnterminated()
    {
        Assert.Equal("LUA001@1:5", Codes("x = \"abc\\\""));
        Assert.Equal("LUA001@1:5", Codes("x = 'abc\\'"));
    }

    [Fact]
    public void Lua001_BackslashAtEof_IsUnterminated()
    {
        Assert.Equal("LUA001@1:5", Codes("x = \"abc\\"));
    }

    [Fact]
    public void Lua001_DoesNotAlsoReportBracketsInsideBrokenString()
    {
        // The rest of the broken line is part of the (unterminated) string.
        Assert.Equal("LUA001@1:5", Codes("x = \"abc ( [ {\n"));
    }

    // --- strings: valid traps -----------------------------------------------

    [Theory]
    [InlineData("x = \"end\"")]
    [InlineData("x = 'function ('")]
    [InlineData("x = \"a\\\"b end (\"")]
    [InlineData("x = 'a\\'b ['")]
    [InlineData("x = \"back\\\\\" -- slash then close")]
    [InlineData("x = \"a\\\\\\\"b\"")]
    [InlineData("x = \"tab\\tnl\\n \\65 \\x41 \\u{48} (\"")]
    [InlineData("x = \"a\\z   \n   b\"")]
    [InlineData("x = \"line1\\\nline2 end (\"")]
    [InlineData("x = \"line1\\\r\nline2 )\"")]
    [InlineData("x = \"]]\"")]
    [InlineData("x = '--[['")]
    [InlineData("x = \"-- not a comment (\"")]
    [InlineData("x = \"[[ not long\"")]
    [InlineData("x = '\"' .. \"'\"")]
    public void ValidStrings_ProduceNothing(string text) => Clean(text);

    // --- LUA002 -------------------------------------------------------------

    [Theory]
    [InlineData("x = [[abc", 1, 5)]
    [InlineData("x = [==[abc ]] ]=] ", 1, 5)]
    [InlineData("--[[ never closed", 1, 1)]
    [InlineData("a = 1\n  --[==[ x ]=] ]]", 2, 3)]
    [InlineData("x = [[\nline\nline", 1, 5)]
    [InlineData("[=[", 1, 1)]
    public void Lua002_UnterminatedLongStringOrComment_AtOpener(string text, int line, int col)
    {
        var d = One(text);
        Assert.Equal("LUA002", d.Code);
        Assert.Equal(line, d.Line);
        Assert.Equal(col, d.Column);
    }

    [Theory]
    [InlineData("x = [[ end ( ]]")]
    [InlineData("x = [==[ ]] ) end ]==]")]
    [InlineData("x = [=[ ]] ]==] ]=]")]
    [InlineData("--[[ end ( function ]]")]
    [InlineData("--[==[ ]] end ]==] x = 1")]
    [InlineData("--[[\nmulti\nline end\n]] x = 1")]
    [InlineData("x = [[\nfirst newline skipped ]]")]
    [InlineData("-- [[ not long comment, just line comment ( \nx = 1")]
    [InlineData("--[ not long ( \nx = 1")]
    [InlineData("--[=x ( \nx = 1")]
    [InlineData("--[==\nx = 1")]
    [InlineData("---[[ line comment because of third dash (\nx = 1")]
    [InlineData("--\nx = 1")]
    public void ValidLongBrackets_AndComments(string text) => Clean(text);

    [Fact]
    public void LongBracket_WithSingleOpenBracketThenName_IsIndexNotLongString()
    {
        Clean("local t = {} t[1] = 2 t[ [[k]] ] = 3 local y = t[t[1]]");
    }

    // --- LUA003 -------------------------------------------------------------

    [Theory]
    [InlineData(")", 1, 1)]
    [InlineData("x = 1 ]", 1, 7)]
    [InlineData("a\n  }", 2, 3)]
    [InlineData("f(a))", 1, 5)]
    public void Lua003_UnmatchedCloser_AtCloser(string text, int line, int col)
    {
        var d = One(text);
        Assert.Equal("LUA003", d.Code);
        Assert.Equal(line, d.Line);
        Assert.Equal(col, d.Column);
    }

    [Theory]
    [InlineData("f(", 1, 2)]
    [InlineData("x = {", 1, 5)]
    [InlineData("x = a[", 1, 6)]
    [InlineData("y = 1\nz = (1,\n", 2, 5)]
    public void Lua003_UnclosedOpener_AtEof_PositionsOpener(string text, int line, int col)
    {
        var d = One(text);
        Assert.Equal("LUA003", d.Code);
        Assert.Equal(line, d.Line);
        Assert.Equal(col, d.Column);
    }

    [Fact]
    public void Lua003_Mismatch_ReportedAtCloser()
    {
        var d = LuaDiagnostics.Check("x = (1]");
        Assert.NotEmpty(d);
        Assert.Equal("LUA003", d[0].Code);
        Assert.Equal(1, d[0].Line);
        Assert.True(d.Any(x => x.Column == 7), "closer at col 7 must be flagged: " + Fmt(d));
        Assert.True(d.All(x => x.Code == "LUA003"));
    }

    [Fact]
    public void Lua003_MismatchBraceVsParen()
    {
        var d = LuaDiagnostics.Check("t = { a = (1 }");
        Assert.NotEmpty(d);
        // Either the mismatched closer (col 14) or the unclosed '(' (col 11) is a defensible report.
        Assert.True(d.Any(x => x.Code == "LUA003" && x.Line == 1 && (x.Column == 14 || x.Column == 11)), Fmt(d));
    }

    [Fact]
    public void Lua003_MultipleUnclosedOpeners_AreAllReported_InOrder()
    {
        var d = LuaDiagnostics.Check("a({\n[");
        // three unclosed openers: ( { [
        Assert.Equal(3, d.Count);
        Assert.True(d.All(x => x.Code == "LUA003"));
        Assert.Equal(new[] { 1, 1, 2 }, d.Select(x => x.Line));
    }

    [Fact]
    public void Brackets_InsideStringsAndComments_Ignored()
    {
        Clean("x = \"(\" -- )\ny = '}' --[[ ] ]]\nz = [[ ) ]]");
    }

    // --- LUA004 -------------------------------------------------------------

    [Theory]
    [InlineData("end", 1, 1)]
    [InlineData("x = 1\n   end", 2, 4)]
    [InlineData("until x", 1, 1)]
    [InlineData("else", 1, 1)]
    [InlineData("elseif x then", 1, 1)]
    [InlineData("if a then b() end end", 1, 19)]
    [InlineData("function f() end\nend", 2, 1)]
    public void Lua004_UnexpectedKeyword(string text, int line, int col)
    {
        var d = LuaDiagnostics.Check(text);
        Assert.NotEmpty(d);
        var first = d.First(x => x.Code == "LUA004");
        Assert.Equal(line, first.Line);
        Assert.Equal(col, first.Column);
    }

    [Fact]
    public void Lua004_ElseOutsideIf_InsideOtherBlock()
    {
        // 'else' inside a function/while/for/do/repeat that has no enclosing if is invalid
        foreach (var src in new[]
        {
            "function f() else end",
            "while x do else end",
            "for i = 1, 2 do else end",
            "do else end",
            "repeat else until x",
            "function f() elseif x then end",
        })
        {
            var d = LuaDiagnostics.Check(src);
            Assert.True(d.Any(x => x.Code == "LUA004"), $"expected LUA004 for: {src} -> [{Fmt(d)}]");
        }
    }

    [Fact]
    public void Lua004_UntilWithoutRepeat_AndEndWithRepeat()
    {
        Assert.True(LuaDiagnostics.Check("while x do until y end").Any(x => x.Code == "LUA004"));
        // 'end' cannot close a repeat
        var d = LuaDiagnostics.Check("repeat x() end");
        Assert.NotEmpty(d);
        Assert.True(d.Any(x => x.Code == "LUA004" || x.Code == "LUA005"), Fmt(d));
    }

    [Fact]
    public void Lua004_ExtraEnd_AfterBalancedCode_DoesNotMaskOtherErrors()
    {
        var d = LuaDiagnostics.Check("if a then b() end\nend\nx = \"oops");
        Assert.Equal("LUA004@2:1, LUA001@3:5", Fmt(d));
    }

    // --- LUA005 -------------------------------------------------------------

    [Theory]
    [InlineData("function f()", 1, 1, "function", "end")]
    [InlineData("local function f()\n  x()", 1, 7, "function", "end")]
    [InlineData("if x then", 1, 1, "if", "end")]
    [InlineData("for i = 1, 3 do", 1, 1, "for", "end")]
    [InlineData("while true do", 1, 1, "while", "end")]
    [InlineData("do", 1, 1, "do", "end")]
    [InlineData("repeat\n  x()", 1, 1, "repeat", "until")]
    [InlineData("x = function() return 1", 1, 5, "function", "end")]
    public void Lua005_UnclosedBlock_AtOpener_MessageNamesKeywordAndCloser(string text, int line, int col, string keyword, string closer)
    {
        var d = One(text);
        Assert.Equal("LUA005", d.Code);
        Assert.Equal(line, d.Line);
        Assert.Equal(col, d.Column);
        Assert.Contains(keyword, d.Message);
        Assert.Contains(closer, d.Message);
    }

    [Fact]
    public void Lua005_ForAndWhile_OwnTheirDo_NotDoubleCounted()
    {
        Clean("for i = 1, 3 do x() end");
        Clean("for k, v in pairs(t) do x() end");
        Clean("while x do y() end");
        Clean("while x do\n  for i = 1, 2 do\n    while y do z() end\n  end\nend");
        // one missing 'end' -> exactly one diagnostic, not two
        var d = LuaDiagnostics.Check("for i = 1, 3 do x()");
        Assert.Equal(1, d.Count);
    }

    [Fact]
    public void Lua005_StandaloneDo_IsBlock()
    {
        Clean("do local x = 1 end");
        Clean("do do do end end end");
        Assert.Equal("LUA005@1:1", Codes("do do end"));
    }

    [Fact]
    public void Lua005_IfElseifElse_SingleEnd()
    {
        Clean("if a then x() elseif b then y() elseif c then z() else w() end");
        Clean("if a then if b then x() else y() end else z() end");
        Assert.Equal("LUA005@1:1", Codes("if a then x() elseif b then y() else z()"));
    }

    [Fact]
    public void Lua005_MultipleUnclosed_ReportedInSourceOrder()
    {
        var d = LuaDiagnostics.Check("function a()\n  if x then\n    for i=1,2 do\n");
        Assert.Equal(3, d.Count);
        Assert.True(d.All(x => x.Code == "LUA005"));
        Assert.Equal(new[] { 1, 2, 3 }, d.Select(x => x.Line));
    }

    [Fact]
    public void Lua005_RepeatUntil_AndNesting()
    {
        Clean("repeat x = x + 1 until x > 3");
        Clean("repeat if a then b() end until c");
        Clean("repeat repeat x() until a until b");
        Clean("repeat local f = function() return 1 end until f()");
    }

    [Fact]
    public void FunctionAsExpression_NeedsEnd()
    {
        Clean("local f = function(a, b) return a + b end");
        Clean("t = { f = function() end, g = function() return 1 end }");
        Clean("foo(function() end, function() end)");
        Assert.Equal("LUA005@1:11", Codes("local f = function()"));
    }

    // --- tokens that must not confuse the scanner ----------------------------

    [Fact]
    public void LabelsGotoNumbers_AreHarmless()
    {
        Clean("::top::\nfor i = 1, 3 do\n  if i == 2 then goto continue end\n  x(i)\n  ::continue::\nend\ngoto top");
        Clean("x = 0x1F + 0Xff + 1e-5 + 1E+5 + 3..2 + .5 + 5. + 0x.8p1");
        Clean("x = a..b..c");
        Clean("x = 1 .. 2");
    }

    [Fact]
    public void KeywordsAsFieldNames_AreNotBlocks()
    {
        // These are not valid Lua keywords usage, but `x.end` isn't legal anyway; we only assert
        // identifiers that merely CONTAIN keywords are not treated as keywords.
        Clean("local endx, myend, end_, _end, ifx, dofoo, functional, forever, whilex, repeated, untilx, elsewhere = 1");
        Clean("x.endpoint = 1 y.forEach = 2 z.do_it() w:format()");
        Clean("local ifelse, elseif_, function_, end2 = 1");
    }

    [Fact]
    public void ExactPrefixKeywords_InIdentifiersWithDigitsOrUnderscore()
    {
        Clean("local if1, for2, do3, end_x, function4, repeat5, until6, while7 = 1");
        Clean("local _function, _end, _if, _do = 1");
    }

    // --- realistic Factorio code: zero diagnostics ----------------------------

    private const string DataLua = """
        local util = require("__core__/lualib/util")
        local item_sounds = require("__base__/prototypes/item_sounds")

        data:extend({
          {
            type = "item",
            name = "my-item",
            icon = "__my-mod__/graphics/icons/my-item.png",
            icon_size = 64,
            subgroup = "intermediate-product",
            order = "a[my-item]",
            stack_size = 100,
            weight = 1000 / 100,
          },
          {
            type = "recipe",
            name = "my-item",
            enabled = false,
            energy_required = 0.5,
            ingredients = {
              { type = "item", name = "iron-plate", amount = 2 },
              { type = "item", name = "copper-plate", amount = 1 },
            },
            results = { { type = "item", name = "my-item", amount = 1 } },
          },
          {
            type = "technology",
            name = "my-tech",
            icon = "__my-mod__/graphics/technology/my-tech.png",
            effects = { { type = "unlock-recipe", recipe = "my-item" } },
            prerequisites = { "automation" },
            unit = {
              count = 100,
              ingredients = { { "automation-science-pack", 1 } },
              time = 15
            },
            order = "c-a"
          },
        })

        local furnace = util.table.deepcopy(data.raw["furnace"]["stone-furnace"])
        furnace.name = "my-furnace"
        furnace.minable.result = "my-furnace"
        furnace.crafting_speed = 2.5e0
        furnace.icons = { { icon = furnace.icon, tint = { r = 1, g = 0.5, b = 0, a = 1 } } }
        data.raw["furnace"]["my-furnace"] = furnace

        for _, proto in pairs(data.raw["assembling-machine"]) do
          if proto.name:find("^my%-") then
            proto.energy_usage = "100kW"
          elseif proto.crafting_speed and proto.crafting_speed > 1 then
            proto.crafting_speed = proto.crafting_speed * 0x2
          else
            -- end of the line: nothing to do (end)
          end
        end
        """;

    private const string ControlLua = """
        local mod_gui = require("mod-gui")
        local handlers = {}

        local function get_player_data(player_index)
          storage.players = storage.players or {}
          local pd = storage.players[player_index]
          if not pd then
            pd = { clicks = 0, last_tick = 0 }
            storage.players[player_index] = pd
          end
          return pd
        end

        script.on_init(function()
          storage.counter = 0
          for _, player in pairs(game.players) do
            get_player_data(player.index)
          end
        end)

        script.on_event(defines.events.on_tick, function(event)
          if event.tick % 60 ~= 0 then return end
          storage.counter = storage.counter + 1
          local i = 1
          while true do
            i = i + 1
            if i > 10 then break end
          end
          repeat
            i = i - 1
          until i <= 0
        end)

        script.on_event(defines.events.on_gui_click, function(event)
          local element = event.element
          if not (element and element.valid) then return end
          if element.name == "my_button" then
            game.get_player(event.player_index).print("Clicked: ]] and [[ in a string, not a long bracket")
            local msg = "block end ) ] }"
            log(msg)
          elseif element.name == "other" then
            -- the word end in a comment, and function( too
            --[[ multi-line comment with end
                 and until and ( brackets ]]
            local text = [==[ a long string with ]] and end inside ]==]
            handlers[#handlers + 1] = function() return text end
          else
            do
              local scoped = 1
            end
          end
        end)

        local function make_counter()
          local n = 0
          return function()
            n = n + 1
            return n
          end
        end

        remote.add_interface("my_mod", {
          get_counter = function() return storage.counter end,
          reset = function()
            storage.counter = 0
          end,
        })

        commands.add_command("hello", "says hi", function(cmd)
          local ok, err = pcall(function()
            game.print({ "", "[color=red]", { "my-mod.hello", cmd.parameter or "" }, "[/color]" })
          end)
          if not ok then log(serpent.block(err)) end
        end)

        return {
          make_counter = make_counter,
          nested = { a = { b = { c = { d = function() return 1 end } } } },
          ["quoted key"] = 'single \'quoted\' value',
        }
        """;

    [Fact]
    public void Factorio_DataLua_Sample_IsClean() => Clean(DataLua);

    [Fact]
    public void Factorio_ControlLua_Sample_IsClean() => Clean(ControlLua);

    [Fact]
    public void Factorio_SampleWithCrlf_IsClean()
    {
        Clean(DataLua.Replace("\n", "\r\n"));
        Clean(ControlLua.Replace("\n", "\r\n"));
    }

    [Fact]
    public void Factorio_SampleWithTabsAndBom_IsClean()
    {
        Clean("\uFEFF" + ControlLua.Replace("  ", "\t"));
    }

    [Fact]
    public void Factorio_SettingsAndLocaleStyleCode_IsClean()
    {
        Clean("""
            data:extend({
              { type = "int-setting", name = "my-mod-speed", setting_type = "runtime-global", default_value = 5, minimum_value = 1 },
              { type = "string-setting", name = "my-mod-mode", setting_type = "startup", default_value = "a", allowed_values = { "a", "b" } },
            })
            local v = settings.startup["my-mod-speed"].value
            if v > 3 then log("big") end
            """);
    }

    // --- positions with Unicode / CRLF / BOM ----------------------------------

    [Fact]
    public void Positions_WithCrlf_CountLinesOnce()
    {
        var d = One("a = 1\r\nb = 2\r\nx = \"abc\r\n");
        Assert.Equal("LUA001", d.Code);
        Assert.Equal(3, d.Line);
        Assert.Equal(5, d.Column);
    }

    [Fact]
    public void Positions_WithCrlf_ForBlocks()
    {
        Assert.Equal("LUA005@2:1", Codes("x = 1\r\nfunction f()\r\n  return 1\r\n"));
        Assert.Equal("LUA004@3:1", Codes("x = 1\r\ny = 2\r\nend\r\n"));
    }

    [Fact]
    public void Positions_WithBmpUnicodeBeforeError_CountCharacters()
    {
        // Cyrillic / CJK are single UTF-16 code units: column counts characters, not bytes.
        var d = One("-- \u043F\u0440\u0438\u0432\u0435\u0442\nx = \"\u4F60\u597D\" .. \"oops");
        Assert.Equal("LUA001", d.Code);
        Assert.Equal(2, d.Line);
        Assert.Equal(13, d.Column);
    }

    [Fact]
    public void Positions_UnicodeInsideStrings_DoNotConfuse()
    {
        Clean("x = \"\u043F\u0440\u0438\u0432\u0435\u0442 end ( \u00E9\"\ny = '\u4F60\u597D'");
    }

    [Fact]
    public void Positions_WithLeadingBom_LineIsCorrect()
    {
        var d = LuaDiagnostics.Check("\uFEFFfunction f()\n");
        Assert.Equal(1, d.Count);
        Assert.Equal("LUA005", d[0].Code);
        Assert.Equal(1, d[0].Line);
        // BOM may or may not be counted; both 1 and 2 are acceptable, anything else is wrong.
        Assert.True(d[0].Column == 1 || d[0].Column == 2, "column was " + d[0].Column);
    }

    [Fact]
    public void Positions_Tabs_CountAsOneColumn()
    {
        var d = One("\t\tx = \"abc");
        Assert.Equal(7, d.Column);
    }

    [Fact]
    public void NonBmp_Characters_DoNotThrow_AndOtherLinesStayCorrect()
    {
        var d = LuaDiagnostics.Check("x = \"\U0001F600\"\nfunction f()\n");
        Assert.Equal(1, d.Count);
        Assert.Equal(2, d[0].Line);
        Assert.Equal(1, d[0].Column);
    }

    [Fact]
    public void NulAndControlChars_DoNotThrow()
    {
        var d = LuaDiagnostics.Check("x = 1\0\0 \u0001 \u007F\nend");
        Assert.True(d.Any(x => x.Code == "LUA004" && x.Line == 2));
    }

    // --- caps, ordering, limits --------------------------------------------

    [Fact]
    public void AtMost50Diagnostics()
    {
        var d = LuaDiagnostics.Check(new string(')', 500));
        Assert.Equal(50, d.Count);
        Assert.Equal(50, LuaDiagnostics.Check(string.Concat(Enumerable.Repeat("function f()\n", 300))).Count);
        Assert.Equal(50, LuaDiagnostics.Check(string.Concat(Enumerable.Repeat("x = \"a\n", 300))).Count);
    }

    [Fact]
    public void Exactly49_And_50_And_51_Errors()
    {
        foreach (var n in new[] { 49, 50, 51 })
        {
            var d = LuaDiagnostics.Check(string.Concat(Enumerable.Repeat("end\n", n)));
            Assert.Equal(Math.Min(n, 50), d.Count);
        }
    }

    [Fact]
    public void ResultsAreInSourceOrder_EvenWhenUnclosedOpenersAreReportedAtEof()
    {
        // unclosed ( at line 1 is only known at EOF, after the 'end' on line 2 is flagged
        var d = LuaDiagnostics.Check("f(\nend\n");
        Assert.Equal(2, d.Count);
        Assert.True(d[0].Line <= d[1].Line);
        Assert.Equal(1, d[0].Line);
        Assert.Equal("LUA003", d[0].Code);
        Assert.Equal("LUA004", d[1].Code);
    }

    [Fact]
    public void ResultsSortedByLineThenColumn_ForMixedErrors()
    {
        var d = LuaDiagnostics.Check("function a()\n x = (\n y = \"s\n ] end end\n");
        for (var i = 1; i < d.Count; i++)
            Assert.True(d[i - 1].Line < d[i].Line || (d[i - 1].Line == d[i].Line && d[i - 1].Column <= d[i].Column), Fmt(d));
    }

    [Fact]
    public void IsPure_SameInputSameOutput()
    {
        var src = "function f() x = (\n y = \"abc\n";
        Assert.Equal(Codes(src), Codes(src));
        Assert.Equal(Codes(src), Codes(new string(src.ToCharArray())));
    }

    [Fact]
    public void ConcurrentCalls_AreSafe()
    {
        var src = DataLua + "\nfunction oops(";
        var expected = Codes(src);
        var bad = 0;
        Parallel.For(0, 200, _ => { if (Codes(src) != expected) Interlocked.Increment(ref bad); });
        Assert.Equal(0, bad);
    }

    // --- performance (linear time, 2 MB < 1 s) -------------------------------

    private static void Timed(string name, string text, double limitSeconds = 1.0)
    {
        LuaDiagnostics.Check("x = 1"); // warm up
        var sw = Stopwatch.StartNew();
        var d = LuaDiagnostics.Check(text);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds < limitSeconds, $"{name}: {sw.Elapsed.TotalSeconds:F2}s for {text.Length} chars (limit {limitSeconds}s)");
        Assert.True(d.Count <= 50);
    }

    [Fact]
    public void Perf_2MB_ValidCode()
    {
        var sb = new StringBuilder();
        while (sb.Length < 2_000_000) sb.Append(ControlLua);
        Timed("valid code", sb.ToString().Substring(0, 2_000_000));
    }

    [Fact]
    public void Perf_2MB_DeepNesting_ParensAndBlocks()
    {
        Timed("deep parens", new string('(', 2_000_000));
        Timed("deep closers", new string(')', 2_000_000));
        Timed("deep blocks", string.Concat(Enumerable.Repeat("do ", 700_000)));
        Timed("deep function", string.Concat(Enumerable.Repeat("function(", 220_000)));
    }

    [Fact]
    public void Perf_2MB_UnterminatedConstructs()
    {
        Timed("unterminated string", "x = \"" + new string('a', 2_000_000));
        Timed("unterminated long", "x = [==[" + new string('a', 2_000_000));
        Timed("unterminated comment", "--[[" + new string(']', 2_000_000));
        // many openers that never close: quadratic scanners for the closing bracket blow up here
        Timed("many long openers", string.Concat(Enumerable.Repeat("[=[ ", 500_000))); 
        Timed("many unterminated long comments", string.Concat(Enumerable.Repeat("--[==[ ", 280_000)));
        Timed("many unterminated strings", string.Concat(Enumerable.Repeat("\"a\n", 600_000)));
        Timed("many level-n openers", string.Concat(Enumerable.Range(0, 1500).Select(i => "[" + new string('=', i) + "[ ")));
    }

    [Fact]
    public void Perf_2MB_LongLineAndManyLines()
    {
        Timed("one long line", "x = {" + string.Concat(Enumerable.Repeat("1,", 1_000_000)) + "}");
        Timed("many newlines", new string('\n', 2_000_000));
        Timed("many crlf", string.Concat(Enumerable.Repeat("\r\n", 1_000_000)));
        Timed("many ends", string.Concat(Enumerable.Repeat("end\n", 500_000)));
    }

    [Fact]
    public void Perf_2MB_BackslashesAndEquals()
    {
        Timed("backslashes in string", "x = \"" + new string('\\', 2_000_000));
        Timed("equals signs", "x = [" + new string('=', 2_000_000));
        Timed("dashes", new string('-', 2_000_000));
        Timed("open brackets", new string('[', 2_000_000));
    }

    // --- fuzz ----------------------------------------------------------------

    private static readonly string[] Atoms =
    {
        "function", "end", "if", "then", "else", "elseif", "for", "while", "do", "repeat", "until", "local", "return",
        "(", ")", "[", "]", "{", "}", "[[", "]]", "[=[", "]=]", "[==[", "]==]", "--", "--[[", "--[==[", "\"", "'", "\\", "\\z",
        "\n", "\r\n", " ", "\t", "=", "..", "::", "x", "1", "0x", "1e-", "\0", "\u00E9", "\u4F60", "\uFEFF", "goto", "-", ",", ".", ":",
    };

    private static void CheckInvariants(string text)
    {
        IReadOnlyList<LuaDiagnostic> d = null!;
        try { d = LuaDiagnostics.Check(text); }
        catch (Exception e) { Fail($"Check threw {e.GetType().Name}: {e.Message} for input: {Escape(text)}"); }
        Assert.NotNull(d);
        if (d.Count > 50) Fail($"{d.Count} diagnostics for: {Escape(text)}");
        var lineCount = text.Split('\n').Length + text.Count(c => c == '\r');
        for (var i = 0; i < d.Count; i++)
        {
            var x = d[i];
            if (x.Line < 1 || x.Column < 1 || x.Line > lineCount) Fail($"bad position {x.Line}:{x.Column} for: {Escape(text)}");
            if (x.Severity != "error") Fail("severity " + x.Severity);
            if (i > 0 && (d[i - 1].Line > x.Line || (d[i - 1].Line == x.Line && d[i - 1].Column > x.Column)))
                Fail($"not sorted [{Fmt(d)}] for: {Escape(text)}");
            var lineText = text.Split('\n')[x.Line - 1];
            if (!text.Contains('\r') && x.Column > lineText.Length + 1) Fail($"column beyond line end {x.Line}:{x.Column} for: {Escape(text)}");
        }
    }

    private static string Escape(string s) => s.Length > 300 ? Escape(s.Substring(0, 300)) + "..." : s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\0", "\\0");

    [Fact]
    public void Fuzz_RandomTokenSoup_FixedSeed()
    {
        var rng = new Random(12345);
        for (var iter = 0; iter < 4000; iter++)
        {
            var n = rng.Next(0, 60);
            var sb = new StringBuilder();
            for (var i = 0; i < n; i++) sb.Append(Atoms[rng.Next(Atoms.Length)]).Append(rng.Next(3) == 0 ? " " : "");
            CheckInvariants(sb.ToString());
        }
    }

    [Fact]
    public void Fuzz_RandomChars_FixedSeed()
    {
        var rng = new Random(987654321);
        for (var iter = 0; iter < 2000; iter++)
        {
            var n = rng.Next(0, 200);
            var chars = new char[n];
            for (var i = 0; i < n; i++)
                chars[i] = rng.Next(4) == 0 ? (char)rng.Next(0, 0x10000) : (char)rng.Next(0, 128);
            CheckInvariants(new string(chars)); // includes lone surrogates
        }
    }

    [Fact]
    public void Fuzz_MutatedValidCode_FixedSeed()
    {
        var rng = new Random(2024);
        var src = ControlLua;
        for (var iter = 0; iter < 1500; iter++)
        {
            var chars = src.ToCharArray().ToList();
            var edits = rng.Next(1, 6);
            for (var e = 0; e < edits && chars.Count > 0; e++)
            {
                var pos = rng.Next(chars.Count);
                switch (rng.Next(3))
                {
                    case 0: chars.RemoveAt(pos); break;
                    case 1: chars.Insert(pos, "()[]{}\"'-=\n e"[rng.Next(13)]); break;
                    default: chars.RemoveRange(pos, Math.Min(rng.Next(1, 40), chars.Count - pos)); break;
                }
            }
            CheckInvariants(new string(chars.ToArray()));
        }
    }

    [Fact]
    public void Truncations_OfValidCode_NeverThrow_AndEveryPrefixIsHandled()
    {
        for (var len = 0; len <= ControlLua.Length; len += 7)
            CheckInvariants(ControlLua.Substring(0, len));
    }

    [Fact]
    public void RemovingOneCloser_FromValidCode_AlwaysProducesAtLeastOneDiagnostic()
    {
        // Delete each standalone 'end' keyword occurrence once; the result must not be clean.
        var src = "function f()\n  if a then\n    for i = 1, 2 do\n      x()\n    end\n  end\nend\n";
        var idx = -1;
        var count = 0;
        while ((idx = src.IndexOf("end", idx + 1, StringComparison.Ordinal)) >= 0)
        {
            count++;
            var broken = src.Remove(idx, 3);
            Assert.True(LuaDiagnostics.Check(broken).Count > 0, "removing 'end' at " + idx + " must be flagged");
        }
        Assert.Equal(3, count);
    }
}
