using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

public class WorkspaceTests
{
    private const string Info = """{ "name": "{0}", "version": "1.0.0", "title": "T", "author": "a", "factorio_version": "2.0", "dependencies": ["base"] }""";

    private static string InfoFor(string name) => Info.Replace("{0}", name);

    private static async Task<RunningUi> Start(TempDir project, TempDir workspace) =>
        await UiServer.StartAsync(
            new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false),
            new UiOptions(Port: 0, WorkspaceDirectory: workspace.Path));

    private static HttpClient Client(RunningUi ui, bool withToken = true)
    {
        var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        if (withToken) http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        return http;
    }

    private static async Task<JsonElement> GetJson(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string> WorkspaceModId(HttpClient http, string name) =>
        (await GetJson(http, "/api/mods")).EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;

    private static HttpRequestMessage Put(string id, string path, string content, string? ifMatch = null, bool create = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/mods/{id}/file?path={Uri.EscapeDataString(path)}")
        {
            Content = new StringContent(content, new UTF8Encoding(false), "text/plain")
        };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (create) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        return request;
    }

    [Fact]
    public async Task DiscoversWorkspaceMods_AsEditable_AndPicksUpNewOnesWithoutRestart()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("alpha/info.json", InfoFor("alpha"));
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var alpha = (await GetJson(http, "/api/mods")).EnumerateArray().Single();
        Assert.Equal("workspace", alpha.GetProperty("source").GetString());
        Assert.True(alpha.GetProperty("editable").GetBoolean());

        workspace.Write("beta/info.json", InfoFor("beta"));
        var names = (await GetJson(http, "/api/mods")).EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("beta", names);
    }

    [Fact]
    public async Task Save_RequiresToken_AndRejectsForeignOrigin()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("alpha/info.json", InfoFor("alpha"));
        workspace.Write("alpha/data.lua", "-- a\n");
        await using var ui = await Start(project, workspace);
        using var anonymous = Client(ui, withToken: false);
        var id = await WorkspaceModId(anonymous, "alpha");

        using var noToken = await anonymous.SendAsync(Put(id, "data.lua", "x", "\"0\""));
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);

        using var http = Client(ui);
        var foreign = Put(id, "data.lua", "x", "\"0\"");
        foreign.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        using var wrongOrigin = await http.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
        Assert.Equal("-- a\n", File.ReadAllText(workspace.Combine("alpha", "data.lua")));
    }

    [Fact]
    public async Task Save_WritesFile_ReturnsNewEtag_AndDetectsConflicts()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("alpha/info.json", InfoFor("alpha"));
        workspace.Write("alpha/data.lua", "-- v1\n");
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await WorkspaceModId(http, "alpha");

        using var read = await http.GetAsync($"/api/mods/{id}/file?path=data.lua");
        var etag = read.Headers.ETag!.ToString();

        using var saved = await http.SendAsync(Put(id, "data.lua", "-- v2\n", etag));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("-- v2\n", File.ReadAllText(workspace.Combine("alpha", "data.lua")));
        var newEtag = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("etag").GetString();
        Assert.NotEqual(etag, newEtag);

        // Stale validator: someone else wrote in the meantime.
        using var stale = await http.SendAsync(Put(id, "data.lua", "-- mine\n", etag));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("-- v2\n", File.ReadAllText(workspace.Combine("alpha", "data.lua")));

        // No validator at all is refused too.
        using var missing = await http.SendAsync(Put(id, "data.lua", "-- mine\n"));
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        // No temp files are left behind.
        Assert.Empty(Directory.GetFiles(workspace.Combine("alpha"), "*.fms-tmp"));
    }

    [Fact]
    public async Task Save_PreservesCrlfAndBom()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("alpha/info.json", InfoFor("alpha"));
        var path = workspace.Combine("alpha", "data.lua");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a\r\nb\r\n")).ToArray());
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await WorkspaceModId(http, "alpha");

        using var read = await http.GetAsync($"/api/mods/{id}/file?path=data.lua");
        using var saved = await http.SendAsync(Put(id, "data.lua", "a\nb\nc\n", read.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal("a\r\nb\r\nc\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task Save_CreatesNewFile_OnlyWithIfNoneMatch()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("alpha/info.json", InfoFor("alpha"));
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await WorkspaceModId(http, "alpha");

        using var refused = await http.SendAsync(Put(id, "scripts/new.lua", "-- hi\n"));
        Assert.Equal((HttpStatusCode)428, refused.StatusCode);

        using var created = await http.SendAsync(Put(id, "scripts/new.lua", "-- hi\n", create: true));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("-- hi\n", File.ReadAllText(workspace.Combine("alpha", "scripts", "new.lua")));

        // Creating over an existing file with If-None-Match: * must not overwrite it.
        using var again = await http.SendAsync(Put(id, "scripts/new.lua", "-- other\n", create: true));
        Assert.Equal((HttpStatusCode)428, again.StatusCode);
        Assert.Equal("-- hi\n", File.ReadAllText(workspace.Combine("alpha", "scripts", "new.lua")));
    }

    [Fact]
    public async Task Save_RejectsReadOnlyMods_BadPaths_AndUnsupportedTypes()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        using var installed = new TempDir();
        Fixtures.WriteModZip(installed.Path, "zipped", "1.0.0", "base");
        workspace.Write("alpha/info.json", InfoFor("alpha"));
        workspace.Write("alpha/.hidden.lua", "x");
        var secretName = $"fms-secret-{Guid.NewGuid():N}.lua";
        var secret = Path.Combine(workspace.Path, secretName);
        File.WriteAllText(secret, "keep");

        var context = new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false);
        await using var ui = await UiServer.StartAsync(context, new UiOptions(Port: 0, ExtraModFolders: new[] { installed.Path }, WorkspaceDirectory: workspace.Path));
        using var http = Client(ui);
        try
        {
            var id = await WorkspaceModId(http, "alpha");
            var zipped = await WorkspaceModId(http, "zipped");

            using var readOnly = await http.SendAsync(Put(zipped, "data.lua", "x", create: true));
            Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);

            foreach (var bad in new[] { "../" + secretName, "..\\" + secretName, "/etc/passwd", ".git/config", ".hidden.lua" })
            {
                using var r = await http.SendAsync(Put(id, bad, "pwned", create: true));
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            }

            using var exe = await http.SendAsync(Put(id, "run.exe", "x", create: true));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, exe.StatusCode);

            using var unknown = await http.SendAsync(Put("deadbeef0000", "data.lua", "x", create: true));
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            Assert.Equal("keep", File.ReadAllText(secret));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public async Task Save_RejectsInvalidUtf8_AndOversizedBodies()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("alpha/info.json", InfoFor("alpha"));
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await WorkspaceModId(http, "alpha");

        var invalid = new HttpRequestMessage(HttpMethod.Put, $"/api/mods/{id}/file?path=a.lua") { Content = new ByteArrayContent(new byte[] { 0xFF, 0xFE, 0xFA }) };
        invalid.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var bad = await http.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var big = await http.SendAsync(Put(id, "big.lua", new string('x', 2 * 1024 * 1024 + 1), create: true));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.False(File.Exists(workspace.Combine("alpha", "big.lua")));
    }

    [Fact]
    public async Task Events_ReportFileChanges_AndNewMods()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("alpha/info.json", InfoFor("alpha"));
        await using var ui = await Start(project, workspace);
        Assert.True(ui.WatchedFolders >= 1);

        using var http = Client(ui);
        http.Timeout = TimeSpan.FromSeconds(30);
        var id = await WorkspaceModId(http, "alpha");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        async Task<JsonElement> NextEvent(Func<JsonElement, bool> match)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("stream ended");
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var json = JsonDocument.Parse(line[6..]).RootElement.Clone();
                if (match(json)) return json;
            }
        }

        File.WriteAllText(workspace.Combine("alpha", "control.lua"), "-- hello\n");
        var changed = await NextEvent(e => e.GetProperty("type").GetString() == "changed");
        Assert.Equal(id, changed.GetProperty("modId").GetString());
        Assert.Equal("control.lua", changed.GetProperty("path").GetString());

        workspace.Write("gamma/info.json", InfoFor("gamma"));
        await NextEvent(e => e.GetProperty("type").GetString() == "mods");
        Assert.Contains("gamma", (await GetJson(http, "/api/mods")).EnumerateArray().Select(m => m.GetProperty("name").GetString()));
    }

    [Fact]
    public void ProjectConfig_ParsesWorkspace_AndDefaultsToMods()
    {
        var withValue = ProjectConfig.Parse("""{ "workspace": "dev-mods" }""");
        var context = new ProjectContext("/p", withValue, configFileExists: true);
        Assert.EndsWith("dev-mods", context.WorkspaceDirectory);

        var plain = new ProjectContext("/p", new ProjectConfig(), configFileExists: false);
        Assert.EndsWith("mods", plain.WorkspaceDirectory);
    }

    [Fact]
    public async Task ChangeFeed_DeliversToEverySubscriber_AndForgetsDisposedOnes()
    {
        var feed = new ChangeFeed();
        using var first = feed.Subscribe();
        var second = feed.Subscribe();
        Assert.Equal(2, feed.SubscriberCount);

        feed.Publish(new { type = "mods" });
        Assert.Contains("\"type\":\"mods\"", await first.Reader.ReadAsync());
        Assert.Contains("\"type\":\"mods\"", await second.Reader.ReadAsync());

        second.Dispose();
        Assert.Equal(1, feed.SubscriberCount);
    }
}
