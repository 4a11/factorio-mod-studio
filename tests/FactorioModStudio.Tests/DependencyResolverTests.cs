using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;

namespace FactorioModStudio.Tests;

public class DependencyResolverTests
{
    private static ModInfo Info(params string[] deps) => new() { Name = "project", Version = "1.0.0", RawDependencies = deps };
    private static AvailableMod Mod(string name, string version, params string[] deps) => new(name, ModVersion.Parse(version), name, true, deps.Select(s => { Assert.True(Dependency.TryParse(s, out var d)); return d; }).ToList());

    [Fact]
    public void SelectsOlderCompatibleVersion()
    {
        var result = DependencyResolver.Resolve(Info("lib < 2.0.0"), [Mod("lib", "2.0.0"), Mod("lib", "1.0.0")], []);
        Assert.Null(result.Error);
        Assert.Equal("1.0.0", Assert.Single(result.Mods).Version.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconsidersSelectionForLaterTransitiveConstraint(bool reverse)
    {
        var deps = reverse ? new[] { "consumer", "lib" } : new[] { "lib", "consumer" };
        var result = DependencyResolver.Resolve(Info(deps), [Mod("lib", "2.0.0"), Mod("lib", "1.0.0"), Mod("consumer", "1.0.0", "lib < 2.0.0")], []);
        Assert.Null(result.Error);
        Assert.Equal("1.0.0", result.Mods.Single(m => m.Name == "lib").Version.ToString());
    }

    [Fact]
    public void BacktrackingDoesNotRetainDependenciesOfRejectedVersion()
    {
        var result = DependencyResolver.Resolve(Info("lib"), [Mod("lib", "2.0.0", "missing"), Mod("lib", "1.0.0")], []);
        Assert.Null(result.Error);
        Assert.Equal("1.0.0", Assert.Single(result.Mods).Version.ToString());
    }

    [Fact]
    public void OptionalConstraintAppliesOnlyWhenEnabled()
    {
        var available = new[] { Mod("lib", "2.0.0"), Mod("lib", "1.0.0") };
        Assert.Empty(DependencyResolver.Resolve(Info("? lib < 2.0.0"), available, []).Mods);
        var result = DependencyResolver.Resolve(Info("? lib < 2.0.0"), available, ["lib"]);
        Assert.Equal("1.0.0", Assert.Single(result.Mods).Version.ToString());
    }

    [Fact]
    public void NoLoadOrderDependencyIsRequiredAndCyclesTerminate()
    {
        var result = DependencyResolver.Resolve(Info("~ lib"), [Mod("lib", "1.0.0", "consumer"), Mod("consumer", "1.0.0", "lib")], []);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Mods.Count);
    }

    [Fact]
    public void IncompatibleVersionIsAvoided()
    {
        var result = DependencyResolver.Resolve(Info("lib", "! lib >= 2.0.0"), [Mod("lib", "2.0.0"), Mod("lib", "1.0.0")], []);
        Assert.Null(result.Error);
        Assert.Equal("1.0.0", Assert.Single(result.Mods).Version.ToString());
    }

    [Fact]
    public void ImpossibleConstraintsDoNotTouchExistingProfile()
    {
        using var root = Fixtures.ValidMod(JsonSerializer.Serialize(new { name = "project", version = "1.0.0", dependencies = new[] { "lib >= 2.0.0", "lib < 2.0.0" } }));
        using var source = new TempDir();
        Fixtures.WriteModZip(source.Path, "lib", "2.0.0");
        var marker = root.Write(".fmstudio/profiles/default/mods/keep.txt", "keep");
        var context = new ProjectContext(root.Path, new ProjectConfig { ModsSource = source.Path }, true);
        var error = Assert.Throws<FmsException>(() => ProfileBuilder.Prepare(context, "default"));
        Assert.Contains("lib", error.Message);
        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [Fact]
    public async Task OverviewRefreshesStatusesAndProblemsWhenSourceChanges()
    {
        using var root = Fixtures.ValidMod(JsonSerializer.Serialize(new { name = "project", version = "1.0.0", dependencies = new[] { "base", "lib < 2.0.0", "? extra" } }));
        using var source = new TempDir();
        var context = new ProjectContext(root.Path, new ProjectConfig { ModsSource = source.Path }, true);
        await using var ui = await UiServer.StartAsync(context, new UiOptions(Port: 0));
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        using var missing = JsonDocument.Parse(await http.GetStringAsync("api/mods/project"));
        Assert.Contains(missing.RootElement.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "FMS050");
        Fixtures.WriteModZip(source.Path, "lib", "1.0.0");
        Fixtures.WriteModZip(source.Path, "lib", "2.0.0");
        using var ready = JsonDocument.Parse(await http.GetStringAsync("api/mods/project"));
        var statuses = ready.RootElement.GetProperty("dependencyStatuses").EnumerateArray().ToArray();
        Assert.Equal("game", statuses[0].GetProperty("status").GetString());
        Assert.Equal("ready", statuses[1].GetProperty("status").GetString());
        Assert.Equal("1.0.0", statuses[1].GetProperty("version").GetString());
        Assert.Equal("inactive", statuses[2].GetProperty("status").GetString());
        Assert.DoesNotContain(ready.RootElement.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "FMS050");
    }
}
