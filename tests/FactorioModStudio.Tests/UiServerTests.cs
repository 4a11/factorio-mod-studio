using System.Net;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

public class UiServerTests
{
    private static async Task<RunningUi> Start(TempDir project, params string[] modFolders)
    {
        var context = new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false);
        return await UiServer.StartAsync(context, new UiOptions(Port: 0, ExtraModFolders: modFolders));
    }

    private static async Task<JsonElement> GetJson(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task ServesIndexPageAndScripts_WithSecurityHeaders()
    {
        using var mod = Fixtures.ValidMod();
        await using var ui = await Start(mod);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };

        using var index = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("Factorio Mod Studio", await index.Content.ReadAsStringAsync());
        Assert.True(index.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/style.css")).StatusCode);
    }

    [Fact]
    public async Task ListsProjectModAndInstalledMods_AndReturnsOverview()
    {
        using var mod = Fixtures.ValidMod();
        using var installed = new TempDir();
        Fixtures.WriteModZip(installed.Path, "zipped-dep", "2.0.0", "base");
        await using var ui = await Start(mod, installed.Path);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };

        var mods = (await GetJson(http, "/api/mods")).EnumerateArray().ToList();
        Assert.Equal("project", mods[0].GetProperty("id").GetString());
        Assert.Equal("my-mod", mods[0].GetProperty("name").GetString());

        var zipped = mods.Single(m => m.GetProperty("name").GetString() == "zipped-dep");
        Assert.Equal("zip", zipped.GetProperty("kind").GetString());

        var project = await GetJson(http, "/api/mods/project");
        Assert.Equal("my-mod", project.GetProperty("info").GetProperty("name").GetString());

        var dep = await GetJson(http, "/api/mods/" + zipped.GetProperty("id").GetString());
        Assert.Equal("zipped-dep", dep.GetProperty("info").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ReflectsChangesToTheProjectModWithoutRestart()
    {
        using var mod = Fixtures.ValidMod();
        await using var ui = await Start(mod);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };

        Assert.Equal(0, (await GetJson(http, "/api/mods/project")).GetProperty("prototypes").GetArrayLength());
        mod.Write("data.lua", "data:extend({ { type = \"item\", name = \"fresh\" } })");
        Assert.Equal(1, (await GetJson(http, "/api/mods/project")).GetProperty("prototypes").GetArrayLength());
    }

    [Fact]
    public async Task ServesFilesInsideTheModOnly()
    {
        using var mod = Fixtures.ValidMod();

        // A real file just outside the mod folder: it must stay unreachable.
        var secretName = $"fms-secret-{Guid.NewGuid():N}.txt";
        var secretPath = Path.Combine(Path.GetDirectoryName(mod.Path)!, secretName);
        File.WriteAllText(secretPath, "top-secret-content");

        try
        {
            await using var ui = await Start(mod);
            using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };

            using (var ok = await http.GetAsync("/api/mods/project/file?path=data.lua"))
            {
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
                Assert.Contains("data:extend", await ok.Content.ReadAsStringAsync());
            }

            foreach (var attack in new[] { $"../{secretName}", $"..%2f{secretName}", secretPath, $"a/../../{secretName}", $"..\\{secretName}" })
            {
                using var response = await http.GetAsync("/api/mods/project/file?path=" + Uri.EscapeDataString(attack).Replace("%2F", "/"));
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{attack} -> {response.StatusCode}");
                Assert.DoesNotContain("top-secret-content", body);
            }

            using var unknown = await http.GetAsync("/api/mods/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            File.Delete(secretPath);
        }
    }

    [Fact]
    public async Task RejectsRequestsWithAForeignHostHeader()
    {
        using var mod = Fixtures.ValidMod();
        await using var ui = await Start(mod);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/mods");
        request.Headers.Host = "attacker.example";
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("data.lua", true)]
    [InlineData("sub/dir/file.png", true)]
    [InlineData("../x", false)]
    [InlineData("a/../../x", false)]
    [InlineData("a\\..\\..\\x", false)]
    [InlineData("", false)]
    [InlineData("/abs", false)]
    public void ResolveInside_AcceptsOnlyRelativePathsBelowTheRoot(string path, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "fms-root");
        Assert.Equal(allowed, UiServer.ResolveInside(root, path) is not null);
    }
}
