using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

public class LocaleApiTests
{
    private static string InfoFor(string name) =>
        $$"""{ "name": "{{name}}", "version": "1.0.0", "title": "T", "author": "a", "factorio_version": "2.0", "dependencies": ["base"] }""";

    private static async Task<RunningUi> Start(TempDir project, TempDir workspace) =>
        await UiServer.StartAsync(
            new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false),
            new UiOptions(Port: 0, WorkspaceDirectory: workspace.Path));

    private static HttpClient Client(RunningUi ui)
    {
        var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        return http;
    }

    private static async Task<JsonElement> GetJson(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> Overview(HttpClient http, string name)
    {
        var id = (await GetJson(http, "/api/mods")).EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("id").GetString();
        return await GetJson(http, $"/api/mods/{id}");
    }

    private static List<JsonElement> Issues(JsonElement overview, string code) =>
        overview.GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("code").GetString() == code).ToList();

    private static string Repo()
    {
        var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("FMS_REPO_ROOT") is { Length: > 0 } root ? root : AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "examples", "hello-mod"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("examples/hello-mod not found");
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    [Fact]
    public async Task Overview_HasLocaleCoverage_EnFirst_CamelCase_AndNumbers()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        ws.Write("alpha/info.json", InfoFor("alpha"));
        ws.Write("alpha/data.lua", "data:extend({})\n");
        ws.Write("alpha/locale/fr/a.cfg", "[item-name]\nx=1\n");
        ws.Write("alpha/locale/en/a.cfg", "[item-name]\nx=1\ny=2\nz=3\n");
        ws.Write("alpha/locale/de/a.cfg", "[item-name]\nx=1\nq=9\n");
        await using var ui = await Start(project, ws);
        using var http = Client(ui);

        var overview = await Overview(http, "alpha");
        var cov = overview.GetProperty("localeCoverage");
        Assert.Equal(JsonValueKind.Array, cov.ValueKind);
        var rows = cov.EnumerateArray().ToList();
        Assert.Equal(new[] { "en", "de", "fr" }, rows.Select(r => r.GetProperty("language").GetString()!).ToArray());
        Assert.Equal(new[] { 3, 2, 1 }, rows.Select(r => r.GetProperty("keys").GetInt32()).ToArray());
        Assert.Equal(new[] { 0, 2, 2 }, rows.Select(r => r.GetProperty("missing").GetInt32()).ToArray());
        Assert.Equal(new[] { 0, 1, 0 }, rows.Select(r => r.GetProperty("extra").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Overview_IssuesCarryTheLocaleCodes_WithFileAndLine()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        ws.Write("alpha/info.json", InfoFor("alpha"));
        ws.Write("alpha/data.lua", "data:extend({\n  { type = \"item\", name = \"zed\", stack_size = 1 },\n})\n");
        ws.Write("alpha/control.lua", "local s = {\"my-gui.nope\"}\n");
        ws.Write("alpha/locale/en/a.cfg", "[my-gui]\nk=1\nk=2\nbroken\n[mod-name]\nx=X\n");
        ws.Write("alpha/locale/de/a.cfg", "[my-gui]\nk=__1__\nextra=3\n");
        await using var ui = await Start(project, ws);
        using var http = Client(ui);

        var overview = await Overview(http, "alpha");
        foreach (var code in new[] { "FMS040", "FMS041", "FMS042", "FMS043", "FMS044", "FMS045", "FMS046", "FMS047" })
            Assert.True(Issues(overview, code).Count > 0, "missing " + code);

        var f40 = Issues(overview, "FMS040").Single();
        Assert.Equal(4, f40.GetProperty("line").GetInt32());
        Assert.Contains("a.cfg", f40.GetProperty("file").GetString());
        Assert.Equal("error", f40.GetProperty("severity").GetString());
        var f42 = Issues(overview, "FMS042").Single();
        Assert.Equal(2, f42.GetProperty("line").GetInt32());
        Assert.Equal("data.lua", f42.GetProperty("file").GetString());
        Assert.Equal("info", Issues(overview, "FMS045").Single().GetProperty("severity").GetString());
    }

    [Fact]
    public async Task Overview_WithoutDefaultLanguage_ZeroMissingExtra_AndFms048()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        ws.Write("beta/info.json", InfoFor("beta"));
        ws.Write("beta/locale/de/a.cfg", "[item-name]\nx=1\ny=2\n");
        ws.Write("beta/locale/fr/a.cfg", "[item-name]\nq=1\n");
        await using var ui = await Start(project, ws);
        using var http = Client(ui);

        var overview = await Overview(http, "beta");
        var rows = overview.GetProperty("localeCoverage").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.Equal(0, r.GetProperty("missing").GetInt32()); Assert.Equal(0, r.GetProperty("extra").GetInt32()); });
        Assert.Single(Issues(overview, "FMS048"));
        Assert.Empty(Issues(overview, "FMS044"));
    }

    [Fact]
    public async Task Overview_ModWithoutLocale_HasEmptyCoverageArray()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        ws.Write("gamma/info.json", InfoFor("gamma"));
        await using var ui = await Start(project, ws);
        using var http = Client(ui);

        var overview = await Overview(http, "gamma");
        var cov = overview.GetProperty("localeCoverage");
        Assert.Equal(JsonValueKind.Array, cov.ValueKind);
        Assert.Equal(0, cov.GetArrayLength());
    }

    [Fact]
    public async Task Overview_HelloExample_HasNoLocaleProblems_AndCoverageOfEn()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        CopyDir(Path.Combine(Repo(), "examples", "hello-mod"), ws.Combine("hello-mod"));
        await using var ui = await Start(project, ws);
        using var http = Client(ui);

        var overview = await Overview(http, "hello-mod");
        var bad = overview.GetProperty("issues").EnumerateArray()
            .Where(i => i.GetProperty("code").GetString()!.StartsWith("FMS04") && i.GetProperty("severity").GetString() != "info").ToList();
        Assert.Empty(bad);
        var row = Assert.Single(overview.GetProperty("localeCoverage").EnumerateArray().ToList());
        Assert.Equal("en", row.GetProperty("language").GetString());
        Assert.Equal(1, row.GetProperty("keys").GetInt32());
    }

    [Fact]
    public async Task Overview_PicksUpLocaleEditsOnNextRequest()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        ws.Write("alpha/info.json", InfoFor("alpha"));
        ws.Write("alpha/locale/en/a.cfg", "[item-name]\nx=1\n");
        await using var ui = await Start(project, ws);
        using var http = Client(ui);

        Assert.Empty(Issues(await Overview(http, "alpha"), "FMS040"));
        File.WriteAllText(ws.Combine("alpha", "locale", "en", "a.cfg"), "[item-name]\nx=1\noops\n");
        Assert.Single(Issues(await Overview(http, "alpha"), "FMS040"));
    }
}
