using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Core.Lua;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Black-box tests of GET /api/templates and POST /api/workspace/new, written from the feature contract only.</summary>
public class ScaffoldApiTests
{
    private static readonly string[] TemplateIds = { "empty", "item-recipe", "entity", "runtime-script" };

    private sealed record Resp(HttpStatusCode Status, string Raw)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement.Clone();
        public string? Str(string property) => Json.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private static async Task<RunningUi> Start(TempDir project, string workspacePath) =>
        await UiServer.StartAsync(
            new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false),
            new UiOptions(Port: 0, WorkspaceDirectory: workspacePath));

    private static HttpClient Client(RunningUi ui, bool withToken = true)
    {
        var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        if (withToken) http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        return http;
    }

    private static async Task<Resp> PostRaw(HttpClient http, string body, string contentType = "application/json", Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/new")
        {
            Content = new StringContent(body, new UTF8Encoding(false), contentType)
        };
        tweak?.Invoke(request);
        using var response = await http.SendAsync(request);
        return new Resp(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Task<Resp> Post(HttpClient http, object body, Action<HttpRequestMessage>? tweak = null) =>
        PostRaw(http, JsonSerializer.Serialize(body), tweak: tweak);

    private static async Task<List<JsonElement>> Mods(HttpClient http)
    {
        var text = await http.GetStringAsync("/api/mods");
        return JsonDocument.Parse(text).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static void AssertError(Resp r, HttpStatusCode expected)
    {
        Assert.Equal(expected, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Str("error")), $"expected JSON {{\"error\":...}}, got: {r.Raw}");
    }

    private static List<string> DiskFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();

    private static List<string> Entries(string dir) =>
        Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToList() : new();

    /// <summary>Workspace nested inside a root folder that also holds a canary file; anything escaping would show up in the root.</summary>
    private sealed class Env : IDisposable
    {
        public TempDir Project = new();
        public TempDir Root = new();
        public string Workspace = "";
        public string CanaryName = Guid.NewGuid().ToString("N") + ".txt";
        public RunningUi Ui = null!;
        public HttpClient Http = null!;

        public static async Task<Env> Create(bool createWorkspace = true)
        {
            var env = new Env();
            env.Workspace = env.Root.Combine("ws");
            if (createWorkspace) Directory.CreateDirectory(env.Workspace);
            File.WriteAllText(env.Root.Combine(env.CanaryName), "keep");
            env.Ui = await Start(env.Project, env.Workspace);
            env.Http = Client(env.Ui);
            return env;
        }

        public void AssertNothingEscaped()
        {
            Assert.Equal("keep", File.ReadAllText(Root.Combine(CanaryName)));
            Assert.Equal(new[] { CanaryName, "ws" }.OrderBy(n => n, StringComparer.Ordinal).ToList(), Entries(Root.Path));
            Assert.Empty(Entries(Project.Path));
        }

        public void Dispose()
        {
            Http.Dispose();
            Ui.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Project.Dispose();
            Root.Dispose();
        }
    }

    // ------------------------------------------------------------------ GET /api/templates

    [Fact]
    public async Task Templates_ListsTheFourTemplatesInOrder_MatchingCore()
    {
        using var env = await Env.Create();
        var text = await env.Http.GetStringAsync("/api/templates");
        var items = JsonDocument.Parse(text).RootElement.EnumerateArray().ToList();
        Assert.Equal(TemplateIds.ToList(), items.Select(i => i.GetProperty("id").GetString()!).ToList());
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(ModScaffolder.Templates[i].Title, items[i].GetProperty("title").GetString());
            Assert.Equal(ModScaffolder.Templates[i].Description, items[i].GetProperty("description").GetString());
            Assert.False(items[i].TryGetProperty("Id", out _), "camelCase property names");
        }
    }

    [Fact]
    public async Task Templates_ForeignHostIsRejected()
    {
        using var env = await Env.Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/templates");
        request.Headers.Host = "attacker.example";
        using var response = await env.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------ security

    [Fact]
    public async Task Post_RequiresToken_AndRejectsForeignOrigin_WritingNothing()
    {
        using var env = await Env.Create();
        using var anonymous = Client(env.Ui, withToken: false);
        AssertError0(await Post(anonymous, new { name = "sneaky" }), HttpStatusCode.Forbidden);

        using var wrongToken = new HttpClient { BaseAddress = new Uri(env.Ui.Url) };
        wrongToken.DefaultRequestHeaders.Add("X-FMS-Token", "not-the-token");
        AssertError0(await Post(wrongToken, new { name = "sneaky" }), HttpStatusCode.Forbidden);

        var foreign = await Post(env.Http, new { name = "sneaky" }, r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.example"));
        AssertError0(foreign, HttpStatusCode.Forbidden);

        var foreignHost = await Post(env.Http, new { name = "sneaky" }, r => r.Headers.Host = "attacker.example");
        AssertError0(foreignHost, HttpStatusCode.Forbidden);

        Assert.Empty(Entries(env.Workspace));
        env.AssertNothingEscaped();

        // The same origin is fine.
        var ok = await Post(env.Http, new { name = "friendly" }, r => r.Headers.TryAddWithoutValidation("Origin", env.Ui.Url.TrimEnd('/')));
        Assert.Equal(HttpStatusCode.Created, ok.Status);
    }

    private static void AssertError0(Resp r, HttpStatusCode expected) => Assert.Equal(expected, r.Status);

    [Fact]
    public async Task Get_OnTheNewEndpoint_IsNotAllowed_AndCreatesNothing()
    {
        using var env = await Env.Create();
        using var response = await env.Http.GetAsync("/api/workspace/new?name=viaget");
        Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, "got " + response.StatusCode);
        Assert.Empty(Entries(env.Workspace));
    }

    [Fact]
    public async Task TraversalAndAbsoluteNames_AreRejected_AndNothingIsWrittenOutsideTheWorkspace()
    {
        using var env = await Env.Create();
        var escape = "/tmp/fms-evil-" + Guid.NewGuid().ToString("N");
        foreach (var name in new[] { "../" + env.CanaryName, "..\\" + env.CanaryName, "../evil", "a/../../evil", "..", ".", escape, "a/b", "a\\b", "%2e%2e/evil", "..%2fevil", "C:\\evil" })
        {
            var r = await Post(env.Http, new { name });
            AssertError(r, HttpStatusCode.BadRequest);
        }
        Assert.False(Directory.Exists(escape) || File.Exists(escape));
        Assert.Empty(Entries(env.Workspace).Where(e => e != ".fmstudio"));
        env.AssertNothingEscaped();
    }

    // ------------------------------------------------------------------ success

    [Theory]
    [InlineData("empty")]
    [InlineData("item-recipe")]
    [InlineData("entity")]
    [InlineData("runtime-script")]
    public async Task Create_Succeeds_AndTheModIsListedAsEditableWorkspaceMod(string template)
    {
        using var env = await Env.Create();
        var r = await Post(env.Http, new { name = "api-" + template, template, title = "API \"Title\"", author = "Au\\thor", description = "Desc ]] {{x}} 100%" });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal("api-" + template, r.Str("name"));
        Assert.Equal("0.1.0", r.Str("version"));
        var folder = Path.Combine(env.Workspace, "api-" + template);
        Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(r.Str("folder")!));
        Assert.Equal(JsonValueKind.Number, r.Json.GetProperty("files").ValueKind);
        Assert.Equal(DiskFiles(folder).Count, r.Json.GetProperty("files").GetInt32());

        var validation = ModValidator.Validate(folder);
        Assert.True(validation.Issues.Count == 0, string.Join("; ", validation.Issues.Select(i => i.Code + " " + i.Message)));
        using (var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "info.json"))))
        {
            Assert.Equal("API \"Title\"", info.RootElement.GetProperty("title").GetString());
            Assert.Equal("Au\\thor", info.RootElement.GetProperty("author").GetString());
            Assert.Equal("Desc ]] {{x}} 100%", info.RootElement.GetProperty("description").GetString());
        }

        var mods = await Mods(env.Http);
        var mod = mods.Single(m => m.GetProperty("name").GetString() == "api-" + template);
        Assert.Equal(r.Str("id"), mod.GetProperty("id").GetString());
        Assert.Equal("workspace", mod.GetProperty("source").GetString());
        Assert.True(mod.GetProperty("editable").GetBoolean());

        // The new files can be read through the file API (and so edited in the UI).
        var id = r.Str("id");
        using var info2 = await env.Http.GetAsync($"/api/mods/{id}/file?path=info.json");
        Assert.Equal(HttpStatusCode.OK, info2.StatusCode);
        var entry = template == "runtime-script" ? "control.lua" : "data.lua";
        using var lua = await env.Http.GetAsync($"/api/mods/{id}/file?path={entry}");
        Assert.Equal(HttpStatusCode.OK, lua.StatusCode);
        env.AssertNothingEscaped();
    }

    [Fact]
    public async Task Create_UsesDefaults_WhenFieldsAreOmittedNullOrBlank_AndIgnoresUnknownProperties()
    {
        using var env = await Env.Create();
        var a = await PostRaw(env.Http, "{\"name\":\"just-name\"}");
        Assert.Equal(HttpStatusCode.Created, a.Status);
        var b = await PostRaw(env.Http, "{\"name\":\"nulls-here\",\"template\":null,\"title\":null,\"author\":null,\"description\":null}");
        Assert.Equal(HttpStatusCode.Created, b.Status);
        var c = await PostRaw(env.Http, "{\"name\":\"blanks-here\",\"template\":\"empty\",\"title\":\"  \",\"author\":\"\",\"description\":\" \",\"junk\":{\"x\":1}}");
        Assert.Equal(HttpStatusCode.Created, c.Status);

        using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(env.Workspace, "just-name", "info.json")));
        Assert.Equal("Just Name", info.RootElement.GetProperty("title").GetString());
        Assert.Equal("A Factorio mod created with Factorio Mod Studio.", info.RootElement.GetProperty("description").GetString());
        Assert.False(string.IsNullOrWhiteSpace(info.RootElement.GetProperty("author").GetString()));
        using var info3 = JsonDocument.Parse(File.ReadAllText(Path.Combine(env.Workspace, "blanks-here", "info.json")));
        Assert.Equal("Blanks Here", info3.RootElement.GetProperty("title").GetString());
        Assert.True(File.Exists(Path.Combine(env.Workspace, "just-name", "data.lua")));
        Assert.True(ModValidator.Validate(Path.Combine(env.Workspace, "nulls-here")).Issues.Count == 0);
    }

    [Fact]
    public async Task Create_CreatesTheWorkspaceFolderIfMissing()
    {
        using var env = await Env.Create(createWorkspace: false);
        Assert.False(Directory.Exists(env.Workspace));
        var r = await Post(env.Http, new { name = "first-ever" });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.True(File.Exists(Path.Combine(env.Workspace, "first-ever", "info.json")));
        Assert.Contains("first-ever", (await Mods(env.Http)).Select(m => m.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Create_LeavesNoStagingDirectoryContentsBehind()
    {
        using var env = await Env.Create();
        foreach (var t in TemplateIds) Assert.Equal(HttpStatusCode.Created, (await Post(env.Http, new { name = "stage-" + t, template = t })).Status);
        AssertError(await Post(env.Http, new { name = "stage-empty" }), HttpStatusCode.Conflict);
        AssertError(await Post(env.Http, new { name = "x", title = "a\nb" }), HttpStatusCode.BadRequest);
        var entries = Entries(env.Workspace);
        Assert.Equal(4, entries.Count(e => e != ".fmstudio"));
        if (entries.Contains(".fmstudio")) Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(env.Workspace, ".fmstudio"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Create_PublishesAModsEvent()
    {
        using var env = await Env.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var events = await env.Http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(cts.Token));

        var r = await Post(env.Http, new { name = "evented" });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("event stream ended");
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (JsonDocument.Parse(line[6..]).RootElement.GetProperty("type").GetString() == "mods") break;
        }
    }

    [Fact]
    public async Task Create_NastyFieldValues_RoundTripThroughHttp()
    {
        using var env = await Env.Create();
        var values = ScaffoldTests.NastyValues;
        var i = 0;
        foreach (var value in values)
        {
            var template = TemplateIds[i % 4];
            var name = "nasty-" + i++;
            var r = await Post(env.Http, new { name, template, title = value, author = value, description = value });
            Assert.True(r.Status == HttpStatusCode.Created, $"[{value}] {r.Status} {r.Raw}");
            var folder = Path.Combine(env.Workspace, name);
            Assert.True(ModValidator.Validate(folder).Issues.Count == 0, $"[{value}] validator");
            using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "info.json")));
            Assert.Equal(value.Trim(), info.RootElement.GetProperty("title").GetString());
            foreach (var lua in DiskFiles(folder).Where(f => f.EndsWith(".lua", StringComparison.Ordinal)))
                Assert.Empty(LuaDiagnostics.Check(File.ReadAllText(Path.Combine(folder, lua))));
        }
    }

    [Fact]
    public async Task Create_RawUnicodeInJsonBody_AndEscapedUnicode_AreEqual()
    {
        using var env = await Env.Create();
        var raw = await PostRaw(env.Http, "{\"name\":\"uni-a\",\"title\":\"Ünïcødé ✓ \\ud83d\\ude00\"}");
        var esc = await PostRaw(env.Http, "{\"name\":\"uni-b\",\"title\":\"\\u00dcn\\u00efc\\u00f8d\\u00e9 \\u2713 \uD83D\uDE00\"}");
        Assert.Equal(HttpStatusCode.Created, raw.Status);
        Assert.Equal(HttpStatusCode.Created, esc.Status);
        string TitleOf(string n) => JsonDocument.Parse(File.ReadAllText(Path.Combine(env.Workspace, n, "info.json"))).RootElement.GetProperty("title").GetString()!;
        Assert.Equal("Ünïcødé ✓ \U0001F600", TitleOf("uni-a"));
        Assert.Equal(TitleOf("uni-a"), TitleOf("uni-b"));
    }

    // ------------------------------------------------------------------ 400

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("a.b")]
    [InlineData("héllo")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("lpt3")]
    [InlineData("a\\u0000b")]
    [InlineData("a\\nb")]
    public async Task InvalidNames_Return400_AndWriteNothing(string name)
    {
        using var env = await Env.Create();
        var r = await PostRaw(env.Http, "{\"name\":\"" + name + "\"}");
        AssertError(r, HttpStatusCode.BadRequest);
        Assert.Empty(Entries(env.Workspace).Where(e => e != ".fmstudio"));
        env.AssertNothingEscaped();
    }

    [Fact]
    public async Task NameOver100Chars_Returns400_100IsAccepted()
    {
        using var env = await Env.Create();
        AssertError(await Post(env.Http, new { name = new string('n', 101) }), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Created, (await Post(env.Http, new { name = new string('n', 100) })).Status);
    }

    [Fact]
    public async Task UnknownTemplate_Returns400()
    {
        using var env = await Env.Create();
        foreach (var t in new[] { "nope", "Entity", "../empty", "item recipe" })
        {
            var r = await Post(env.Http, new { name = "tmpl", template = t });
            Assert.True(r.Status == HttpStatusCode.BadRequest, $"template [{t}] -> {(int)r.Status}");
            Assert.False(string.IsNullOrWhiteSpace(r.Str("error")));
        }
        Assert.Empty(Entries(env.Workspace).Where(e => e != ".fmstudio"));
    }

    [Fact]
    public async Task InvalidFields_Return400()
    {
        using var env = await Env.Create();
        var bad = new (string Field, string Value)[]
        {
            ("title", new string('t', 101)), ("author", new string('a', 101)), ("description", new string('d', 501)),
            ("title", "a\nb"), ("title", "a\u0000b"), ("author", "a\tb"), ("description", "a\r\nb"), ("description", "x\u007fy"), ("title", "x\u001fy"),
        };
        foreach (var (field, value) in bad)
        {
            var body = new Dictionary<string, object?> { ["name"] = "badfield", ["template"] = "empty", [field] = value };
            AssertError(await Post(env.Http, body), HttpStatusCode.BadRequest);
        }
        Assert.Empty(Entries(env.Workspace).Where(e => e != ".fmstudio"));
        // The boundaries themselves are accepted.
        var okBody = new { name = "boundaries", title = new string('t', 100), author = new string('a', 100), description = new string('d', 500) };
        Assert.Equal(HttpStatusCode.Created, (await Post(env.Http, okBody)).Status);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"name\"]")]
    [InlineData("\"name\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":42}")]
    [InlineData("{\"name\":[\"a\"]}")]
    [InlineData("{\"name\":{\"x\":1}}")]
    [InlineData("{\"name\":\"ok\",\"title\":42}")]
    [InlineData("{\"name\":\"ok\",\"template\":[]}")]
    [InlineData("{\"name\":\"ok\"} trailing")]
    [InlineData("{\"name\":\"ok\",")]
    public async Task MalformedOrMissingBodies_Return400_NeverCreateAnything(string body)
    {
        using var env = await Env.Create();
        var r = await PostRaw(env.Http, body);
        Assert.True(r.Status == HttpStatusCode.BadRequest, $"body [{body}] -> {(int)r.Status} {r.Raw}");
        Assert.False(string.IsNullOrWhiteSpace(r.Str("error")), r.Raw);
        Assert.Empty(Entries(env.Workspace).Where(e => e != ".fmstudio"));
        env.AssertNothingEscaped();
    }

    [Fact]
    public async Task NoBodyAtAll_Returns400()
    {
        using var env = await Env.Create();
        using var response = await env.Http.PostAsync("/api/workspace/new", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var wrongType = await env.Http.PostAsync("/api/workspace/new", new StringContent("name=x", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.True(wrongType.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType, "got " + wrongType.StatusCode);
        Assert.Empty(Entries(env.Workspace).Where(e => e != ".fmstudio"));
    }

    [Fact]
    public async Task BodiesOver16KB_Return413_AndSmallOnesStillWork()
    {
        using var env = await Env.Create();
        var big = await PostRaw(env.Http, "{\"name\":\"too-big\",\"description\":\"" + new string('d', 20 * 1024) + "\"}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.Status);
        var huge = await PostRaw(env.Http, "{\"name\":\"too-huge\",\"description\":\"" + new string('d', 3 * 1024 * 1024) + "\"}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.Status);
        var padded = await PostRaw(env.Http, "{\"name\":\"padded\"" + new string(' ', 17 * 1024) + "}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, padded.Status);
        Assert.Empty(Entries(env.Workspace).Where(e => e != ".fmstudio"));
        Assert.Equal(HttpStatusCode.Created, (await Post(env.Http, new { name = "small-one" })).Status);
    }

    // ------------------------------------------------------------------ 409

    [Fact]
    public async Task Duplicate_Returns409_WithExistingId_AndKeepsTheOriginal()
    {
        using var env = await Env.Create();
        var first = await Post(env.Http, new { name = "dupe", template = "entity", title = "Original" });
        Assert.Equal(HttpStatusCode.Created, first.Status);
        var before = DiskFiles(Path.Combine(env.Workspace, "dupe"));
        var infoBefore = File.ReadAllText(Path.Combine(env.Workspace, "dupe", "info.json"));

        foreach (var t in TemplateIds)
        {
            var second = await Post(env.Http, new { name = "dupe", template = t, title = "Replacement" });
            AssertError(second, HttpStatusCode.Conflict);
            Assert.Equal(first.Str("id"), second.Str("existing"));
        }
        Assert.Equal(before, DiskFiles(Path.Combine(env.Workspace, "dupe")));
        Assert.Equal(infoBefore, File.ReadAllText(Path.Combine(env.Workspace, "dupe", "info.json")));
        Assert.Equal(1, (await Mods(env.Http)).Count(m => m.GetProperty("name").GetString() == "dupe"));
    }

    [Fact]
    public async Task ExistingListedMod_PlacedByHand_Returns409WithItsId()
    {
        using var env = await Env.Create();
        Directory.CreateDirectory(Path.Combine(env.Workspace, "handmade"));
        File.WriteAllText(Path.Combine(env.Workspace, "handmade", "info.json"),
            """{ "name": "handmade", "version": "1.0.0", "title": "T", "author": "a", "factorio_version": "2.0", "dependencies": ["base"] }""");
        var existingId = (await Mods(env.Http)).Single(m => m.GetProperty("name").GetString() == "handmade").GetProperty("id").GetString();
        var r = await Post(env.Http, new { name = "handmade" });
        AssertError(r, HttpStatusCode.Conflict);
        Assert.Equal(existingId, r.Str("existing"));
        Assert.Equal("1.0.0", JsonDocument.Parse(File.ReadAllText(Path.Combine(env.Workspace, "handmade", "info.json"))).RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task ExistingNonModFolderOrFile_Returns409_AndIsNotTouched()
    {
        using var env = await Env.Create();
        Directory.CreateDirectory(Path.Combine(env.Workspace, "plainfolder"));
        File.WriteAllText(Path.Combine(env.Workspace, "plainfolder", "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(env.Workspace, "plainfile"), "I am a file");

        var folder = await Post(env.Http, new { name = "plainfolder" });
        AssertError(folder, HttpStatusCode.Conflict);
        var file = await Post(env.Http, new { name = "plainfile" });
        AssertError(file, HttpStatusCode.Conflict);
        Assert.Equal(new[] { "notes.txt" }, DiskFiles(Path.Combine(env.Workspace, "plainfolder")).ToArray());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(env.Workspace, "plainfolder", "notes.txt")));
        Assert.Equal("I am a file", File.ReadAllText(Path.Combine(env.Workspace, "plainfile")));
    }

    [Fact]
    public async Task ConcurrentPosts_OfTheSameName_ExactlyOneCreated_OthersConflict()
    {
        using var env = await Env.Create();
        var tasks = Enumerable.Range(0, 10).Select(i => Post(env.Http, new { name = "racing", template = TemplateIds[i % 4], title = "R" + i })).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(9, results.Count(r => r.Status == HttpStatusCode.Conflict));
        var winner = results.Single(r => r.Status == HttpStatusCode.Created);
        var folder = Path.Combine(env.Workspace, "racing");
        Assert.Equal(DiskFiles(folder).Count, winner.Json.GetProperty("files").GetInt32());
        Assert.True(ModValidator.Validate(folder).Issues.Count == 0);
        Assert.Equal(1, (await Mods(env.Http)).Count(m => m.GetProperty("name").GetString() == "racing"));
    }

    [Fact]
    public async Task ConcurrentPosts_OfDifferentNames_AllSucceed()
    {
        using var env = await Env.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Post(env.Http, new { name = "multi-" + i, template = TemplateIds[i % 4] })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Equal(12, (await Mods(env.Http)).Count(m => m.GetProperty("name").GetString()!.StartsWith("multi-", StringComparison.Ordinal)));
        Assert.Equal(12, results.Select(r => r.Str("id")).Distinct().Count());
    }

    [Fact]
    public async Task CreatedMod_CanBeEditedThroughTheWorkspaceFileApi()
    {
        using var env = await Env.Create();
        var r = await Post(env.Http, new { name = "editable-one" });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        var id = r.Str("id");
        using var read = await env.Http.GetAsync($"/api/mods/{id}/file?path=data.lua");
        var etag = read.Headers.ETag!.ToString();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/mods/{id}/file?path=data.lua") { Content = new StringContent("-- edited\n", new UTF8Encoding(false), "text/plain") };
        put.Headers.TryAddWithoutValidation("If-Match", etag);
        using var saved = await env.Http.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("-- edited\n", File.ReadAllText(Path.Combine(env.Workspace, "editable-one", "data.lua")));
    }
}
