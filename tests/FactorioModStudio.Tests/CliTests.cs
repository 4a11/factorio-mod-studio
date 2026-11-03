using FactorioModStudio.Cli;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public class CliTests
{
    private static (int Code, string Out, string Err) Run(IProcessRunner? runner, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Program.Run(args, output, error, runner);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void NoArguments_ShowsHelpWithUsageExitCode()
    {
        var (code, output, _) = Run(null);
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("Usage: fmstudio", output);
    }

    [Fact]
    public void UnknownCommand_ReportsError()
    {
        var (code, _, error) = Run(null, "frobnicate");
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("unknown command", error);
    }

    [Fact]
    public void Validate_ReturnsZeroForValidMod_AndOneForBrokenMod()
    {
        using var good = Fixtures.ValidMod();
        Assert.Equal(Program.ExitOk, Run(null, "validate", good.Path).Code);

        using var bad = Fixtures.ValidMod("{}");
        var (code, output, _) = Run(null, "validate", bad.Path);
        Assert.Equal(Program.ExitProblemsFound, code);
        Assert.Contains("FMS003", output);
    }

    [Fact]
    public void Validate_JsonOutput_IsMachineReadable()
    {
        using var bad = Fixtures.ValidMod("{}");
        var (_, output, _) = Run(null, "validate", bad.Path, "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("errorCount").GetInt32() > 0);
    }

    [Fact]
    public void Pack_WritesArchiveIntoDist()
    {
        using var mod = Fixtures.ValidMod();
        var (code, output, _) = Run(null, "pack", "--project", mod.Path);
        Assert.Equal(Program.ExitOk, code);
        Assert.True(File.Exists(Path.Combine(mod.Path, "dist", "my-mod_1.2.3.zip")));
        Assert.Contains("Packed", output);
    }

    [Fact]
    public void Init_CreatesConfigOnce()
    {
        using var dir = new TempDir();
        Assert.Equal(Program.ExitOk, Run(null, "init", "-p", dir.Path).Code);
        var path = Path.Combine(dir.Path, ProjectConfig.FileName);
        Assert.True(File.Exists(path));

        File.WriteAllText(path, "{ \"modPath\": \"custom\" }");
        Run(null, "init", "-p", dir.Path);
        Assert.Contains("custom", File.ReadAllText(path));
    }

    [Fact]
    public void Log_ReturnsOneWhenErrorsAreFound()
    {
        using var dir = new TempDir();
        var log = dir.Write("factorio-current.log", "   0.3 Error Foo.cpp:1: boom\n");
        var (code, output, _) = Run(null, "log", log);
        Assert.Equal(Program.ExitProblemsFound, code);
        Assert.Contains("boom", output);

        var clean = dir.Write("clean.log", "   0.3 Info Foo.cpp:1: fine\n");
        Assert.Equal(Program.ExitOk, Run(null, "log", clean).Code);
    }

    [Fact]
    public void Log_MissingFile_IsUsageError()
    {
        var (code, _, error) = Run(null, "log", "/definitely/not/here.log");
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("not found", error);
    }

    [Fact]
    public void Smoke_UsesInjectedRunner_AndReportsFailure()
    {
        using var mod = Fixtures.ValidMod();
        var fakeFactorio = mod.Write("fake-factorio", "");
        var runner = new FakeRunner(args => args[0] == "--benchmark"
            ? new ProcessResult(0, false, new[] { "   9.0 Error Foo.cpp:1: runtime boom" })
            : new ProcessResult(0, false, Array.Empty<string>()));

        var (code, output, _) = Run(runner, "smoke", "--project", mod.Path, "--factorio", fakeFactorio, "--ticks", "120");

        Assert.Equal(Program.ExitProblemsFound, code);
        Assert.Contains("SMOKE TEST FAILED", output);
        Assert.Contains("runtime boom", output);
        Assert.Contains("120", runner.Calls[1]);
    }

    [Fact]
    public void Smoke_WithoutFactorio_ExplainsHowToConfigureIt()
    {
        using var mod = Fixtures.ValidMod();
        var previous = Environment.GetEnvironmentVariable(FactorioLocator.EnvironmentVariable);
        Environment.SetEnvironmentVariable(FactorioLocator.EnvironmentVariable, Path.Combine(mod.Path, "nope"));
        try
        {
            var (code, _, error) = Run(new FakeRunner(_ => new ProcessResult(0, false, Array.Empty<string>())), "smoke", "-p", mod.Path);
            Assert.Equal(Program.ExitUsageError, code);
            Assert.Contains("FACTORIO_PATH", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FactorioLocator.EnvironmentVariable, previous);
        }
    }
}
