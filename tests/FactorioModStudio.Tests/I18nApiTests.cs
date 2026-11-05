using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

public class I18nApiTests
{
    private static readonly string[] Codes = { "en", "ru", "de", "zh-CN", "uk", "pl", "fr", "es" };

    private static async Task<RunningUi> Start(TempDir project, TempDir workspace) =>
        await UiServer.StartAsync(
            new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false),
            new UiOptions(Port: 0, WorkspaceDirectory: workspace.Path));

    private static string Repo()
    {
        var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("FMS_REPO_ROOT") is { Length: > 0 } root ? root : AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "examples", "hello-mod"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found");
    }

    private static string WwwRoot() => Path.Combine(Repo(), "src", "FactorioModStudio.Server", "wwwroot");

    // Sends the request line verbatim so that the client cannot normalise ../ or %2e%2e.
    private static async Task<(int Status, string Headers, byte[] Body)> Raw(RunningUi ui, string target)
    {
        var uri = new Uri(ui.Url);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port);
        var stream = tcp.GetStream();
        var req = $"GET {target} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(req));
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var all = ms.ToArray();
        var split = IndexOf(all, "\r\n\r\n"u8.ToArray());
        if (split < 0) return (0, Encoding.ASCII.GetString(all), Array.Empty<byte>());
        var head = Encoding.ASCII.GetString(all, 0, split);
        var status = int.Parse(head.Split(' ')[1]);
        var body = all.Skip(split + 4).ToArray();
        if (head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)) body = Dechunk(body);
        return (status, head, body);
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length && ok; j++) ok = hay[i + j] == needle[j];
            if (ok) return i;
        }
        return -1;
    }

    private static byte[] Dechunk(byte[] data)
    {
        using var output = new MemoryStream();
        var pos = 0;
        while (pos < data.Length)
        {
            var eol = IndexOf(data.Skip(pos).ToArray(), "\r\n"u8.ToArray());
            if (eol < 0) break;
            var size = Convert.ToInt32(Encoding.ASCII.GetString(data, pos, eol).Split(';')[0].Trim(), 16);
            pos += eol + 2;
            if (size == 0) break;
            output.Write(data, pos, size);
            pos += size + 2;
        }
        return output.ToArray();
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";

    [Fact]
    public async Task EachLanguageFile_IsServed_AsUtf8Json_WithSecurityHeaders_AndRepoBytes()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        await using var ui = await Start(project, ws);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        using var index = await http.GetAsync("/");
        var csp = Header(index, "Content-Security-Policy");
        Assert.True(csp.Length > 0);

        foreach (var code in Codes)
        {
            using var response = await http.GetAsync($"/i18n/{code}.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString().ToLowerInvariant());
            Assert.Contains("no-cache", Header(response, "Cache-Control"));
            Assert.Equal(csp, Header(response, "Content-Security-Policy"));
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var expected = await File.ReadAllBytesAsync(Path.Combine(WwwRoot(), "i18n", code + ".json"));
            Assert.True(bytes.SequenceEqual(expected), $"{code}.json bytes differ from repo file ({bytes.Length} vs {expected.Length})");

            var doc = JsonDocument.Parse(bytes);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.NotEmpty(doc.RootElement.EnumerateObject());
            Assert.All(doc.RootElement.EnumerateObject(), p =>
            {
                Assert.Equal(JsonValueKind.String, p.Value.ValueKind);
                Assert.DoesNotContain("�", p.Value.GetString());
            });
        }
    }

    [Fact]
    public async Task LanguageFiles_ServedFromEmbeddedResources_SurviveBuildOutputOnly()
    {
        // The served files must not depend on the source tree: the assembly itself carries them.
        var asm = typeof(UiServer).Assembly;
        var names = asm.GetManifestResourceNames();
        Assert.True(names.Any(n => n.Contains("i18n", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".js")), "i18n.js is not an embedded resource: " + string.Join(", ", names));
        foreach (var code in Codes)
            Assert.True(names.Any(n => n.EndsWith(code + ".json", StringComparison.OrdinalIgnoreCase)), $"{code}.json is not an embedded resource");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task I18nScript_IsServed_AsJavaScript_AndMatchesRepo()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        await using var ui = await Start(project, ws);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };

        using var response = await http.GetAsync("/i18n.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType!.MediaType!);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.True(Header(response, "Content-Security-Policy").Length > 0);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var expected = await File.ReadAllBytesAsync(Path.Combine(WwwRoot(), "i18n.js"));
        Assert.True(bytes.SequenceEqual(expected), "i18n.js differs from repo");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("export", text);
        Assert.Contains("createTranslator", text);
    }

    [Fact]
    public async Task EverythingElseUnderI18n_Is404()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        await using var ui = await Start(project, ws);

        var targets = new[]
        {
            "/i18n/xx.json", "/i18n/EN.json", "/i18n/En.json", "/i18n/zh-cn.json", "/i18n/ZH-CN.json", "/i18n/zh.json", "/i18n/en.json.bak",
            "/i18n/index.json", "/i18n/", "/i18n", "/i18n/en", "/i18n/en.json/", "/i18n/en.json/x", "/i18n/.json", "/i18n/en.JSON",
            "/i18n/../app.js", "/i18n/../i18n/en.json/../../app.js", "/i18n/%2e%2e/app.js", "/i18n/%2E%2E/app.js", "/i18n/..%2fapp.js", "/i18n/..%2Fapp.js",
            "/i18n/%2e%2e%2fapp.js", "/i18n/..\\app.js", "/i18n/..%5capp.js", "/i18n/en.json%00", "/i18n/en.json%00.bak", "/i18n/%65n.json.bak",
            "/i18n//en.json", "/i18n/./en.json/.", "/i18n/en.json::$DATA", "/i18n/en.json%20", "/i18n/%20en.json", "/i18n/en.json.",
            "/i18n/ru.json.gz", "/i18n/../../../../etc/passwd", "/i18n/%2e%2e/%2e%2e/%2e%2e/etc/passwd", "/i18n/....//app.js", "/i18n/en%2ejson.bak",
            "/i18n/../index.html", "/i18n/uk.json%2f..%2f..%2fapp.js", "/i18n/*.json", "/i18n/en.json;x", "/i18n/ru.json%0d%0aX: y",
        };
        // Kestrel itself collapses dot segments before routing, so these end up as /app.js etc. (public files); they
        // must merely never expose anything outside wwwroot.
        var normalised = new HashSet<string> { "/i18n/../app.js", "/i18n/../i18n/en.json/../../app.js", "/i18n/%2e%2e/app.js", "/i18n/%2E%2E/app.js", "/i18n/../index.html", "/i18n/../../../../etc/passwd", "/i18n/%2e%2e/%2e%2e/%2e%2e/etc/passwd" };
        var violations = new List<string>();
        foreach (var target in targets)
        {
            var (status, head, body) = await Raw(ui, target);
            var text = Encoding.UTF8.GetString(body);
            Assert.DoesNotContain("root:x:", text);
            Assert.DoesNotContain("[mod-name]", text);
            if (normalised.Contains(target)) continue;
            if (status == 200) violations.Add($"{target} -> 200");
        }
        Assert.True(violations.Count == 0, "non-allowlisted /i18n paths answered 200: " + string.Join("; ", violations));
        // 404 on the strict ones (clean, unambiguous paths)
        foreach (var target in new[] { "/i18n/xx.json", "/i18n/EN.json", "/i18n/index.json", "/i18n/en.json.bak", "/i18n/%2e%2e/app.js", "/i18n/", "/i18n/zh-cn.json" })
            Assert.Equal(404, (await Raw(ui, target)).Status);
    }

    [Fact]
    public async Task AllowedFiles_IgnoreQueryString_AndRejectOtherMethods()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        await using var ui = await Start(project, ws);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };

        using var q = await http.GetAsync("/i18n/de.json?v=123");
        Assert.Equal(HttpStatusCode.OK, q.StatusCode);
        var plain = await http.GetByteArrayAsync("/i18n/de.json");
        Assert.True((await q.Content.ReadAsByteArrayAsync()).SequenceEqual(plain));

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, "/i18n/en.json") { Content = new StringContent("{}") };
            using var response = await http.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
        // the dictionary must remain intact after the attempted writes
        var served = await http.GetByteArrayAsync("/i18n/en.json");
        var onDisk = await File.ReadAllBytesAsync(Path.Combine(WwwRoot(), "i18n", "en.json"));
        Assert.True(served.SequenceEqual(onDisk));

        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/i18n/en.json"));
        Assert.True(head.StatusCode is HttpStatusCode.OK or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task ServedLanguageFiles_DoNotRequireTheApiToken()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        await using var ui = await Start(project, ws);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        // static assets are public to the page like app.js; they must never contain the session token
        var all = new StringBuilder();
        foreach (var code in Codes) all.Append(await http.GetStringAsync($"/i18n/{code}.json"));
        all.Append(await http.GetStringAsync("/i18n.js"));
        Assert.DoesNotContain(ui.Token, all.ToString());
    }

    [Fact]
    public async Task Index_StaysFreeOfInlineScriptsAndStyles_AndCspIsStrict()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        await using var ui = await Start(project, ws);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };

        using var index = await http.GetAsync("/");
        var csp = Header(index, "Content-Security-Policy");
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("style-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("*", csp.Replace("'self'", ""));

        var html = await index.Content.ReadAsStringAsync();
        foreach (Match m in Regex.Matches(html, "<script\\b[^>]*>", RegexOptions.IgnoreCase))
            Assert.Contains("src=", m.Value);
        Assert.Empty(Regex.Matches(html, "<script\\b[^>]*>\\s*[^<\\s]", RegexOptions.IgnoreCase));
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Regex.Matches(html, "\\sstyle\\s*=", RegexOptions.IgnoreCase));
        Assert.Empty(Regex.Matches(html, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase));
        Assert.Empty(Regex.Matches(html, "https?://(?!www\\.w3\\.org)", RegexOptions.IgnoreCase));

        // The index is also served unchanged apart from i18n markup: still contains the brand.
        Assert.Contains("Factorio Mod Studio", html);
    }

    [Fact]
    public async Task Dictionaries_AreValidUtf8_WithoutBom_AndStaticFilesHaveNoSourceMaps()
    {
        foreach (var code in Codes)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(WwwRoot(), "i18n", code + ".json"));
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{code}.json has a BOM");
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            var text = strict.GetString(bytes);
            Assert.DoesNotContain("�", text);
        }
    }

    [Fact]
    public async Task ApiErrors_StayEnglish_RegardlessOfAcceptLanguage()
    {
        using var project = new TempDir();
        using var ws = new TempDir();
        await using var ui = await Start(project, ws);
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        http.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,de;q=0.8");

        using var missing = await http.GetAsync("/api/mods/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var text = await missing.Content.ReadAsStringAsync();
        Assert.DoesNotMatch("[\\u0400-\\u04FF\\u4E00-\\u9FFF]", text);

        using var templates = await http.GetAsync("/api/templates");
        if (templates.StatusCode == HttpStatusCode.OK)
            Assert.DoesNotMatch("[\\u0400-\\u04FF\\u4E00-\\u9FFF]", await templates.Content.ReadAsStringAsync());
    }
}
