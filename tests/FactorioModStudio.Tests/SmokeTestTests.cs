using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public class SmokeTestTests
{
    private static (ProjectContext Context, PreparedProfile Profile) Setup(TempDir root)
    {
        var context = new ProjectContext(root.Path, new ProjectConfig(), configFileExists: false);
        return (context, ProfileBuilder.Prepare(context, "default"));
    }

    private static ProcessResult Ok(params string[] lines) => new(0, false, lines);

    [Fact]
    public void Passes_WhenBothStepsSucceedWithoutLogErrors()
    {
        using var root = Fixtures.ValidMod();
        var (context, profile) = Setup(root);
        var runner = new FakeRunner(_ => Ok("   0.1 Info Main.cpp:1: fine", "   0.2 Warning Foo.cpp:1: meh"));

        var result = SmokeTest.Run("factorio", context, profile, new ProfileConfig(), new SmokeOptions(Ticks: 300, Seed: 7), runner);

        Assert.True(result.Passed);
        Assert.Equal(1, result.Report.WarningCount);

        Assert.Equal(2, runner.Calls.Count);
        var create = runner.Calls[0];
        Assert.Equal("--create", create[0]);
        Assert.Contains("--mod-directory", create);
        Assert.Equal("7", create[create.ToList().IndexOf("--map-gen-seed") + 1]);
        Assert.Contains("--disable-audio", create); // the full game; the studio's headless servers get it left out

        var benchmark = runner.Calls[1];
        Assert.Equal("--benchmark", benchmark[0]);
        Assert.Equal("300", benchmark[benchmark.ToList().IndexOf("--benchmark-ticks") + 1]);
        Assert.Equal(profile.ModsDirectory, benchmark[benchmark.ToList().IndexOf("--mod-directory") + 1]);
    }

    [Fact]
    public void Fails_WhenLogContainsErrorsEvenIfExitCodeIsZero()
    {
        using var root = Fixtures.ValidMod();
        var (context, profile) = Setup(root);
        var runner = new FakeRunner(args => args[0] == "--benchmark"
            ? Ok("Error while running event my-mod::on_tick (ID 0)", "attempt to index a nil value")
            : Ok());

        var result = SmokeTest.Run("factorio", context, profile, new ProfileConfig(), new SmokeOptions(), runner);

        Assert.False(result.Passed);
        Assert.Equal(1, result.Report.ErrorCount);
    }

    [Fact]
    public void StopsAfterFailedMapGeneration()
    {
        using var root = Fixtures.ValidMod();
        var (context, profile) = Setup(root);
        var runner = new FakeRunner(_ => new ProcessResult(1, false,
            new[] { "   0.3 Error ModManager.cpp:1: Failed to load mod \"my-mod\": __my-mod__/data.lua:1: boom" }));

        var result = SmokeTest.Run("factorio", context, profile, new ProfileConfig(), new SmokeOptions(), runner);

        Assert.False(result.Passed);
        Assert.Single(runner.Calls); // the benchmark step was never started
        Assert.Contains("Map generation failed", result.FailureReason);
        Assert.Equal("data.lua", result.Report.Diagnostics[0].File);
    }

    [Fact]
    public void Fails_OnTimeout()
    {
        using var root = Fixtures.ValidMod();
        var (context, profile) = Setup(root);
        var runner = new FakeRunner(_ => new ProcessResult(-1, true, Array.Empty<string>()));

        var result = SmokeTest.Run("factorio", context, profile, new ProfileConfig(), new SmokeOptions(), runner);

        Assert.False(result.Passed);
        Assert.Contains("timed out", result.FailureReason);
    }

    [Fact]
    public void UsesConfiguredSave_AndSkipsMapGeneration()
    {
        using var root = Fixtures.ValidMod();
        File.WriteAllText(Path.Combine(root.Path, "scenario.zip"), "save");
        var (context, profile) = Setup(root);
        var runner = new FakeRunner(_ => Ok());
        var config = new ProfileConfig { Save = "scenario.zip", Args = { "--some-flag" } };

        var result = SmokeTest.Run("factorio", context, profile, config, new SmokeOptions(), runner);

        Assert.True(result.Passed);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("--benchmark", call[0]);
        Assert.Equal(Path.Combine(root.Path, "scenario.zip"), call[1]);
        Assert.Contains("--some-flag", call);
    }

    [Fact]
    public void MissingConfiguredSave_Throws()
    {
        using var root = Fixtures.ValidMod();
        var (context, profile) = Setup(root);
        var config = new ProfileConfig { Save = "does-not-exist.zip" };

        Assert.Throws<FmsException>(() =>
            SmokeTest.Run("factorio", context, profile, config, new SmokeOptions(), new FakeRunner(_ => Ok())));
    }
}
