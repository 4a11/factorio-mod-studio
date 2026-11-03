using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public class LogParserTests
{
    private static LogReport Parse(string log) => LogParser.Parse(log.Split('\n'));

    [Fact]
    public void CleanLog_HasNoDiagnostics()
    {
        var report = Parse("""
               0.001 Info Main.cpp:1: Factorio started
               0.200 Script @__my-mod__/control.lua:2: my-mod initialised
            """);
        Assert.Empty(report.Diagnostics);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void DataStageError_IsExtractedWithLocationAndTraceback()
    {
        var report = Parse(
            "   0.351 Error ModManager.cpp:1744: Failed to load mod \"my-mod\": __my-mod__/prototypes/item.lua:12: attempt to index a nil value\n" +
            "stack traceback:\n" +
            "\t__my-mod__/prototypes/item.lua:12: in main chunk\n" +
            "\t[C]: in function 'require'\n" +
            "   0.360 Info Main.cpp:2: next thing\n");

        var error = Assert.Single(report.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("load", error.Category);
        Assert.Equal("my-mod", error.Mod);
        Assert.Equal("prototypes/item.lua", error.File);
        Assert.Equal(12, error.Line);
        Assert.Equal(3, error.Details.Count);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void RuntimeError_CombinesHeaderWithLuaMessage()
    {
        var report = Parse(
            "Error while running event my-mod::on_tick (ID 0)\n" +
            "attempt to call global 'frobnicate' (a nil value)\n" +
            "stack traceback:\n" +
            "\t__my-mod__/control.lua:12: in function <__my-mod__/control.lua:10>\n");

        var error = Assert.Single(report.Diagnostics);
        Assert.Equal("runtime", error.Category);
        Assert.Contains("frobnicate", error.Message);
        Assert.Equal("control.lua", error.File);
        Assert.Equal(12, error.Line);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void FatalError_IsCategorized()
    {
        var report = Parse("   0.4 Error Util.cpp:107: The mod my-mod (0.1.0) caused a non-recoverable error.\nPlease report this error to the mod author.\n");
        Assert.Equal("fatal", Assert.Single(report.Diagnostics).Category);
    }

    [Fact]
    public void IdenticalMessages_AreMergedAndCounted()
    {
        var report = Parse(
            "   1.0 Warning Foo.cpp:1: deprecated thing\n" +
            "   2.0 Warning Foo.cpp:1: deprecated thing\n" +
            "   3.0 Warning Foo.cpp:1: deprecated thing\n" +
            "   4.0 Warning Foo.cpp:1: something else\n");

        Assert.Equal(2, report.WarningCount);
        Assert.Equal(3, report.Diagnostics[0].Count);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void WindowsLineEndings_AreHandled()
    {
        var report = LogParser.Parse(new[] { "   0.351 Error Foo.cpp:1: boom\r", "" });
        Assert.Equal("boom", Assert.Single(report.Diagnostics).Message);
    }

    [Fact]
    public void BlankLine_EndsTracebackCollection()
    {
        var report = Parse("   0.1 Error Foo.cpp:1: boom\nstack traceback:\n\n unrelated line\n");
        Assert.Single(Assert.Single(report.Diagnostics).Details);
    }
}
