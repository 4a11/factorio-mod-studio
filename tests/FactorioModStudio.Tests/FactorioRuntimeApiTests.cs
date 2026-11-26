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
              "events": [{"name":"CustomInputEvent","description":"Not a defines.events member"},{"name":"on_tick","description":"Runs every tick."}],
              "classes": [{"name":"LuaBootstrap","methods":[{"name":"on_event","description":"Register a handler.","parameters":[
                  {"name":"filters","order":2,"type":"EventFilter","optional":true},
                  {"name":"event","order":0,"type":"LuaEventType"},
                  {"name":"handler","order":1,"type":{"complex_type":"union","options":[{"complex_type":"function"},"nil"]}}
              ]}]}]
            }
            """);

        var api = Assert.IsType<FactorioRuntimeApi>(FactorioRuntimeApi.Read(executable, "2.1.24"));
        Assert.Equal("2.1.24", api.Version);
        Assert.Equal(new[] { "game", "log" }, api.Globals);
        Assert.Equal(new[] { "on_tick" }, api.Events);
        Assert.Equal("Runs every tick.", Assert.Single(api.EventDescriptions).Value);
        Assert.NotNull(api.OnEvent);
        Assert.Equal(new[] { "event", "handler", "filters" }, api.OnEvent.Parameters.Select(p => p.Name));
        Assert.Equal("function | nil", api.OnEvent.Parameters[1].Type);
        Assert.True(api.OnEvent.Parameters[2].Optional);
        Assert.Null(FactorioRuntimeApi.Read(executable, "2.0.77"));
        File.WriteAllText(path, "{");
        Assert.Null(FactorioRuntimeApi.Read(executable, "2.1.24"));
        File.Delete(path);
        Assert.Null(FactorioRuntimeApi.Read(executable, "2.1.24"));
    }
}
