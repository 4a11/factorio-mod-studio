using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Black-box tests of /api/mods/{id}/locale endpoints, written from the localization-editor contract only.</summary>
public class LocaleEditorApiTests
{
    private static string InfoFor(string name) =>
        $$"""{ "name": "{{name}}", "version": "1.0.0", "title": "T", "author": "a", "factorio_version": "2.0", "dependencies": ["base"] }""";

    private sealed record Resp(HttpStatusCode Status, string Raw, System.Net.Http.Headers.HttpResponseHeaders Headers, System.Net.Http.Headers.HttpContentHeaders ContentHeaders)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement.Clone();
        public string? Str(string p) => Json.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public string? Header(string name) => Headers.TryGetValues(name, out var v) ? string.Join(",", v) : ContentHeaders.TryGetValues(name, out var c) ? string.Join(",", c) : null;
    }

    private sealed class Env : IDisposable
    {
        public TempDir Project = new();
        public TempDir Workspace = new();
        public TempDir Installed = new();
        public RunningUi Ui = null!;
        public HttpClient Http = null!;

        public static async Task<Env> Create(Action<Env>? seed = null)
        {
            var env = new Env();
            seed?.Invoke(env);
            env.Ui = await UiServer.StartAsync(
                new ProjectContext(env.Project.Path, new ProjectConfig(), configFileExists: false),
                new UiOptions(Port: 0, ExtraModFolders: new[] { env.Installed.Path }, WorkspaceDirectory: env.Workspace.Path));
            env.Http = Client(env.Ui);
            env.Http.Timeout = TimeSpan.FromSeconds(60);
            return env;
        }

        public void Dispose()
        {
            Http?.Dispose();
            Ui?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Project.Dispose(); Workspace.Dispose(); Installed.Dispose();
        }
    }

    private static HttpClient Client(RunningUi ui, bool token = true)
    {
        var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        if (token) http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        return http;
    }

    private static async Task<string> ModId(HttpClient http, string name)
    {
        var text = await http.GetStringAsync("/api/mods");
        return JsonDocument.Parse(text).RootElement.EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    private static async Task<Resp> Send(HttpClient http, HttpMethod method, string url, string? body = null, string? ifMatch = null, Action<HttpRequestMessage>? tweak = null, string contentType = "application/json")
    {
        using var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = new StringContent(body, new UTF8Encoding(false), contentType);
        if (ifMatch != null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        tweak?.Invoke(req);
        using var res = await http.SendAsync(req);
        return new Resp(res.StatusCode, await res.Content.ReadAsStringAsync(), res.Headers, res.Content.Headers);
    }

    private static Task<Resp> Get(HttpClient http, string url) => Send(http, HttpMethod.Get, url);

    private static string Changes(params (string S, string K, string? V)[] c) =>
        JsonSerializer.Serialize(new { changes = c.Select(x => new { section = x.S, key = x.K, value = x.V }).ToArray() });

    private static Task<Resp> Put(HttpClient http, string id, string lang, string body, string? rev, Action<HttpRequestMessage>? tweak = null) =>
        Send(http, HttpMethod.Put, $"/api/mods/{id}/locale/{lang}", body, rev == null ? null : "\"" + rev + "\"", tweak);

    private static Task<Resp> Post(HttpClient http, string id, string body, Action<HttpRequestMessage>? tweak = null) =>
        Send(http, HttpMethod.Post, $"/api/mods/{id}/locale", body, tweak: tweak);

    private static async Task<string> Revision(HttpClient http, string id, string lang) =>
        (await Get(http, $"/api/mods/{id}/locale/{lang}")).Str("revision")!;

    private static Env Seed(Env e)
    {
        e.Workspace.Write("alpha/info.json", InfoFor("alpha"));
        e.Workspace.Write("alpha/locale/en/main.cfg", "[a]\nk1=One\nk2=Two __1__\n[b]\nx=Hello\n");
        e.Workspace.Write("alpha/locale/de/main.cfg", "[a]\nk1=Eins\nz=Zusatz\n");
        e.Installed.Write("ro-mod_1.0.0/info.json", InfoFor("ro-mod"));
        e.Installed.Write("ro-mod_1.0.0/locale/en/main.cfg", "[a]\nk1=One\nk2=Two\n");
        e.Installed.Write("ro-mod_1.0.0/locale/de/main.cfg", "[a]\nk1=Eins\n");
        return e;
    }

    private static Task<Env> Standard() => Env.Create(e => Seed(e));

    private static string AlphaDe(Env e) => File.ReadAllText(e.Workspace.Combine("alpha", "locale", "de", "main.cfg"));

    private static string RoPath(Env e, params string[] rel) => e.Installed.Combine(new[] { "ro-mod_1.0.0" }.Concat(rel).ToArray());

    private static List<string> AllFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------ GET /locale

    [Fact]
    public async Task List_ReturnsSourceLanguage_Editable_AndSummaries()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var r = await Get(env.Http, $"/api/mods/{id}/locale");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var j = r.Json;
        Assert.Equal("en", j.GetProperty("sourceLanguage").GetString());
        Assert.True(j.GetProperty("editable").GetBoolean());
        var langs = j.GetProperty("languages").EnumerateArray().ToList();
        Assert.Equal(new[] { "de", "en" }, langs.Select(l => l.GetProperty("code").GetString()!).ToArray());
        var de = langs[0];
        Assert.Equal(3, de.GetProperty("total").GetInt32());
        Assert.Equal(1, de.GetProperty("translated").GetInt32());
        Assert.Equal(2, de.GetProperty("untranslated").GetInt32());
        Assert.Equal(0, de.GetProperty("identical").GetInt32());
        Assert.Equal(1, de.GetProperty("extra").GetInt32());
        Assert.Equal(0, de.GetProperty("placeholderProblems").GetInt32());
        Assert.False(j.TryGetProperty("SourceLanguage", out _));
    }

    [Fact]
    public async Task List_ReadOnlyMod_IsNotEditable_ButReadable()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "ro-mod");
        var r = await Get(env.Http, $"/api/mods/{id}/locale");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(r.Json.GetProperty("editable").GetBoolean());
        Assert.Equal(2, r.Json.GetProperty("languages").GetArrayLength());
        var t = await Get(env.Http, $"/api/mods/{id}/locale/de");
        Assert.Equal(HttpStatusCode.OK, t.Status);
    }

    [Fact]
    public async Task List_ModWithoutLocale_HasNullSourceAndNoLanguages()
    {
        using var env = await Env.Create(e => e.Workspace.Write("bare/info.json", InfoFor("bare")));
        var id = await ModId(env.Http, "bare");
        var r = await Get(env.Http, $"/api/mods/{id}/locale");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(JsonValueKind.Null, r.Json.GetProperty("sourceLanguage").ValueKind);
        Assert.Equal(0, r.Json.GetProperty("languages").GetArrayLength());
        Assert.True(r.Json.GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task List_UnknownMod_Is404_WithErrorBody()
    {
        using var env = await Standard();
        var r = await Get(env.Http, "/api/mods/deadbeef0000/locale");
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Str("error")), r.Raw);
    }

    [Fact]
    public async Task Get_RejectsForeignHost()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var r = await Send(env.Http, HttpMethod.Get, $"/api/mods/{id}/locale/de", tweak: q => q.Headers.Host = "attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        var l = await Send(env.Http, HttpMethod.Get, $"/api/mods/{id}/locale", tweak: q => q.Headers.Host = "attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, l.Status);
    }

    // ------------------------------------------------------------------ GET /locale/{lang}

    [Fact]
    public async Task Table_HasRowsSummaryRevision_ETag_AndNoStore()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var r = await Get(env.Http, $"/api/mods/{id}/locale/de");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var j = r.Json;
        Assert.Equal("en", j.GetProperty("sourceLanguage").GetString());
        Assert.Equal("de", j.GetProperty("language").GetString());
        var rev = j.GetProperty("revision").GetString()!;
        Assert.Equal(64, rev.Length);
        Assert.Equal("\"" + rev + "\"", r.Header("ETag"));
        Assert.Contains("no-store", r.Header("Cache-Control"));
        Assert.Equal(3, j.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal("de", j.GetProperty("summary").GetProperty("code").GetString());

        var rows = j.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(new[] { "k1", "k2", "x", "z" }, rows.Select(x => x.GetProperty("key").GetString()!).ToArray());
        var k1 = rows[0];
        Assert.Equal("a", k1.GetProperty("section").GetString());
        Assert.Equal("One", k1.GetProperty("source").GetString());
        Assert.Equal("Eins", k1.GetProperty("target").GetString());
        Assert.Equal("locale/en/main.cfg", k1.GetProperty("sourceFile").GetString());
        Assert.Equal("locale/de/main.cfg", k1.GetProperty("targetFile").GetString());
        Assert.Equal(2, k1.GetProperty("targetLine").GetInt32());
        Assert.False(k1.GetProperty("identical").GetBoolean());
        Assert.Equal(0, k1.GetProperty("placeholderMissing").GetArrayLength());
        Assert.Equal(0, k1.GetProperty("placeholderUnexpected").GetArrayLength());
        var k2 = rows[1];
        Assert.Equal(JsonValueKind.Null, k2.GetProperty("target").ValueKind);
        Assert.Equal(JsonValueKind.Null, k2.GetProperty("targetFile").ValueKind);
        Assert.Equal(JsonValueKind.Null, k2.GetProperty("targetLine").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[3].GetProperty("sourceFile").ValueKind);

        string StatusOf(JsonElement row)
        {
            var s = row.GetProperty("status");
            return s.ValueKind == JsonValueKind.String ? s.GetString()!.ToLowerInvariant() : new[] { "translated", "untranslated", "extra" }[s.GetInt32()];
        }
        Assert.Equal(new[] { "translated", "untranslated", "untranslated", "extra" }, rows.Select(StatusOf).ToArray());
    }

    [Fact]
    public async Task Table_ETagMatchesPutResponseAndSubsequentGet()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var put = await Put(env.Http, id, "de", Changes(("a", "k2", "Zwei __1__")), rev);
        Assert.Equal(HttpStatusCode.OK, put.Status);
        var newRev = put.Str("revision")!;
        Assert.NotEqual(rev, newRev);
        Assert.Equal("\"" + newRev + "\"", put.Header("ETag"));
        Assert.Equal(newRev, await Revision(env.Http, id, "de"));
    }

    [Fact]
    public async Task Table_InvalidMissingAndUnknown()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        foreach (var bad in new[] { "EN", "e", "toolonglang", "de_DE", "x--y", "zh-" })
        {
            var r = await Get(env.Http, $"/api/mods/{id}/locale/{bad}");
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        }
        foreach (var traversal in new[] { "..%2Fx", "..%2f..%2fetc", "de%2F..", "..%5Cx" })
        {
            var r = await Get(env.Http, $"/api/mods/{id}/locale/{traversal}");
            Assert.True(r.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, traversal + " -> " + r.Status);
        }
        var missing = await Get(env.Http, $"/api/mods/{id}/locale/ru");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        var unknown = await Get(env.Http, "/api/mods/deadbeef0000/locale/de");
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.False(string.IsNullOrWhiteSpace(unknown.Str("error")));
        Assert.False(string.IsNullOrWhiteSpace(missing.Str("error")));
    }

    [Fact]
    public async Task Table_ForModWithoutSourceLanguage_IsAnErrorNotA500()
    {
        using var env = await Env.Create(e => e.Workspace.Write("bare/info.json", InfoFor("bare")));
        var id = await ModId(env.Http, "bare");
        var r = await Get(env.Http, $"/api/mods/{id}/locale/de");
        Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, r.Status.ToString());
    }

    [Fact]
    public async Task Table_TranslationMemory_ComesFromOtherEditableWorkspaceMods_Only()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/info.json", InfoFor("alpha"));
            e.Workspace.Write("alpha/locale/en/main.cfg", "[a]\nhello=Hello\nbye=Goodbye\nwithph=Count __1__\nonlyro=ReadOnlyOnly\n");
            e.Workspace.Write("alpha/locale/de/main.cfg", "[a]\n");
            e.Workspace.Write("beta/info.json", InfoFor("beta"));
            e.Workspace.Write("beta/locale/en/main.cfg", "[x]\nh=Hello\nb=Goodbye\nc=Count __1__\n");
            e.Workspace.Write("beta/locale/de/main.cfg", "[x]\nh=Hallo\nb=Tschüss\nc=Anzahl\n");
            e.Workspace.Write("beta/locale/fr/main.cfg", "[x]\nh=Bonjour\n");
            e.Installed.Write("ro_1.0.0/info.json", InfoFor("ro"));
            e.Installed.Write("ro_1.0.0/locale/en/main.cfg", "[x]\nq=ReadOnlyOnly\n");
            e.Installed.Write("ro_1.0.0/locale/de/main.cfg", "[x]\nq=NurLesen\n");
        });
        var id = await ModId(env.Http, "alpha");
        var j = (await Get(env.Http, $"/api/mods/{id}/locale/de")).Json;
        var rows = j.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!);
        Assert.Equal("Hallo", rows["hello"].GetProperty("suggestion").GetString());
        Assert.Equal("Tschüss", rows["bye"].GetProperty("suggestion").GetString());
        Assert.Equal(JsonValueKind.Null, rows["withph"].GetProperty("suggestion").ValueKind);   // placeholder-incompatible candidate
        Assert.Equal(JsonValueKind.Null, rows["onlyro"].GetProperty("suggestion").ValueKind);   // read-only mods are not a source of memory
        Assert.Equal(0, j.GetProperty("summary").GetProperty("translated").GetInt32());          // memory never changes status

        var fr = (await Get(env.Http, $"/api/mods/{id}/locale/fr"));
        Assert.Equal(HttpStatusCode.NotFound, fr.Status);   // alpha has no fr
    }

    [Fact]
    public async Task Table_OwnModIsNotAMemorySourceTwice_ButOwnTranslatedRowsStillSuggest()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/info.json", InfoFor("alpha"));
            e.Workspace.Write("alpha/locale/en/main.cfg", "[a]\nk1=Save\nk2=Save\n");
            e.Workspace.Write("alpha/locale/de/main.cfg", "[a]\nk1=Speichern\n");
        });
        var id = await ModId(env.Http, "alpha");
        var rows = (await Get(env.Http, $"/api/mods/{id}/locale/de")).Json.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Speichern", rows[1].GetProperty("suggestion").GetString());
    }

    // ------------------------------------------------------------------ PUT: success paths

    [Fact]
    public async Task Put_AppliesChanges_ReturnsCounts_TouchedFiles_AndWritesTheFile()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var body = Changes(("a", "k1", "Eins!"), ("a", "k2", "Zwei __1__"), ("a", "z", null), ("b", "x", "Hallo"));
        var r = await Put(env.Http, id, "de", body, rev);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(2, r.Json.GetProperty("added").GetInt32());
        Assert.Equal(1, r.Json.GetProperty("updated").GetInt32());
        Assert.Equal(1, r.Json.GetProperty("removed").GetInt32());
        Assert.Equal(new[] { "locale/de/main.cfg" }, r.Json.GetProperty("touchedFiles").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("[a]\nk1=Eins!\nk2=Zwei __1__\n\n[b]\nx=Hallo\n", AlphaDe(env));
        Assert.Equal(await Revision(env.Http, id, "de"), r.Str("revision"));
    }

    [Fact]
    public async Task Put_NoOpBatch_KeepsRevision()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var r = await Put(env.Http, id, "de", Changes(("a", "k1", "Eins")), rev);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(rev, r.Str("revision"));
        Assert.Equal(0, r.Json.GetProperty("touchedFiles").GetArrayLength());
        var empty = await Put(env.Http, id, "de", "{\"changes\":[]}", rev);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Equal(rev, empty.Str("revision"));
    }

    [Fact]
    public async Task Put_CreatesFilesInNewLanguage_AndUnicodeRoundTrips()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var created = await Post(env.Http, id, "{\"code\":\"ru\"}");
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var rev = await Revision(env.Http, id, "ru");
        var r = await Put(env.Http, id, "ru", Changes(("a", "k1", "Один 😀 \\n [color=1,0,0]")), rev);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var disk = File.ReadAllText(env.Workspace.Combine("alpha", "locale", "ru", "main.cfg"), Encoding.UTF8);
        Assert.Equal("; Translation: ru\n\n[a]\nk1=Один 😀 \\n [color=1,0,0]\n", disk);
        var row = (await Get(env.Http, $"/api/mods/{id}/locale/ru")).Json.GetProperty("rows").EnumerateArray().First();
        Assert.Equal("Один 😀 \\n [color=1,0,0]", row.GetProperty("target").GetString());
        Assert.Equal(2, row.GetProperty("placeholderUnexpected").GetArrayLength());
    }

    [Fact]
    public async Task Put_FiresTheSseChangedEvent()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var events = await env.Http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(cts.Token));
        var put = await Put(env.Http, id, "de", Changes(("a", "k2", "Zwei __1__")), rev);
        Assert.Equal(HttpStatusCode.OK, put.Status);
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("stream ended");
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = JsonDocument.Parse(line[6..]).RootElement;
            if (json.GetProperty("type").GetString() == "changed" && json.GetProperty("modId").GetString() == id
                && (json.GetProperty("path").GetString() ?? "").Replace('\\', '/').StartsWith("locale/de/", StringComparison.Ordinal)) break;
        }
    }

    // ------------------------------------------------------------------ PUT: preconditions & errors

    [Fact]
    public async Task Put_WithoutIfMatch_Is428_AndWritesNothing()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var before = AlphaDe(env);
        var r = await Put(env.Http, id, "de", Changes(("a", "k2", "Zwei __1__")), null);
        Assert.Equal((HttpStatusCode)428, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Str("error")));
        Assert.Equal(before, AlphaDe(env));
    }

    [Fact]
    public async Task Put_StaleIfMatch_Is409_WithCurrentRevision_AndWritesNothing()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        File.AppendAllText(env.Workspace.Combine("alpha", "locale", "de", "main.cfg"), "; external edit\n");
        var current = await Revision(env.Http, id, "de");
        Assert.NotEqual(rev, current);
        var before = AlphaDe(env);
        var r = await Put(env.Http, id, "de", Changes(("a", "k2", "Zwei __1__")), rev);
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Str("error")));
        Assert.Equal(current, r.Str("revision"));
        Assert.Equal(before, AlphaDe(env));

        var garbage = await Put(env.Http, id, "de", Changes(("a", "k2", "x")), "not-a-revision");
        Assert.Equal(HttpStatusCode.Conflict, garbage.Status);
        Assert.Equal(before, AlphaDe(env));
    }

    [Fact]
    public async Task Put_SecondWriterWithTheOldEtag_Conflicts()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        Assert.Equal(HttpStatusCode.OK, (await Put(env.Http, id, "de", Changes(("a", "k2", "Zwei __1__")), rev)).Status);
        var second = await Put(env.Http, id, "de", Changes(("b", "x", "Hallo")), rev);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.DoesNotContain("Hallo", AlphaDe(env));
    }

    [Fact]
    public async Task Put_ConcurrentWritersWithSameEtag_ExactlyOneSucceeds()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var tasks = Enumerable.Range(0, 8).Select(i => Put(env.Http, id, "de", Changes(("a", "k2", "Zwei" + i + " __1__")), rev)).ToArray();
        var rs = await Task.WhenAll(tasks);
        Assert.Equal(1, rs.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(7, rs.Count(r => r.Status == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Put_ReadOnlyMod_Is403_AndWritesNothing()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "ro-mod");
        var rev = await Revision(env.Http, id, "de");
        var before = AllFiles(env.Installed.Path);
        var contentBefore = File.ReadAllText(RoPath(env, "locale", "de", "main.cfg"));
        var r = await Put(env.Http, id, "de", Changes(("a", "k2", "Zwei")), rev);
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.Equal(before, AllFiles(env.Installed.Path));
        Assert.Equal(contentBefore, File.ReadAllText(RoPath(env, "locale", "de", "main.cfg")));
        var noMatch = await Put(env.Http, id, "de", Changes(("a", "k2", "Zwei")), null);
        Assert.True(noMatch.Status is HttpStatusCode.Forbidden or (HttpStatusCode)428, noMatch.Status.ToString());
    }

    [Fact]
    public async Task Put_RequiresTokenAndSameOrigin()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var before = AlphaDe(env);
        var body = Changes(("a", "k2", "Zwei __1__"));

        using var anonymous = Client(env.Ui, token: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(anonymous, id, "de", body, rev)).Status);

        using var wrong = new HttpClient { BaseAddress = new Uri(env.Ui.Url) };
        wrong.DefaultRequestHeaders.Add("X-FMS-Token", "nope");
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(wrong, id, "de", body, rev)).Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await Put(env.Http, id, "de", body, rev, q => q.Headers.TryAddWithoutValidation("Origin", "http://evil.example"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(env.Http, id, "de", body, rev, q => q.Headers.Host = "attacker.example")).Status);
        Assert.Equal(before, AlphaDe(env));

        var ok = await Put(env.Http, id, "de", body, rev, q => q.Headers.TryAddWithoutValidation("Origin", env.Ui.Url.TrimEnd('/')));
        Assert.Equal(HttpStatusCode.OK, ok.Status);
    }

    [Fact]
    public async Task Post_RequiresTokenAndSameOrigin()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        using var anonymous = Client(env.Ui, token: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(anonymous, id, "{\"code\":\"fr\"}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(env.Http, id, "{\"code\":\"fr\"}", q => q.Headers.TryAddWithoutValidation("Origin", "http://evil.example"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(env.Http, id, "{\"code\":\"fr\"}", q => q.Headers.Host = "attacker.example")).Status);
        Assert.False(Directory.Exists(env.Workspace.Combine("alpha", "locale", "fr")));
        Assert.Equal(HttpStatusCode.Created, (await Post(env.Http, id, "{\"code\":\"fr\"}", q => q.Headers.TryAddWithoutValidation("Origin", env.Ui.Url.TrimEnd('/')))).Status);
    }

    [Fact]
    public async Task Put_BadRequests_InvalidJson_ShapeErrors_Validation_AndLanguage()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var before = AlphaDe(env);

        foreach (var bad in new[] { "{not json", "", "[]", "null", "{}", "{\"changes\":null}", "{\"changes\":{}}", "{\"changes\":[1]}", "{\"changes\":[{\"key\":\"k\",\"value\":\"v\"}]}", "{\"changes\":[{\"section\":\"s\",\"value\":\"v\"}]}", "{\"changes\":[{\"section\":\"s\",\"key\":\"k\",\"value\":5}]}" })
        {
            var r = await Put(env.Http, id, "de", bad, rev);
            Assert.True(r.Status == HttpStatusCode.BadRequest, $"body '{bad}' -> {r.Status}");
            Assert.False(string.IsNullOrWhiteSpace(r.Str("error")), r.Raw);
        }

        var invalidBatches = new[]
        {
            Changes(("a", "k2", "ok"), ("", "k", "v")),
            Changes(("a", "k2", "ok"), ("a]", "k", "v")),
            Changes(("a", "k2", "ok"), ("a", "k=1", "v")),
            Changes(("a", "k2", "ok"), ("a", " k", "v")),
            Changes(("a", "k2", "ok"), ("a", "k", "line1\nline2")),
            Changes(("a", "k2", "ok"), ("a", "k", "line1\r\nline2")),
            Changes(("a", "k2", "ok"), ("a", "k", new string('x', 65537))),
            Changes(("a", "k2", "ok"), (";c", "k", "v")),
            Changes(("a", "k2", "ok"), ("a", "#k", "v")),
        };
        foreach (var body in invalidBatches)
        {
            var r = await Put(env.Http, id, "de", body, rev);
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.False(string.IsNullOrWhiteSpace(r.Str("error")));
            Assert.Equal(before, AlphaDe(env));
        }

        var tooMany = Changes(Enumerable.Range(0, 5001).Select(i => ("bulk", "k" + i, (string?)"v")).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(env.Http, id, "de", tooMany, rev)).Status);
        Assert.Equal(before, AlphaDe(env));

        foreach (var lang in new[] { "EN", "e", "de_DE", "x--y" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(env.Http, id, lang, Changes(("a", "k", "v")), rev)).Status);
        Assert.Equal(before, AlphaDe(env));
    }

    [Fact]
    public async Task Put_UnknownModOrLanguage_Is404()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var noLang = await Put(env.Http, id, "ru", Changes(("a", "k", "v")), rev);
        Assert.Equal(HttpStatusCode.NotFound, noLang.Status);
        Assert.False(Directory.Exists(env.Workspace.Combine("alpha", "locale", "ru")));
        var noMod = await Put(env.Http, "deadbeef0000", "de", Changes(("a", "k", "v")), rev);
        Assert.Equal(HttpStatusCode.NotFound, noMod.Status);
    }

    [Fact]
    public async Task Put_PathTraversalInLanguage_WritesNothingOutsideTheLocaleDirectory()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var before = AllFiles(env.Workspace.Path);
        foreach (var lang in new[] { "..%2Fx", "..%2f..%2Fbeta", "de%2F..%2F..", "..%5Cx" })
        {
            var r = await Put(env.Http, id, lang, Changes(("a", "k", "pwned")), rev);
            Assert.True(r.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, lang + " -> " + r.Status);
        }
        Assert.Equal(before, AllFiles(env.Workspace.Path));
    }

    [Fact]
    public async Task Put_BodyOver2MB_Is413_AndWritesNothing()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var before = AlphaDe(env);
        var value = new string('x', 60000);
        var big = Changes(Enumerable.Range(0, 40).Select(i => ("bulk", "k" + i, (string?)value)).ToArray());
        Assert.True(big.Length > 2 * 1024 * 1024);
        var r = await Put(env.Http, id, "de", big, rev);
        Assert.Equal((HttpStatusCode)413, r.Status);
        Assert.Equal(before, AlphaDe(env));
        Assert.False(File.Exists(env.Workspace.Combine("alpha", "locale", "de", "extra.cfg")));
    }

    [Fact]
    public async Task Put_BodyJustUnder2MB_IsAccepted()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var value = new string('x', 60000);
        var body = Changes(Enumerable.Range(0, 32).Select(i => ("bulk", "k" + i, (string?)value)).ToArray());
        Assert.True(body.Length < 2 * 1024 * 1024 && body.Length > 1_800_000);
        var r = await Put(env.Http, id, "de", body, rev);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(32, r.Json.GetProperty("added").GetInt32());
    }

    [Fact]
    public async Task Put_ChunkedBodyOver2MB_IsAlsoRejected()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var value = new string('x', 60000);
        var big = Changes(Enumerable.Range(0, 40).Select(i => ("bulk", "k" + i, (string?)value)).ToArray());
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/mods/{id}/locale/de") { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big))) };
        req.Content.Headers.ContentType = new("application/json");
        req.Headers.TransferEncodingChunked = true;
        req.Headers.TryAddWithoutValidation("If-Match", "\"" + rev + "\"");
        using var res = await env.Http.SendAsync(req);
        Assert.Equal((HttpStatusCode)413, res.StatusCode);
    }

    [Fact]
    public async Task Put_ReparsePointLanguageDirectory_IsRejected_NothingWrittenThroughTheLink()
    {
        using var outside = new TempDir();
        outside.Write("main.cfg", "[a]\nk1=old\n");
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        try { Directory.CreateSymbolicLink(env.Workspace.Combine("alpha", "locale", "fr"), outside.Path); }
        catch (Exception) { return; }
        var before = File.ReadAllText(Path.Combine(outside.Path, "main.cfg"));
        var rev = "0".PadLeft(64, '0');
        var r = await Put(env.Http, id, "fr", Changes(("a", "k1", "pwned")), rev);
        Assert.True(r.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Forbidden, r.Status.ToString());
        Assert.Equal(HttpStatusCode.BadRequest == r.Status || r.Status != HttpStatusCode.OK, true);
        Assert.Equal(before, File.ReadAllText(Path.Combine(outside.Path, "main.cfg")));
        Assert.Equal(new[] { "main.cfg" }, AllFiles(outside.Path));

        // even with the revision the server itself would compute
        var current = await Get(env.Http, $"/api/mods/{id}/locale/fr");
        if (current.Status == HttpStatusCode.OK)
        {
            var r2 = await Put(env.Http, id, "fr", Changes(("a", "k1", "pwned")), current.Str("revision"));
            Assert.NotEqual(HttpStatusCode.OK, r2.Status);
            Assert.Equal(before, File.ReadAllText(Path.Combine(outside.Path, "main.cfg")));
        }
    }

    [Fact]
    public async Task Put_PreservesFileBytes_BomCrlfAndNoTrailingNewline()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/info.json", InfoFor("alpha"));
            e.Workspace.Write("alpha/locale/en/main.cfg", "[a]\nk1=One\nk2=Two\n");
            File.WriteAllBytes(e.Workspace.Combine("alpha", "locale", "de.tmp"), Array.Empty<byte>());
            File.Delete(e.Workspace.Combine("alpha", "locale", "de.tmp"));
            Directory.CreateDirectory(e.Workspace.Combine("alpha", "locale", "de"));
            File.WriteAllBytes(e.Workspace.Combine("alpha", "locale", "de", "main.cfg"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("; c\r\n[a]\r\nk1=Eins")).ToArray());
        });
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var r = await Put(env.Http, id, "de", Changes(("a", "k2", "Zwei")), rev);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var bytes = File.ReadAllBytes(env.Workspace.Combine("alpha", "locale", "de", "main.cfg"));
        Assert.Equal("﻿; c\r\n[a]\r\nk1=Eins\r\nk2=Zwei", Encoding.UTF8.GetString(bytes));
    }

    // ------------------------------------------------------------------ POST /locale

    [Fact]
    public async Task Post_CreatesLanguage_201_AndItAppearsInTheList()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var r = await Post(env.Http, id, "{\"code\":\"ru\"}");
        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal("ru", r.Str("code"));
        Assert.Equal("; Translation: ru\n", File.ReadAllText(env.Workspace.Combine("alpha", "locale", "ru", "main.cfg")));
        var list = (await Get(env.Http, $"/api/mods/{id}/locale")).Json.GetProperty("languages").EnumerateArray().ToList();
        var ru = list.Single(l => l.GetProperty("code").GetString() == "ru");
        Assert.Equal(0, ru.GetProperty("translated").GetInt32());
        Assert.Equal(ru.GetProperty("total").GetInt32(), ru.GetProperty("untranslated").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await Get(env.Http, $"/api/mods/{id}/locale/ru")).Status);
    }

    [Fact]
    public async Task Post_Errors()
    {
        using var env = await Env.Create(e => { Seed(e); e.Workspace.Write("bare/info.json", InfoFor("bare")); });
        var id = await ModId(env.Http, "alpha");
        var bare = await ModId(env.Http, "bare");
        var ro = await ModId(env.Http, "ro-mod");
        var before = AllFiles(env.Workspace.Path);

        foreach (var body in new[] { "{\"code\":\"EN\"}", "{\"code\":\"e\"}", "{\"code\":\"../x\"}", "{\"code\":\"\"}", "{\"code\":null}", "{\"code\":5}", "{}", "{not json", "", "[]", "{\"code\":\"x y\"}" })
        {
            var r = await Post(env.Http, id, body);
            Assert.True(r.Status == HttpStatusCode.BadRequest, $"{body} -> {r.Status}");
            Assert.False(string.IsNullOrWhiteSpace(r.Str("error")), r.Raw);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(env.Http, bare, "{\"code\":\"ru\"}")).Status);       // no source language
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(env.Http, ro, "{\"code\":\"ru\"}")).Status);          // read-only
        Assert.Equal(HttpStatusCode.NotFound, (await Post(env.Http, "deadbeef0000", "{\"code\":\"ru\"}")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(env.Http, id, "{\"code\":\"de\"}")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(env.Http, id, "{\"code\":\"en\"}")).Status);
        Assert.Equal(before, AllFiles(env.Workspace.Path));
        Assert.False(Directory.Exists(RoPath(env, "locale", "ru")));
        Assert.False(Directory.Exists(env.Workspace.Combine("bare", "locale")));
    }

    [Fact]
    public async Task Post_AlreadyExists_IsCaseInsensitive()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/info.json", InfoFor("alpha"));
            e.Workspace.Write("alpha/locale/en/main.cfg", "[a]\nk=1\n");
            e.Workspace.Write("alpha/locale/zh-CN/main.cfg", "[a]\nk=1\n");
        });
        var id = await ModId(env.Http, "alpha");
        var r = await Post(env.Http, id, "{\"code\":\"zh-cn\"}");
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
    }

    [Fact]
    public async Task Post_TwiceInParallel_OneCreated_RestConflict()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Post(env.Http, id, "{\"code\":\"pl\"}")));
        Assert.Equal(1, rs.Count(r => r.Status == HttpStatusCode.Created));
        Assert.True(rs.All(r => r.Status is HttpStatusCode.Created or HttpStatusCode.Conflict), string.Join(",", rs.Select(r => r.Status)));
    }

    [Fact]
    public async Task Post_GetOnPostEndpointDoesNotCreateAnything_AndPutOnListIsNotAllowed()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var before = AllFiles(env.Workspace.Path);
        var r = await Send(env.Http, HttpMethod.Put, $"/api/mods/{id}/locale", "{\"code\":\"fr\"}");
        Assert.True(r.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, r.Status.ToString());
        var d = await Send(env.Http, HttpMethod.Delete, $"/api/mods/{id}/locale/de");
        Assert.True(d.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, d.Status.ToString());
        Assert.Equal(before, AllFiles(env.Workspace.Path));
    }

    // ------------------------------------------------------------------ end-to-end flow

    [Fact]
    public async Task EndToEnd_NewLanguage_TranslateAll_ThenProgressIs100Percent()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        Assert.Equal(HttpStatusCode.Created, (await Post(env.Http, id, "{\"code\":\"fr\"}")).Status);
        var table = (await Get(env.Http, $"/api/mods/{id}/locale/fr")).Json;
        var rev = table.GetProperty("revision").GetString()!;
        var changes = table.GetProperty("rows").EnumerateArray()
            .Select(r => (r.GetProperty("section").GetString()!, r.GetProperty("key").GetString()!, (string?)(r.GetProperty("source").GetString() + " FR"))).ToArray();
        var put = await Put(env.Http, id, "fr", Changes(changes), rev);
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal(3, put.Json.GetProperty("added").GetInt32());
        var summary = (await Get(env.Http, $"/api/mods/{id}/locale/fr")).Json.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("translated").GetInt32());
        Assert.Equal(0, summary.GetProperty("untranslated").GetInt32());
    }

    [Fact]
    public async Task Put_ExtraRowRemoval_ViaNullValue()
    {
        using var env = await Standard();
        var id = await ModId(env.Http, "alpha");
        var rev = await Revision(env.Http, id, "de");
        var r = await Put(env.Http, id, "de", Changes(("a", "z", null)), rev);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(1, r.Json.GetProperty("removed").GetInt32());
        Assert.Equal("[a]\nk1=Eins\n", AlphaDe(env));
        Assert.Equal(0, (await Get(env.Http, $"/api/mods/{id}/locale/de")).Json.GetProperty("summary").GetProperty("extra").GetInt32());
    }
}
