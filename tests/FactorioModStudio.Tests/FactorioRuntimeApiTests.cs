using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public sealed class FactorioRuntimeApiTests
{
    [Fact]
    public void ReadsNamesFromTheMatchingInstalledRuntimeDocumentation()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "data", "base"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "doc-html"));
        var executable = Path.Combine(temp.Path, "bin", "x64", "factorio");
        var path = Path.Combine(temp.Path, "doc-html", "runtime-api.json");
        File.WriteAllText(path, """
            {
              "application": "factorio", "application_version": "2.1.24", "stage": "runtime",
              "global_objects": [{"name":"game"},{"name":"invalid.name"}],
              "global_functions": [{"name":"log"}],
              "defines": [{"name":"events","values":[{"name":"on_tick"}]}],
              "events": [{"name":"CustomInputEvent"}]
            }
            """);

        var api = Assert.IsType<FactorioRuntimeApi>(FactorioRuntimeApi.Read(executable, "2.1.24"));
        Assert.Equal("2.1.24", api.Version);
        Assert.Equal(new[] { "game", "log" }, api.Globals);
        Assert.Equal(new[] { "on_tick" }, api.Events);
        Assert.Null(FactorioRuntimeApi.Read(executable, "2.0.77"));
        File.WriteAllText(path, "{");
        Assert.Null(FactorioRuntimeApi.Read(executable, "2.1.24"));
        File.Delete(path);
        Assert.Null(FactorioRuntimeApi.Read(executable, "2.1.24"));
    }
}
