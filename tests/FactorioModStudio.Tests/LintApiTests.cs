using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

public class LintApiTests
{
    private const string Info = """{ "name": "{0}", "version": "1.0.0", "title": "T", "author": "a", "factorio_version": "2.0", "dependencies": ["base"] }""";

    private static string InfoFor(string name) => Info.Replace("{0}", name);

    private sealed class Env : IDisposable
    {
        public TempDir Project = new();
        public TempDir Workspace = new();
        public TempDir Installed = new();
        public RunningUi Ui = null!;
        public HttpClient Http = null!;
        public HttpClient Anonymous = null!;
        public string AlphaId = "";
        public string ZippedId = "";

        public static async Task<Env> Create(Action<TempDir>? setup = null)
        {
            var env = new Env();
            env.Workspace.Write("alpha/info.json", InfoFor("alpha"));
            env.Workspace.Write("alpha/data.lua", "data:extend({})\n");
            setup?.Invoke(env.Workspace);
            Fixtures.WriteModZip(env.Installed.Path, "zipped", "1.0.0");
            env.Ui = await UiServer.StartAsync(
                new ProjectContext(env.Project.Path, new ProjectConfig(), configFileExists: false),
                new UiOptions(Port: 0, ExtraModFolders: new[] { env.Installed.Path }, WorkspaceDirectory: env.Workspace.Path));
            env.Http = new HttpClient { BaseAddress = new Uri(env.Ui.Url) };
            env.Http.DefaultRequestHeaders.Add("X-FMS-Token", env.Ui.Token);
            env.Anonymous = new HttpClient { BaseAddress = new Uri(env.Ui.Url) };
            var mods = JsonDocument.Parse(await env.Anonymous.GetStringAsync("/api/mods")).RootElement;
            env.AlphaId = mods.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "alpha").GetProperty("id").GetString()!;
            env.ZippedId = mods.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "zipped").GetProperty("id").GetString()!;
            return env;
        }

        public void Dispose()
        {
            Http.Dispose();
            Anonymous.Dispose();
            Ui.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Project.Dispose(); Workspace.Dispose(); Installed.Dispose();
        }
    }

    private static HttpRequestMessage Lint(string id, string path, byte[] body, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/lint?id={Uri.EscapeDataString(id)}&path={Uri.EscapeDataString(path)}")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return request;
    }

    private static HttpRequestMessage Lint(string id, string path, string body, string? origin = null) =>
        Lint(id, path, new UTF8Encoding(false).GetBytes(body), origin);

    private static async Task<JsonElement> Diagnostics(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return doc.GetProperty("diagnostics");
    }

    private static async Task<JsonElement> LintOk(Env env, string id, string path, string body)
    {
        using var response = await env.Http.SendAsync(Lint(id, path, body));
        return await Diagnostics(response);
    }

    [Fact]
    public async Task Lint_ReturnsCamelCaseDiagnostics_ForBrokenLua()
    {
        using var env = await Env.Create();
        var d = await LintOk(env, env.AlphaId, "data.lua", "x = 1\nfunction f()\n  y = \"abc\n");
        Assert.Equal(JsonValueKind.Array, d.ValueKind);
        Assert.Equal(2, d.GetArrayLength());
        var codes = d.EnumerateArray().Select(e => e.GetProperty("code").GetString()).ToList();
        Assert.Contains("LUA001", codes);
        Assert.Contains("LUA005", codes);
        foreach (var e in d.EnumerateArray())
        {
            Assert.Equal("error", e.GetProperty("severity").GetString());
            Assert.True(e.GetProperty("line").GetInt32() >= 1);
            Assert.True(e.GetProperty("column").GetInt32() >= 1);
            Assert.True(e.GetProperty("message").GetString()!.Length > 0);
            Assert.False(e.TryGetProperty("Line", out _), "property names must be camelCase");
            Assert.False(e.TryGetProperty("Code", out _));
        }
        var first = d[0];
        Assert.Equal(2, first.GetProperty("line").GetInt32());
        Assert.Equal("LUA005", first.GetProperty("code").GetString());
        Assert.Equal(1, first.GetProperty("column").GetInt32());
    }

    [Fact]
    public async Task Lint_CleanCode_ReturnsEmptyArray()
    {
        using var env = await Env.Create();
        using var response = await env.Http.SendAsync(Lint(env.AlphaId, "data.lua", "data:extend({ { type = \"item\", name = \"x\" } })\n"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"diagnostics\":[]}", (await response.Content.ReadAsStringAsync()).Replace(" ", ""));
    }

    [Fact]
    public async Task Lint_EmptyBody_IsOk_AndEmpty()
    {
        using var env = await Env.Create();
        var d = await LintOk(env, env.AlphaId, "data.lua", "");
        Assert.Equal(0, d.GetArrayLength());
    }

    [Fact]
    public async Task Lint_FileNeedNotExist_AndSubfolderPaths_Work()
    {
        using var env = await Env.Create();
        var d = await LintOk(env, env.AlphaId, "prototypes/brand-new/thing.lua", "end");
        Assert.Equal(1, d.GetArrayLength());
        Assert.Equal("LUA004", d[0].GetProperty("code").GetString());
        Assert.False(File.Exists(env.Workspace.Combine("alpha", "prototypes", "brand-new", "thing.lua")));
        Assert.False(Directory.Exists(env.Workspace.Combine("alpha", "prototypes")), "linting must not create directories");
    }

    [Fact]
    public async Task Lint_UsesBodyNotDiskContents_AndNeverModifiesFiles()
    {
        using var env = await Env.Create();
        var file = env.Workspace.Combine("alpha", "data.lua");
        var before = File.ReadAllBytes(file);
        var mtime = File.GetLastWriteTimeUtc(file);
        var files = Directory.GetFileSystemEntries(env.Workspace.Path, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();

        var d = await LintOk(env, env.AlphaId, "data.lua", "function broken(");
        Assert.True(d.GetArrayLength() >= 1);

        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(file));
        Assert.Equal(files, Directory.GetFileSystemEntries(env.Workspace.Path, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList());
        Assert.Empty(Directory.GetFiles(env.Workspace.Path, "*.fms-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Lint_ReadOnlyZipMod_Works_AndWritesNothing()
    {
        using var env = await Env.Create();
        var zipFile = Directory.GetFiles(env.Installed.Path, "*.zip").Single();
        var before = File.ReadAllBytes(zipFile);
        var d = await LintOk(env, env.ZippedId, "control.lua", "if x then");
        Assert.Equal(1, d.GetArrayLength());
        Assert.Equal("LUA005", d[0].GetProperty("code").GetString());
        // lint for existing file in the zip too
        var d2 = await LintOk(env, env.ZippedId, "data.lua", "local x = 1");
        Assert.Equal(0, d2.GetArrayLength());
        Assert.Equal(before, File.ReadAllBytes(zipFile));
        Assert.Equal(1, Directory.GetFileSystemEntries(env.Installed.Path).Length);
    }

    [Fact]
    public async Task Lint_RequiresToken()
    {
        using var env = await Env.Create();
        using var noToken = await env.Anonymous.SendAsync(Lint(env.AlphaId, "data.lua", "x"));
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);

        var wrong = Lint(env.AlphaId, "data.lua", "x");
        wrong.Headers.TryAddWithoutValidation("X-FMS-Token", "not-the-token");
        using var wrongToken = await env.Anonymous.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);
    }

    [Fact]
    public async Task Lint_RejectsForeignOrigin_AcceptsOwnOrigin()
    {
        using var env = await Env.Create();
        using var foreign = await env.Http.SendAsync(Lint(env.AlphaId, "data.lua", "x", "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        using var own = await env.Http.SendAsync(Lint(env.AlphaId, "data.lua", "x", env.Ui.Url.TrimEnd('/')));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task Lint_GetMethod_IsNotAllowed()
    {
        using var env = await Env.Create();
        using var response = await env.Http.GetAsync($"/api/lint?id={env.AlphaId}&path=data.lua");
        Assert.True(response.StatusCode == HttpStatusCode.MethodNotAllowed || response.StatusCode == HttpStatusCode.NotFound,
            "GET must not lint, got " + response.StatusCode);
    }

    [Fact]
    public async Task Lint_UnknownMod_Is404()
    {
        using var env = await Env.Create();
        using var response = await env.Http.SendAsync(Lint("no-such-mod-id", "data.lua", "x"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var missingId = await env.Http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/lint?path=data.lua") { Content = new StringContent("x") });
        Assert.True(missingId.StatusCode == HttpStatusCode.NotFound || missingId.StatusCode == HttpStatusCode.BadRequest, "got " + missingId.StatusCode);
    }

    [Fact]
    public async Task Lint_BodyOverTwoMegabytes_Is413()
    {
        using var env = await Env.Create();
        using var tooBig = await env.Http.SendAsync(Lint(env.AlphaId, "data.lua", new string('a', 2 * 1024 * 1024 + 1)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);

        using var big = await env.Http.SendAsync(Lint(env.AlphaId, "data.lua", new string('a', 2 * 1024 * 1024)));
        Assert.Equal(HttpStatusCode.OK, big.StatusCode);
        await Diagnostics(big);
    }

    [Fact]
    public async Task Lint_BodyOverTwoMegabytes_Chunked_Is413()
    {
        using var env = await Env.Create();
        var request = Lint(env.AlphaId, "data.lua", new byte[0]);
        request.Content = new StreamContent(new RepeatStream(3 * 1024 * 1024));
        request.Headers.TransferEncodingChunked = true;
        using var response = await env.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class RepeatStream : Stream
    {
        private long _left;
        public RepeatStream(long length) => _left = length;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _left);
            for (var i = 0; i < n; i++) buffer[offset + i] = (byte)'a';
            _left -= n;
            return n;
        }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Lint_InvalidUtf8_Is400()
    {
        using var env = await Env.Create();
        foreach (var bad in new[]
        {
            new byte[] { 0x78, 0xFF, 0xFE, 0x79 },
            new byte[] { 0xC3 },                       // truncated sequence
            new byte[] { 0xC0, 0xAF },                 // overlong
            new byte[] { 0xED, 0xA0, 0x80 },           // encoded surrogate
        })
        {
            using var response = await env.Http.SendAsync(Lint(env.AlphaId, "data.lua", bad));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Lint_Utf8Bom_AndCrlf_AndUnicode_AreAccepted()
    {
        using var env = await Env.Create();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("-- \u043F\u0440\u0438\u0432\u0435\u0442\r\nx = 1\r\nfunction f()\r\n")).ToArray();
        using var response = await env.Http.SendAsync(Lint(env.AlphaId, "data.lua", bytes));
        var d = await Diagnostics(response);
        Assert.Equal(1, d.GetArrayLength());
        Assert.Equal(3, d[0].GetProperty("line").GetInt32());
        Assert.Equal(1, d[0].GetProperty("column").GetInt32());
    }

    [Theory]
    [InlineData("../info.json")]
    [InlineData("..\\info.json")]
    [InlineData("a/../../x.lua")]
    [InlineData("/etc/passwd.lua")]
    [InlineData("a\0b.lua")]
    public async Task Lint_PathTraversal_Is400(string path)
    {
        using var env = await Env.Create();
        using var response = await env.Http.SendAsync(Lint(env.AlphaId, path, "x"));
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.NotFound,
            $"path '{path}' should be rejected, got {response.StatusCode}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Lint_RawNulInQuery_Is400()
    {
        using var env = await Env.Create();
        using var response = await env.Http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/lint?id={env.AlphaId}&path=a%00b.lua") { Content = new StringContent("x") });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Lint_MissingOrEmptyPath_IsRejected()
    {
        using var env = await Env.Create();
        using var empty = await env.Http.SendAsync(Lint(env.AlphaId, "", "x"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var none = await env.Http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/lint?id={env.AlphaId}") { Content = new StringContent("x") });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Theory]
    [InlineData("info.json")]
    [InlineData("locale/en/locale.cfg")]
    [InlineData("changelog.txt")]
    [InlineData("README")]
    [InlineData("thumbnail.png")]
    [InlineData("data.lua.bak")]
    [InlineData("data.luax")]
    public async Task Lint_NonLuaExtension_ReturnsEmptyDiagnostics(string path)
    {
        using var env = await Env.Create();
        using var response = await env.Http.SendAsync(Lint(env.AlphaId, path, "function broken( \"unterminated"));
        var d = await Diagnostics(response);
        Assert.Equal(0, d.GetArrayLength());
    }

    [Fact]
    public async Task Lint_UppercaseLuaExtension_IsStillLinted_OrIgnored_ButOk()
    {
        using var env = await Env.Create();
        using var response = await env.Http.SendAsync(Lint(env.AlphaId, "DATA.LUA", "function f("));
        await Diagnostics(response); // either behaviour is acceptable, but it must be a 200 with the diagnostics shape
    }

    [Fact]
    public async Task Lint_PathCheckHappensBeforeExtensionShortcut()
    {
        using var env = await Env.Create();
        using var response = await env.Http.SendAsync(Lint(env.AlphaId, "../x.txt", "x"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Lint_ManyErrors_CappedAt50()
    {
        using var env = await Env.Create();
        var d = await LintOk(env, env.AlphaId, "control.lua", string.Concat(Enumerable.Repeat("end\n", 400)));
        Assert.Equal(50, d.GetArrayLength());
    }

    [Fact]
    public async Task Lint_ConcurrentRequests_AreIndependent()
    {
        using var env = await Env.Create();
        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            var body = i % 2 == 0 ? "function f(" : "x = " + i;
            var d = await LintOk(env, env.AlphaId, "data.lua", body);
            return (i, count: d.GetArrayLength());
        });
        foreach (var (i, count) in await Task.WhenAll(tasks))
            Assert.Equal(i % 2 == 0 ? 2 : 0, count);
    }

    [Fact]
    public async Task Lint_DoesNotRequireModToBeEditable_ButStillChecksTokenFirst()
    {
        using var env = await Env.Create();
        using var anon = await env.Anonymous.SendAsync(Lint(env.ZippedId, "control.lua", "x"));
        Assert.Equal(HttpStatusCode.Forbidden, anon.StatusCode);
    }

    [Fact]
    public async Task Lint_SymlinkInsideMod_DoesNotEscape()
    {
        using var env = await Env.Create(ws =>
        {
            try { File.CreateSymbolicLink(ws.Combine("alpha", "link.lua"), "/etc/passwd"); } catch { }
        });
        // Lint uses only the body; whatever happens, it must not leak or fail with a 500.
        using var response = await env.Http.SendAsync(Lint(env.AlphaId, "link.lua", "x = 1"));
        Assert.True((int)response.StatusCode < 500, "got " + response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("root:", text);
    }

    [Fact]
    public async Task ExistingEndpoints_StillWork_AfterLintAdded()
    {
        using var env = await Env.Create();
        using var read = await env.Http.GetAsync($"/api/mods/{env.AlphaId}/file?path=data.lua");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("data:extend({})\n", await read.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Highlighter_IsServed_AsJavaScript()
    {
        using var env = await Env.Create();
        using var response = await env.Anonymous.GetAsync("/lua-highlight.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        Assert.True(type.Contains("javascript"), "content type was " + type);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("tokenize", text);
        Assert.Contains("export", text);
    }
}
