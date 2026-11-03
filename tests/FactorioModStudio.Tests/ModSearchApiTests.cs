using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Black-box tests of GET /api/mods/{id}/search, written from the UI-refresh contract (section 3.1) only.</summary>
public class ModSearchApiTests
{
    private static string InfoFor(string name) =>
        $$"""{ "name": "{{name}}", "version": "1.0.0", "title": "T", "author": "a", "factorio_version": "2.0", "dependencies": ["base"] }""";

    private sealed record Resp(HttpStatusCode Status, string Raw, System.Net.Http.Headers.HttpResponseHeaders Headers, System.Net.Http.Headers.HttpContentHeaders ContentHeaders)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement.Clone();
        public string? Header(string name) => Headers.TryGetValues(name, out var v) ? string.Join(",", v) : ContentHeaders.TryGetValues(name, out var c) ? string.Join(",", c) : null;
        public List<FileHit> Files => Json.GetProperty("files").EnumerateArray().Select(f => new FileHit(
            f.GetProperty("path").GetString()!,
            f.GetProperty("matches").EnumerateArray().Select(m => new Hit(
                m.GetProperty("line").GetInt32(), m.GetProperty("column").GetInt32(), m.GetProperty("length").GetInt32(), m.GetProperty("text").GetString()!)).ToList())).ToList();
        public int Total => Json.GetProperty("totalMatches").GetInt32();
        public bool Truncated => Json.GetProperty("truncated").GetBoolean();
        public int Skipped => Json.GetProperty("skippedFiles").GetInt32();
        public List<string> Paths => Files.Select(f => f.Path).ToList();
        public int MatchCount => Files.Sum(f => f.Matches.Count);
    }

    private sealed record Hit(int Line, int Column, int Length, string Text);
    private sealed record FileHit(string Path, List<Hit> Matches);

    private sealed class Env : IDisposable
    {
        public TempDir Project = new();
        public TempDir Workspace = new();
        public TempDir Installed = new();
        public RunningUi Ui = null!;
        public HttpClient Http = null!;
        public string Id = "";

        public static async Task<Env> Create(Action<Env>? seed = null)
        {
            var env = new Env();
            env.Workspace.Write("alpha/info.json", InfoFor("alpha"));
            seed?.Invoke(env);
            env.Ui = await UiServer.StartAsync(
                new ProjectContext(env.Project.Path, new ProjectConfig(), configFileExists: false),
                new UiOptions(Port: 0, ExtraModFolders: new[] { env.Installed.Path }, WorkspaceDirectory: env.Workspace.Path));
            env.Http = Client(env.Ui);
            env.Http.Timeout = TimeSpan.FromSeconds(60);
            env.Id = await ModId(env.Http, "alpha");
            return env;
        }

        public string A(string rel) => Workspace.Combine(new[] { "alpha" }.Concat(rel.Split('/')).ToArray());

        public void WriteA(string rel, string content, Encoding? enc = null)
        {
            var full = A(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, enc ?? new UTF8Encoding(false));
        }

        public void WriteBytesA(string rel, byte[] bytes)
        {
            var full = A(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        public Task<Resp> Search(string q, bool? regex = null, bool? cs = null, string? id = null, HttpClient? http = null, Action<HttpRequestMessage>? tweak = null)
        {
            var url = $"/api/mods/{id ?? Id}/search?q={Uri.EscapeDataString(q)}";
            if (regex != null) url += "&regex=" + (regex.Value ? "1" : "0");
            if (cs != null) url += "&case=" + (cs.Value ? "1" : "0");
            return ModSearchApiTests.Send(http ?? Http, url, tweak);
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

    private static async Task<Resp> Send(HttpClient http, string url, Action<HttpRequestMessage>? tweak = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        tweak?.Invoke(req);
        using var res = await http.SendAsync(req);
        return new Resp(res.StatusCode, await res.Content.ReadAsStringAsync(), res.Headers, res.Content.Headers);
    }

    private static Task<Env> Standard() => Env.Create();

    private static Resp Ok(Resp r)
    {
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r;
    }

    // ------------------------------------------------------------------ shape

    [Fact]
    public async Task Shape_SingleMatch_HasAllDocumentedFields()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "local x = 1\nlocal needle = 2\n"));
        var r = Ok(await env.Search("needle"));
        var j = r.Json;
        Assert.Equal("needle", j.GetProperty("query").GetString());
        Assert.Equal(1, r.Total);
        Assert.False(r.Truncated);
        Assert.Equal(0, r.Skipped);
        var f = Assert.Single(r.Files);
        Assert.Equal("data.lua", f.Path);
        var m = Assert.Single(f.Matches);
        Assert.Equal(2, m.Line);
        Assert.Equal(7, m.Column);
        Assert.Equal(6, m.Length);
        Assert.Equal("local needle = 2", m.Text);
        Assert.Contains("application/json", r.Header("Content-Type"));
    }

    [Fact]
    public async Task NoMatches_ReturnsEmptyFiles_NotNull()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "abc\n"));
        var r = Ok(await env.Search("zzz"));
        Assert.Equal(JsonValueKind.Array, r.Json.GetProperty("files").ValueKind);
        Assert.Empty(r.Files);
        Assert.Equal(0, r.Total);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Response_HasNoStore_AndNoTokenIsRequired()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "needle\n"));
        var r = Ok(await env.Search("needle"));
        Assert.Contains("no-store", r.Header("Cache-Control"));
        using var anon = Client(env.Ui, token: false);
        var r2 = Ok(await env.Search("needle", http: anon));
        Assert.Equal(1, r2.Total);
    }

    [Fact]
    public async Task ForeignHost_IsRejected()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "needle\n"));
        var r = await env.Search("needle", tweak: q => q.Headers.Host = "attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
    }

    [Fact]
    public async Task UnknownMod_Is404()
    {
        using var env = await Standard();
        var r = await env.Search("x", id: "does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        var r2 = await env.Search("x", id: "00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, r2.Status);
    }

    // ------------------------------------------------------------------ flags

    [Fact]
    public async Task CaseInsensitive_ByDefault_AndExplicitZero()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "Foo foo FOO\n"));
        Assert.Equal(3, Ok(await env.Search("foo")).Total);
        Assert.Equal(3, Ok(await env.Search("foo", cs: false)).Total);
        Assert.Equal(3, Ok(await env.Search("FoO", cs: false)).Total);
    }

    [Fact]
    public async Task CaseSensitive_WhenCaseIs1()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "Foo foo FOO\n"));
        var r = Ok(await env.Search("foo", cs: true));
        Assert.Equal(1, r.Total);
        Assert.Equal(5, r.Files[0].Matches[0].Column);
        Assert.Equal(1, Ok(await env.Search("FOO", cs: true)).Total);
        Assert.Equal(0, Ok(await env.Search("fOO", cs: true)).Total);
    }

    [Fact]
    public async Task Plain_TreatsRegexMetacharactersLiterally()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "a.b(c)[d] axb\nf(x)+1 $^ \\d\n"));
        Assert.Equal(1, Ok(await env.Search("a.b(c)[d]")).Total);
        Assert.Equal(1, Ok(await env.Search("a.b")).Total);       // not "axb"
        Assert.Equal(1, Ok(await env.Search("f(x)+1")).Total);
        Assert.Equal(1, Ok(await env.Search("$^")).Total);
        Assert.Equal(1, Ok(await env.Search("\\d")).Total);
        Assert.True(Ok(await env.Search("(", regex: false)).Total >= 2);
    }

    [Fact]
    public async Task Regex_Basic_AndCaseFlags()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "foo fooo FOO f0o\nbar\n"));
        var r = Ok(await env.Search("fo+", regex: true));
        Assert.Equal(3, r.Total);                                // foo, fooo, FOO (case-insensitive default)
        Assert.Equal(new[] { 1, 5, 10 }, r.Files[0].Matches.Select(m => m.Column).ToArray());
        Assert.Equal(new[] { 3, 4, 3 }, r.Files[0].Matches.Select(m => m.Length).ToArray());
        Assert.Equal(2, Ok(await env.Search("fo+", regex: true, cs: true)).Total);
        Assert.Equal(1, Ok(await env.Search("f[0-9]o", regex: true)).Total);
        Assert.Equal(1, Ok(await env.Search("\\bbar\\b", regex: true)).Total);
    }

    [Fact]
    public async Task Regex_Anchors_AreAppliedPerLine()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "local a\nx\nlocal b\nend\n"));
        var r = Ok(await env.Search("^local", regex: true));
        Assert.Equal(new[] { 1, 3 }, r.Files[0].Matches.Select(m => m.Line).ToArray());
        var r2 = Ok(await env.Search("^end$", regex: true));
        Assert.Equal(1, r2.Total);
        Assert.Equal(4, r2.Files[0].Matches[0].Line);
    }

    [Fact]
    public async Task Regex_InvalidPattern_Is400_WithError()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "a(b\n"));
        foreach (var bad in new[] { "(", "[a-", "*abc", "a{2,1}", "(?<n>" })
        {
            var r = await env.Search(bad, regex: true);
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.False(string.IsNullOrWhiteSpace(r.Json.GetProperty("error").GetString()));
        }
        // the same text is a perfectly fine literal query
        Assert.Equal(1, Ok(await env.Search("a(b", regex: false)).Total);
    }

    [Fact]
    public async Task Regex_ZeroLengthPattern_DoesNotHang_AndIsCapped()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", new string('a', 200_000) + "\n"));
        var sw = Stopwatch.StartNew();
        var r = await env.Search("x*", regex: true);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "took " + sw.Elapsed);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(r.MatchCount <= 500);
    }

    // ------------------------------------------------------------------ positions

    [Fact]
    public async Task MultipleMatchesOnOneLine_AreEachReported_InColumnOrder()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "ab ab ab\nxab\n"));
        var r = Ok(await env.Search("ab"));
        Assert.Equal(4, r.Total);
        var ms = r.Files[0].Matches;
        Assert.Equal(new[] { (1, 1), (1, 4), (1, 7), (2, 2) }, ms.Select(m => (m.Line, m.Column)).ToArray());
    }

    [Fact]
    public async Task OverlappingPlainMatches_AreNotDoubleCounted_Consecutively()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "aaaa\n"));
        var r = Ok(await env.Search("aa"));
        // either non-overlapping (2) or overlapping (3) is defensible; never more than 3 and at least 2
        Assert.True(r.Total == 2 || r.Total == 3, "total=" + r.Total);
        Assert.Equal(1, r.Files[0].Matches[0].Column);
        Assert.True(r.Files[0].Matches[1].Column is 2 or 3);
    }

    [Fact]
    public async Task Columns_AreUtf16CodeUnits_NonAscii_SurrogatePairs_Tabs()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/u.lua", "h\u00e9llo w\u00f6rld foo\n");                    // BMP non-ASCII: 1 unit each
            e.Workspace.Write("alpha/s.lua", "\U0001F600 \U0001F600foo \u4e2d\u6587foo\n");   // emoji = 2 units
            e.Workspace.Write("alpha/t.lua", "\t\tfoo\tfoo\n");
        });
        var u = Ok(await env.Search("foo")).Files.Single(f => f.Path == "u.lua").Matches.Single();
        Assert.Equal(13, u.Column);
        Assert.Equal(3, u.Length);

        var s = Ok(await env.Search("foo")).Files.Single(f => f.Path == "s.lua").Matches;
        // emoji(2)+space(1)+emoji(2) => foo at index 5 => column 6; "foo " => CJK at idx 9..10 (column 10), foo at idx 11 => column 12
        Assert.Equal(new[] { 6, 12 }, s.Select(m => m.Column).ToArray());

        var emoji = Ok(await env.Search("\U0001F600")).Files.Single(f => f.Path == "s.lua").Matches;
        Assert.Equal(new[] { 1, 4 }, emoji.Select(m => m.Column).ToArray());
        Assert.Equal(new[] { 2, 2 }, emoji.Select(m => m.Length).ToArray());

        var cjk = Ok(await env.Search("\u4e2d\u6587")).Files.Single().Matches.Single();
        Assert.Equal(10, cjk.Column);
        Assert.Equal(2, cjk.Length);

        var t = Ok(await env.Search("foo")).Files.Single(f => f.Path == "t.lua").Matches;
        Assert.Equal(new[] { 3, 7 }, t.Select(m => m.Column).ToArray());      // a tab is one unit
        Assert.Equal("\t\tfoo\tfoo", t[0].Text);                              // tabs kept in text
    }

    [Fact]
    public async Task NonAsciiQuery_CaseInsensitive()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "\u00dcber \u00fcber \u0414\u043e\u043c \u0434\u041e\u041c\n"));
        Assert.Equal(2, Ok(await env.Search("\u00fcber")).Total);
        Assert.Equal(2, Ok(await env.Search("\u0434\u043e\u043c")).Total);
        Assert.Equal(1, Ok(await env.Search("\u00fcber", cs: true)).Total);
    }

    [Fact]
    public async Task QueryWithReservedUrlCharacters_RoundTrips()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "a&b=c#d%20 e+f ?x\n"));
        foreach (var q in new[] { "a&b=c", "c#d", "%20", "e+f", "?x" })
            Assert.Equal(1, Ok(await env.Search(q)).Total);
        Assert.Equal("e+f", Ok(await env.Search("e+f")).Json.GetProperty("query").GetString());
    }

    [Fact]
    public async Task LineText_IsTrimmedTo300_ButPositionsRefToFullLine()
    {
        var line = new string('x', 449) + "needle" + new string('y', 100);
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "short needle\n" + line + "\n"));
        var r = Ok(await env.Search("needle"));
        var ms = r.Files[0].Matches;
        Assert.Equal(2, ms.Count);
        Assert.Equal("short needle", ms[0].Text);
        Assert.True(ms[1].Text.Length <= 300, "len " + ms[1].Text.Length);
        Assert.True(ms[1].Text.Length > 0);
        Assert.Equal(450, ms[1].Column);
        Assert.Equal(6, ms[1].Length);
    }

    [Fact]
    public async Task LineOf300Chars_IsNotShortened_AndPrefixOfLongLineIsKept()
    {
        var exact = new string('a', 294) + "needle";
        var longer = "needle" + new string('b', 400);
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/a.lua", exact + "\n");
            e.Workspace.Write("alpha/b.lua", longer + "\n");
        });
        var r = Ok(await env.Search("needle"));
        Assert.Equal(exact, r.Files.Single(f => f.Path == "a.lua").Matches[0].Text);
        var b = r.Files.Single(f => f.Path == "b.lua").Matches[0];
        Assert.True(b.Text.Length <= 300);
        Assert.StartsWith("needle", b.Text);   // match at column 1 must remain visible
    }

    [Fact]
    public async Task VeryLongSingleLineFile_Works()
    {
        var text = new string('z', 1_500_000) + "needle";
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", text));
        var sw = Stopwatch.StartNew();
        var r = Ok(await env.Search("needle"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "took " + sw.Elapsed);
        var m = Assert.Single(r.Files.Single().Matches);
        Assert.Equal(1, m.Line);
        Assert.Equal(1_500_001, m.Column);
        Assert.True(m.Text.Length <= 300);
        Assert.True(r.Json.GetRawText().Length < 20_000, "response must not echo the whole line");
    }

    [Fact]
    public async Task Crlf_LinesAreCountedOnce_AndTextHasNoCarriageReturn()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "one\r\ntwo needle\r\n\r\nfour needle"));
        var r = Ok(await env.Search("needle"));
        var ms = r.Files[0].Matches;
        Assert.Equal(new[] { 2, 4 }, ms.Select(m => m.Line).ToArray());
        Assert.Equal("two needle", ms[0].Text);
        Assert.Equal("four needle", ms[1].Text);   // last line without terminator
        var rx = Ok(await env.Search("needle$", regex: true));
        Assert.Equal(2, rx.Total);                  // `$` must hold before the CR of a CRLF
    }

    [Fact]
    public async Task LfOnly_BlankLinesCounted()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "\n\n\nneedle\n"));
        Assert.Equal(4, Ok(await env.Search("needle")).Files[0].Matches[0].Line);
    }

    [Fact]
    public async Task Utf8Bom_IsValid_AndNotPartOfTheFirstLine()
    {
        using var env = await Env.Create(e => e.WriteBytesA("data.lua", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("needle first\nsecond\n")).ToArray()));
        var r = Ok(await env.Search("needle"));
        Assert.Equal(0, r.Skipped);
        var m = Assert.Single(r.Files.Single().Matches);
        Assert.Equal(1, m.Line);
        Assert.Equal(1, m.Column);
        Assert.Equal("needle first", m.Text);
    }

    [Fact]
    public async Task EmptyFile_IsHarmless()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/empty.lua", "");
            e.Workspace.Write("alpha/data.lua", "needle\n");
        });
        var r = Ok(await env.Search("needle"));
        Assert.Equal(new[] { "data.lua" }, r.Paths.ToArray());
        Assert.Equal(0, r.Skipped);
    }

    [Fact]
    public async Task LastLineWithoutNewline_AndLineCountingWithMatchOnFirstAndLast()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "needle\nmid\nneedle"));
        var ms = Ok(await env.Search("needle")).Files[0].Matches;
        Assert.Equal(new[] { 1, 3 }, ms.Select(m => m.Line).ToArray());
    }

    // ------------------------------------------------------------------ files & ordering

    [Fact]
    public async Task Files_AreOrderedOrdinally_WithForwardSlashes_AndFilesWithoutMatchesOmitted()
    {
        using var env = await Env.Create(e =>
        {
            foreach (var p in new[] { "z.lua", "a.lua", "B.lua", "sub/x.lua", "sub-a.lua", "sub/deep/er/y.lua", "Sub/q.lua", "locale/en/main.cfg" })
                e.Workspace.Write("alpha/" + p, "needle\n");
            e.Workspace.Write("alpha/nomatch.lua", "nothing\n");
        });
        var r = Ok(await env.Search("needle"));
        var expected = new[] { "B.lua", "Sub/q.lua", "a.lua", "locale/en/main.cfg", "sub-a.lua", "sub/deep/er/y.lua", "sub/x.lua", "z.lua" };
        Assert.Equal(expected, r.Paths.ToArray());
        Assert.DoesNotContain("\\", r.Raw.Replace("\\n", "").Replace("\\t", "").Replace("\\u", "").Replace("\\\"", ""));
    }

    [Fact]
    public async Task Matches_AreOrderedByLineThenColumn_AcrossRegexAlternations()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "beta alpha\nalpha beta\n"));
        var ms = Ok(await env.Search("alpha|beta", regex: true)).Files[0].Matches;
        Assert.Equal(new[] { (1, 1), (1, 6), (2, 1), (2, 7) }, ms.Select(m => (m.Line, m.Column)).ToArray());
    }

    [Fact]
    public async Task ExtensionAllowlist_TextTypesIncluded_BinaryTypesExcluded()
    {
        using var env = await Env.Create(e =>
        {
            foreach (var p in new[] { "a.lua", "b.cfg", "c.json", "d.txt", "e.md" }) e.Workspace.Write("alpha/" + p, "needle\n");
            foreach (var p in new[] { "i.png", "j.dll", "k.exe", "l.zip", "m.ogg", "n.bin", "noext" }) e.Workspace.Write("alpha/" + p, "needle\n");
        });
        var r = Ok(await env.Search("needle"));
        Assert.Equal(new[] { "a.lua", "b.cfg", "c.json", "d.txt", "e.md" }, r.Paths.ToArray());
    }

    [Fact]
    public async Task UppercaseExtension_IsTreatedLikeLowercase()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/UP.LUA", "needle\n"));
        Assert.Equal(new[] { "UP.LUA" }, Ok(await env.Search("needle")).Paths.ToArray());
    }

    [Fact]
    public async Task BigFiles_Over2MB_AreSkipped_SmallerOnesSearched()
    {
        var big = new StringBuilder();
        while (big.Length < 2_200_000) big.Append("needle needle needle\n");
        var okSize = new StringBuilder();
        while (okSize.Length < 1_900_000) okSize.Append("filler line\n");
        okSize.Append("needle\n");
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/big.lua", big.ToString());
            e.Workspace.Write("alpha/ok.lua", okSize.ToString());
        });
        var r = Ok(await env.Search("needle"));
        Assert.Equal(new[] { "ok.lua" }, r.Paths.ToArray());
        Assert.Equal(1, r.Total);
    }

    [Fact]
    public async Task InvalidUtf8_AndNulFiles_AreSkipped_AndCounted()
    {
        using var env = await Env.Create(e =>
        {
            e.WriteBytesA("bad.lua", Encoding.ASCII.GetBytes("needle\n").Concat(new byte[] { 0xFF, 0xFE, 0xC3 }).ToArray());
            e.WriteBytesA("nul.txt", Encoding.ASCII.GetBytes("needle").Concat(new byte[] { 0 }).Concat(Encoding.ASCII.GetBytes("tail")).ToArray());
            e.WriteBytesA("trunc.md", Encoding.ASCII.GetBytes("needle ").Concat(new byte[] { 0xE2, 0x82 }).ToArray()); // truncated 3-byte sequence
            e.WriteBytesA("utf16.lua", Encoding.Unicode.GetBytes("needle"));                                        // contains NULs
            e.Workspace.Write("alpha/good.lua", "needle\n");
        });
        var r = Ok(await env.Search("needle"));
        Assert.Equal(new[] { "good.lua" }, r.Paths.ToArray());
        Assert.Equal(4, r.Skipped);
        Assert.Equal(1, r.Total);
    }

    [Fact]
    public async Task NulBeyondFirstChunk_IsStillDetected()
    {
        var bytes = Encoding.ASCII.GetBytes("needle\n" + new string('a', 200_000)).Concat(new byte[] { 0 }).ToArray();
        using var env = await Env.Create(e => e.WriteBytesA("late.lua", bytes));
        var r = Ok(await env.Search("needle"));
        Assert.Empty(r.Files);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public async Task HiddenDirectories_AreSkipped()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/.git/config.txt", "needle\n");
            e.Workspace.Write("alpha/.fmstudio/state.json", "needle\n");
            e.Workspace.Write("alpha/sub/.hidden/y.lua", "needle\n");
            e.Workspace.Write("alpha/sub/.cache/deeper/z.lua", "needle\n");
            e.Workspace.Write("alpha/sub/visible.lua", "needle\n");
        });
        var r = Ok(await env.Search("needle"));
        Assert.Equal(new[] { "sub/visible.lua" }, r.Paths.ToArray());
    }

    [Fact]
    public async Task HiddenFile_AtAnyDepth_IsSkipped()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/.hidden.lua", "needle\n");
            e.Workspace.Write("alpha/sub/.secret.txt", "needle\n");
            e.Workspace.Write("alpha/shown.lua", "needle\n");
        });
        Assert.Equal(new[] { "shown.lua" }, Ok(await env.Search("needle")).Paths.ToArray());
    }

    [Fact]
    public async Task Symlinks_AreNotFollowed_Directory_File_AndLoops()
    {
        using var outside = new TempDir();
        outside.Write("secret.lua", "needle outside\n");
        outside.Write("deep/s2.txt", "needle outside deep\n");
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/real.lua", "needle real\n");
            e.Workspace.Write("alpha/realdir/inner.lua", "needle inner\n");
            try
            {
                Directory.CreateSymbolicLink(e.A("linkdir"), outside.Path);
                File.CreateSymbolicLink(e.A("linkfile.lua"), outside.Combine("secret.lua"));
                Directory.CreateSymbolicLink(e.A("realdir/loop"), e.A(""));        // link to the mod root itself
                Directory.CreateSymbolicLink(e.A("linkinternal"), e.A("realdir")); // link inside the mod
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        });
        var r = Ok(await env.Search("needle"));
        Assert.Equal(new[] { "real.lua", "realdir/inner.lua" }, r.Paths.ToArray());
        Assert.DoesNotContain("outside", r.Raw);
    }

    [Fact]
    public async Task InfoJson_AndRootLevelFiles_AreSearchable()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/info.json", InfoFor("alpha").Replace("\"T\"", "\"Needle Title\"")));
        var r = Ok(await env.Search("needle title"));
        Assert.Equal(new[] { "info.json" }, r.Paths.ToArray());
    }

    [Fact]
    public async Task Search_ReflectsFileChangesImmediately()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "needle\n"));
        Assert.Equal(1, Ok(await env.Search("needle")).Total);
        env.WriteA("data.lua", "changed\n");
        Assert.Equal(0, Ok(await env.Search("needle")).Total);
        Assert.Equal(1, Ok(await env.Search("changed")).Total);
        env.WriteA("new/file.lua", "needle changed\n");
        Assert.Equal(2, Ok(await env.Search("changed")).Total);
    }

    [Fact]
    public async Task Search_DoesNotModifyTheMod()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "needle\n"));
        string Snapshot() => string.Join("|", Directory.GetFileSystemEntries(env.A(""), "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).Select(x => x + ":" + (File.Exists(x) ? new FileInfo(x).Length : -1)));
        var before = Snapshot();
        Ok(await env.Search("needle"));
        Assert.Equal(before, Snapshot());
    }

    // ------------------------------------------------------------------ read-only / zip

    private static string MakeZipMod(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{name}_1.0.0.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string content) { using var w = new StreamWriter(zip.CreateEntry($"{name}_1.0.0/{entry}").Open(), new UTF8Encoding(false)); w.Write(content); }
        Add("info.json", InfoFor(name));
        Add("data.lua", "-- zipped needle\nlocal x = 1\n");
        Add("sub/more.lua", "x\nzipped NEEDLE again\n");
        Add("pic.png", "needle");
        return path;
    }

    [Fact]
    public async Task ReadOnly_FolderMod_InExtraFolder_IsSearchable()
    {
        using var env = await Env.Create(e =>
        {
            e.Installed.Write("ro-mod_1.0.0/info.json", InfoFor("ro-mod"));
            e.Installed.Write("ro-mod_1.0.0/data.lua", "local needle = 1\n");
            e.Installed.Write("ro-mod_1.0.0/.git/x.txt", "needle\n");
        });
        var ro = await ModId(env.Http, "ro-mod");
        var r = Ok(await env.Search("needle", id: ro));
        Assert.Equal(new[] { "data.lua" }, r.Paths.ToArray());
    }

    [Fact]
    public async Task ZippedMod_IsSearchable_WithPathsRelativeToModRoot()
    {
        using var env = await Env.Create(e => MakeZipMod(e.Installed.Path, "zipmod"));
        var z = await ModId(env.Http, "zipmod");
        var r = Ok(await env.Search("needle", id: z));
        Assert.Equal(new[] { "data.lua", "sub/more.lua" }, r.Paths.ToArray());
        Assert.Equal(2, r.Total);
        Assert.Equal(1, r.Files[0].Matches[0].Line);
        Assert.Equal(2, r.Files[1].Matches[0].Line);
        Assert.Equal(8, r.Files[1].Matches[0].Column);
    }

    // ------------------------------------------------------------------ validation of q

    [Fact]
    public async Task Query_Empty_Missing_AreBadRequest_WithErrorField()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "needle\n"));
        var empty = await env.Search("");
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.False(string.IsNullOrWhiteSpace(empty.Json.GetProperty("error").GetString()));
        var missing = await ModSearchApiTests.Send(env.Http, $"/api/mods/{env.Id}/search");
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        var emptyRx = await env.Search("", regex: true);
        Assert.Equal(HttpStatusCode.BadRequest, emptyRx.Status);
    }

    [Fact]
    public async Task Query_Length_200Ok_201BadRequest()
    {
        var q200 = new string('n', 200);
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", q200 + "\n"));
        var ok = Ok(await env.Search(q200));
        Assert.Equal(1, ok.Total);
        var bad = await env.Search(q200 + "n");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var badRx = await env.Search(new string('n', 5000), regex: true);
        Assert.Equal(HttpStatusCode.BadRequest, badRx.Status);
    }

    [Fact]
    public async Task Query_SingleCharacter_IsAccepted()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", "xax\n"));
        Assert.Equal(2, Ok(await env.Search("x")).Total);
    }

    // ------------------------------------------------------------------ limits

    [Fact]
    public async Task MatchLimit_500_Truncates()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 700; i++) sb.Append("needle\n");
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", sb.ToString()));
        var r = Ok(await env.Search("needle"));
        Assert.True(r.Truncated);
        Assert.Equal(500, r.MatchCount);
        Assert.Equal(r.MatchCount, r.Total);
        Assert.Equal(new[] { 1, 2, 3 }, r.Files[0].Matches.Take(3).Select(m => m.Line).ToArray());
    }

    [Fact]
    public async Task MatchLimit_Counts_MatchesNotLines()
    {
        var line = string.Join(" ", Enumerable.Repeat("needle", 100));
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", string.Join("\n", Enumerable.Repeat(line, 10)) + "\n"));
        var r = Ok(await env.Search("needle"));
        Assert.True(r.Truncated);
        Assert.Equal(500, r.MatchCount);
    }

    [Fact]
    public async Task MatchLimit_Across_Files_Stops_At_500()
    {
        using var env = await Env.Create(e =>
        {
            var content = string.Concat(Enumerable.Repeat("needle\n", 40));
            for (var i = 0; i < 20; i++) e.Workspace.Write($"alpha/f{i:D2}.lua", content);   // 800 matches over 20 files
        });
        var r = Ok(await env.Search("needle"));
        Assert.True(r.Truncated);
        Assert.Equal(500, r.MatchCount);
        Assert.Equal("f00.lua", r.Files[0].Path);
    }

    [Fact]
    public async Task FileLimit_100_FilesWithMatches_Truncates()
    {
        using var env = await Env.Create(e =>
        {
            for (var i = 0; i < 150; i++) e.Workspace.Write($"alpha/m/f{i:D3}.lua", "needle\n");
        });
        var r = Ok(await env.Search("needle"));
        Assert.True(r.Truncated);
        Assert.Equal(100, r.Files.Count);
        Assert.Equal("m/f000.lua", r.Files[0].Path);
        Assert.Equal("m/f099.lua", r.Files[99].Path);
    }

    [Fact]
    public async Task ExactlyUnderLimits_IsNotTruncated()
    {
        using var env = await Env.Create(e =>
        {
            for (var i = 0; i < 60; i++) e.Workspace.Write($"alpha/m/f{i:D3}.lua", "needle\nneedle\n");
        });
        var r = Ok(await env.Search("needle"));
        Assert.False(r.Truncated);
        Assert.Equal(60, r.Files.Count);
        Assert.Equal(120, r.Total);
    }

    [Fact]
    public async Task ManyFiles_Over2000_AreHandled_WithinBudget()
    {
        using var env = await Env.Create(e =>
        {
            for (var i = 0; i < 2300; i++) e.Workspace.Write($"alpha/many/f{i:D4}.lua", "nothing here\n");
        });
        var sw = Stopwatch.StartNew();
        var r = await env.Search("needle");
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "took " + sw.Elapsed);
        Assert.Equal(0, r.Total);
    }

    [Fact]
    public async Task CatastrophicRegex_ReturnsQuickly_AsTruncated()
    {
        using var env = await Env.Create(e => e.Workspace.Write("alpha/data.lua", new string('a', 5000) + "b\nneedle ok\n"));
        var sw = Stopwatch.StartNew();
        var r = await env.Search("(a+)+$", regex: true);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "took " + sw.Elapsed);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(r.Truncated, "timeout must be reported as truncated");
    }

    [Fact]
    public async Task CatastrophicRegex_AcrossManyFiles_StaysWithinOverallBudget()
    {
        using var env = await Env.Create(e =>
        {
            for (var i = 0; i < 30; i++) e.Workspace.Write($"alpha/c{i:D2}.lua", new string('a', 3000) + "b\n");
        });
        var sw = Stopwatch.StartNew();
        var r = await env.Search("(a+)+$", regex: true);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "took " + sw.Elapsed);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task CatastrophicRegex_DoesNotBlockOtherRequests()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/data.lua", new string('a', 4000) + "b\n");
        });
        var slow = env.Search("(a+)+$", regex: true);
        await Task.Delay(100);
        var sw = Stopwatch.StartNew();
        var quick = Ok(await ModSearchApiTests.Send(env.Http, "/api/mods"));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "mods list took " + sw.Elapsed);
        await slow;
    }

    [Fact]
    public async Task ConcurrentSearches_AreIndependent()
    {
        using var env = await Env.Create(e =>
        {
            e.Workspace.Write("alpha/a.lua", "wordA\nwordB\n");
        });
        var tasks = Enumerable.Range(0, 20).Select(i => env.Search(i % 2 == 0 ? "wordA" : "wordB")).ToArray();
        var all = await Task.WhenAll(tasks);
        for (var i = 0; i < all.Length; i++)
        {
            Assert.Equal(HttpStatusCode.OK, all[i].Status);
            Assert.Equal(1, all[i].Total);
            Assert.Equal(i % 2 == 0 ? 1 : 2, all[i].Files[0].Matches[0].Line);
        }
    }
}
