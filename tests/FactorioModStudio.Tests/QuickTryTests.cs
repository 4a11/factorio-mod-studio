using System.Text.Json;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Quick try: a helper mod with a test-map scenario put into a prepared profile.</summary>
public class QuickTryTests
{
    private static PreparedProfile Profile(TempDir dir)
    {
        var mods = dir.Combine("mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "mod-list.json"),
            """{ "mods": [ { "name": "base", "enabled": true }, { "name": "my-mod", "enabled": true } ] }""");
        return new PreparedProfile("default", dir.Path, mods, new[] { "base", "my-mod" }, Array.Empty<string>());
    }

    [Theory]
    [InlineData("2.0", true)]
    [InlineData("2.1", true)]
    [InlineData("1.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Problem_OnlyForModsOlderThan2(string? version, bool fine) => Assert.Equal(fine, QuickTry.Problem(version) is null);

    [Fact]
    public void Install_WritesTheHelperMod_AndEnablesIt()
    {
        using var dir = new TempDir();
        var profile = Profile(dir);

        QuickTry.Install(profile, "my-mod", "2.1");

        var root = Path.Combine(profile.ModsDirectory, "fms-quick-try_1.0.0");
        using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "info.json")));
        Assert.Equal("fms-quick-try", info.RootElement.GetProperty("name").GetString());
        Assert.Equal("2.1", info.RootElement.GetProperty("factorio_version").GetString());
        Assert.Equal(new[] { "base", "my-mod" }, info.RootElement.GetProperty("dependencies").EnumerateArray().Select(e => e.GetString()));
        var control = File.ReadAllText(Path.Combine(root, "scenarios", "quick-try", "control.lua"));
        Assert.Contains("local MOD = \"my-mod\"", control);
        Assert.Contains("made_by_mod(item.type, name)", control); // the history is kept under the real type
        Assert.True(File.Exists(Path.Combine(root, "scenarios", "quick-try", "description.json")));

        using var list = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.ModsDirectory, "mod-list.json")));
        var mods = list.RootElement.GetProperty("mods").EnumerateArray().ToList();
        Assert.Contains(mods, m => m.GetProperty("name").GetString() == "fms-quick-try" && m.GetProperty("enabled").GetBoolean());
        Assert.Contains(mods, m => m.GetProperty("name").GetString() == "my-mod");

        QuickTry.Install(profile, "my-mod", "2.1"); // twice: still one entry
        using var again = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.ModsDirectory, "mod-list.json")));
        Assert.Single(again.RootElement.GetProperty("mods").EnumerateArray(), m => m.GetProperty("name").GetString() == "fms-quick-try");
        Assert.Equal(new[] { "--load-scenario", "fms-quick-try/quick-try" }, QuickTry.Arguments);
    }

    [Fact]
    public void Install_RefusesAModFor11()
    {
        using var dir = new TempDir();
        var error = Assert.Throws<FmsException>(() => QuickTry.Install(Profile(dir), "my-mod", "1.1"));
        Assert.Contains("2.0 or later", error.Message);
    }

    [Fact]
    public void Script_QuotesTheModName() =>
        Assert.Contains("local MOD = \"a\\\"b\"", QuickTry.Script("a\"b"));
}
