using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>
/// Black-box tests of the folder browser behind "add a Factorio installation" (GET /api/fs/list) and of how it
/// feeds POST /api/factorio/installations, written from the contracts only (factorio-path-contract.md section 2,
/// versions-contract.md section 2; the single-path settings endpoints are gone). The thorough validation of the
/// installations API lives in FactorioInstallationsApiTests. A fake Factorio (a shell script named <c>factorio</c>)
/// lives in temp installations; tests that need executables are skipped on Windows. The FACTORIO_PATH environment
/// variable is deliberately not set by these tests (process-global, other test classes clear it).
/// </summary>
public class FactorioPathApiTests
{
    private static bool Unsupported => OperatingSystem.IsWindows() || !File.Exists("/bin/sh");

    private const string FakeScript = """
        #!/bin/sh
        D="$(dirname "$0")"
        echo "$$ $*" >> "$D/calls.txt"
        prev=""
        for a in "$@"; do
          if [ "$prev" = "--create" ]; then : > "$a"; fi
          prev="$a"
        done
        echo "   0.100 Info Something.cpp:1: ok"
        exit 0
        """;

    private sealed record Resp(HttpStatusCode Status, string Raw, System.Net.Http.Headers.HttpResponseHeaders Headers, System.Net.Http.Headers.HttpContentHeaders ContentHeaders)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement.Clone();
        public string? Str(string p) => Json.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public string? Header(string name) => Headers.TryGetValues(name, out var v) ? string.Join(",", v) : ContentHeaders.TryGetValues(name, out var c) ? string.Join(",", c) : null;
        public string? ConfiguredPath => Json.GetProperty("configuredPath").ValueKind == JsonValueKind.Null ? null : Json.GetProperty("configuredPath").GetString();
        public string Source => Json.GetProperty("source").GetString()!;
        public bool Found => Json.GetProperty("effective").GetProperty("found").GetBoolean();
        public string? EffPath => Json.GetProperty("effective").GetProperty("path").ValueKind == JsonValueKind.Null ? null : Json.GetProperty("effective").GetProperty("path").GetString();
        public string? EffError => Json.GetProperty("effective").GetProperty("error").ValueKind == JsonValueKind.Null ? null : Json.GetProperty("effective").GetProperty("error").GetString();
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('/', '\\') is { Length: 0 } e ? "/" : Path.GetFullPath(p).TrimEnd('/', '\\');

    private sealed class Env : IDisposable
    {
        public TempDir Project = new();
        public TempDir Workspace = new();
        public TempDir Tools = new();      // fake installations live here (outside the project)
        public RunningUi Ui = null!;
        public HttpClient Http = null!;
        public string SettingsDir => Project.Combine(".fmstudio");
        public string SettingsFile => Project.Combine(".fmstudio", "settings.json");

        /// <summary>Creates <c>Tools/name/bin/x64/factorio</c>; returns (installation dir, executable).</summary>
        public (string Install, string Exe) MakeInstall(string name)
        {
            var install = Tools.Combine(name);
            var bin = Path.Combine(install, "bin", "x64");
            Directory.CreateDirectory(bin);
            var exe = Path.Combine(bin, "factorio");
            File.WriteAllText(exe, FakeScript.Replace("\r\n", "\n"));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return (install, exe);
        }

        public string MakeScript(string dirName, string fileName, string content = "#!/bin/sh\necho hi\n")
        {
            var dir = Tools.Combine(dirName);
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, fileName);
            File.WriteAllText(p, content);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return p;
        }

        public static async Task<Env> Create(string? configuredFactorio = null, Action<Env>? seed = null)
        {
            Environment.SetEnvironmentVariable("FACTORIO_PATH", null);
            var env = new Env();
            AddMod(env.Workspace, "alpha");
            seed?.Invoke(env);
            var cfg = new ProjectConfig { FactorioPath = configuredFactorio };
            env.Ui = await UiServer.StartAsync(
                new ProjectContext(env.Project.Path, cfg, configFileExists: false),
                new UiOptions(Port: 0, ExtraModFolders: Array.Empty<string>(), WorkspaceDirectory: env.Workspace.Path));
            env.Http = Client(env.Ui);
            env.Http.Timeout = TimeSpan.FromSeconds(60);
            return env;
        }

        public static void AddMod(TempDir root, string name)
        {
            root.Write($"{name}/info.json", Fixtures.ValidInfo.Replace("my-mod", name));
            root.Write($"{name}/changelog.txt", new string('-', 99) + "\nVersion: 1.2.3\nDate: 2026-01-01\n  Features:\n    - Initial release.\n");
            root.Write($"{name}/data.lua", "data:extend({})\n");
            Fixtures.WritePngHeader(root.Combine(name, "thumbnail.png"), 144, 144);
        }

        public void Dispose()
        {
            try { Http?.Dispose(); } catch { }
            try { Ui?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            Project.Dispose(); Workspace.Dispose(); Tools.Dispose();
        }
    }

    private static HttpClient Client(RunningUi ui, bool token = true)
    {
        var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        if (token) http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        return http;
    }

    private static async Task<Resp> Send(HttpClient http, HttpMethod method, string url, string? body = null, Action<HttpRequestMessage>? tweak = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
        tweak?.Invoke(req);
        using var res = await http.SendAsync(req);
        return new Resp(res.StatusCode, await res.Content.ReadAsStringAsync(), res.Headers, res.Content.Headers);
    }

    private static Task<Resp> Add(Env e, string? path, Action<HttpRequestMessage>? tweak = null) =>
        Send(e.Http, HttpMethod.Post, "/api/factorio/installations", JsonSerializer.Serialize(new { path }), tweak);
    private static Task<Resp> ListInstalls(Env e) => Send(e.Http, HttpMethod.Get, "/api/factorio/installations");
    private static Task<Resp> Env_(Env e) => Send(e.Http, HttpMethod.Get, "/api/environment");
    private static Task<Resp> Fs(Env e, string? path, Action<HttpRequestMessage>? tweak = null, HttpClient? http = null) =>
        Send(http ?? e.Http, HttpMethod.Get, "/api/fs/list" + (path == null ? "" : "?path=" + Uri.EscapeDataString(path)), tweak: tweak);

    private static void NoFailures(List<string> failures) => Assert.True(failures.Count == 0, string.Join("\n  ", failures));

    // ------------------------------------------------------------------ fs/list: access control & validation

    [Fact]
    public async Task FsList_RequiresToken_AndLocalHost()
    {
        using var env = await Env.Create();
        using var anonymous = Client(env.Ui, token: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fs(env, env.Project.Path, http: anonymous)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fs(env, null, http: anonymous)).Status);
        using var wrong = new HttpClient { BaseAddress = new Uri(env.Ui.Url) };
        wrong.DefaultRequestHeaders.Add("X-FMS-Token", "nope");
        Assert.Equal(HttpStatusCode.Forbidden, (await Fs(env, env.Project.Path, http: wrong)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fs(env, env.Project.Path, r => r.Headers.Host = "attacker.example")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fs(env, null, r => r.Headers.Host = "attacker.example")).Status);
        var ok = await Fs(env, env.Project.Path);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
    }

    [Fact]
    public async Task FsList_InvalidPaths_Are400()
    {
        using var env = await Env.Create();
        var failures = new List<string>();
        var cases = new Dictionary<string, string>
        {
            ["relative"] = "relative/dir",
            ["dot"] = ".",
            ["dotdot"] = "..",
            ["dot slash"] = "./x",
            ["traversal"] = "../../etc",
            ["tilde"] = "~",
            ["NUL"] = env.Project.Path + "\0",
            ["NUL start"] = "\0" + env.Project.Path,
            ["control"] = env.Project.Path + "\u0001",
            ["newline"] = env.Project.Path + "\n",
            ["overlong"] = "/" + new string('a', 1100),
        };
        foreach (var (label, v) in cases)
        {
            var r = await Fs(env, v);
            if (r.Status != HttpStatusCode.BadRequest) { failures.Add($"{label}: expected 400, got {(int)r.Status} {r.Raw[..Math.Min(200, r.Raw.Length)]}"); continue; }
            if (string.IsNullOrWhiteSpace(r.Str("error"))) failures.Add($"{label}: no error message");
        }
        NoFailures(failures);
    }

    [Fact]
    public async Task FsList_MissingOrFile_Is404()
    {
        using var env = await Env.Create();
        var file = env.Project.Write("some.txt", "secret");
        var fact = env.Project.Write("factorio", "x");
        var failures = new List<string>();
        foreach (var p in new[] { env.Project.Combine("nope"), env.Project.Combine("nope", "deeper"), file, fact })
        {
            var r = await Fs(env, p);
            if (r.Status != HttpStatusCode.NotFound) failures.Add($"{p}: expected 404, got {(int)r.Status}");
            else if (string.IsNullOrWhiteSpace(r.Str("error"))) failures.Add("404 without error");
            else if (r.Raw.Contains("secret")) failures.Add("404 body leaks file content");
        }
        NoFailures(failures);
    }

    [Fact]
    public async Task FsList_PathLength_1024Ok_1025Is400()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        string Make(int total)
        {
            var sb = env.Tools.Path;
            var remaining = total - sb.Length;
            while (remaining > 0)
            {
                var comp = Math.Min(200, remaining - 1);
                if (remaining - 1 - comp is > 0 and < 2) comp -= 2;
                sb += "/" + new string('e', comp);
                remaining -= comp + 1;
            }
            Directory.CreateDirectory(sb);
            return sb;
        }
        var d1024 = Make(1024);
        Assert.Equal(1024, d1024.Length);
        Assert.Equal(HttpStatusCode.OK, (await Fs(env, d1024)).Status);
        var d1025 = Make(1025);
        Assert.Equal(1025, d1025.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await Fs(env, d1025)).Status);
    }

    // ------------------------------------------------------------------ fs/list: content rules

    private static List<(string Name, string Type, bool Factorio)> Entries(Resp r) =>
        r.Json.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("type").GetString()!, e.GetProperty("factorio").GetBoolean())).ToList();

    [Fact]
    public async Task FsList_Directory_RulesSortingAndShape()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var root = env.Tools.Combine("tree");
        foreach (var d in new[] { "Bdir", "adir", "Zeta", ".hidden", ".git", "тест dir", "with space", "_under", "10", "9" })
            Directory.CreateDirectory(Path.Combine(root, d));
        foreach (var f in new[] { "factorio", "factorio.exe", "readme.txt", "notfactorio", "factorio.txt", ".factorio", "evil.sh", "FACTORIO.bak", "data.bin" })
            File.WriteAllText(Path.Combine(root, f), "secret-content");
        File.WriteAllText(Path.Combine(root, ".hiddenfile"), "x");

        var r = await Fs(env, root);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Contains("no-store", r.Header("Cache-Control") ?? "");
        Assert.Contains("json", r.Header("Content-Type") ?? "");
        Assert.DoesNotContain("secret-content", r.Raw);

        var top = r.Json.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "entries", "parent", "path", "separator", "truncated" }, top);
        Assert.Equal(Norm(root), Norm(r.Str("path")!));
        Assert.Equal(Norm(env.Tools.Path), Norm(r.Str("parent")!));
        Assert.Equal("/", r.Str("separator"));
        Assert.False(r.Json.GetProperty("truncated").GetBoolean());

        foreach (var e in r.Json.GetProperty("entries").EnumerateArray())
            Assert.Equal(new[] { "factorio", "name", "type" }, e.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());

        var ents = Entries(r);
        var dirs = ents.Where(e => e.Type == "dir").Select(e => e.Name).ToList();
        var files = ents.Where(e => e.Type == "file").Select(e => e.Name).ToList();
        var expectedDirs = new[] { "10", "9", "_under", "adir", "Bdir", "with space", "Zeta", "тест dir" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(expectedDirs, dirs);
        Assert.Equal(new[] { "factorio", "factorio.exe" }, files.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.True(ents.Take(dirs.Count).All(e => e.Type == "dir"), "dirs must come first: " + string.Join(",", ents.Select(e => e.Name)));
        Assert.True(ents.Skip(dirs.Count).All(e => e.Type == "file"));
        Assert.True(ents.Where(e => e.Type == "file").All(e => e.Factorio), "files must be flagged factorio");
        Assert.True(ents.Where(e => e.Type == "dir").All(e => !e.Factorio), "dirs are never factorio executables");
        Assert.DoesNotContain(".hidden", dirs);
    }

    [Fact]
    public async Task FsList_DirectoryNamedFactorio_IsADir_NotAFile()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var root = env.Tools.Combine("t2");
        Directory.CreateDirectory(Path.Combine(root, "factorio"));
        var r = await Fs(env, root);
        var e = Entries(r).Single();
        Assert.Equal("dir", e.Type);
        Assert.False(e.Factorio);
    }

    [Fact]
    public async Task FsList_FilesOnly_NoOtherFilesEverListed()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var root = env.Tools.Combine("t3");
        Directory.CreateDirectory(root);
        foreach (var n in new[] { "a.txt", "b.exe", "c.sh", "factorio.bak", "notfactorio.exe", "my-factorio", "factorio ", " factorio" })
            File.WriteAllText(Path.Combine(root, n), "x");
        var r = await Fs(env, root);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Empty(r.Json.GetProperty("entries").EnumerateArray().ToList());
    }

    [Fact]
    public async Task FsList_PathNormalisation_TrailingSlashAndDotDot()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var root = env.Tools.Combine("t4");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        foreach (var variant in new[] { root + "/", root + "/sub/..", root + "/./sub/../" })
        {
            var r = await Fs(env, variant);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            var p = r.Str("path")!;
            Assert.False(p.Contains(".."), "path should be normalised: " + p);
            Assert.Equal(Norm(root), Norm(p));
            Assert.Equal(new[] { "sub" }, Entries(r).Select(e => e.Name).ToArray());
        }
    }

    [Fact]
    public async Task FsList_Root_HasNullParent_AndSeparator()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var r = await Fs(env, "/");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("/", r.Str("path"));
        Assert.True(r.Json.GetProperty("parent").ValueKind == JsonValueKind.Null || r.Str("parent") == "", "parent of / must be null (or \"\" = roots view); got: " + r.Raw[..Math.Min(200, r.Raw.Length)]);
        Assert.Equal("/", r.Str("separator"));
        Assert.DoesNotContain("/etc/passwd", r.Raw);
        Assert.True(Entries(r).All(e => e.Type == "dir" || e.Name is "factorio" or "factorio.exe"));
    }

    [Fact]
    public async Task FsList_RootsView_ForMissingAndEmptyPath()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        foreach (var r in new[] { await Fs(env, null), await Fs(env, "") })
        {
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.Contains("no-store", r.Header("Cache-Control") ?? "");
            Assert.Equal("", r.Str("path"));
            Assert.Equal(JsonValueKind.Null, r.Json.GetProperty("parent").ValueKind);
            Assert.Equal("/", r.Str("separator"));
            var ents = Entries(r);
            Assert.True(ents.Any(e => e.Name == "/"), "roots view must offer '/': " + r.Raw);
            Assert.True(ents.Count >= 1);
            Assert.True(ents.All(e => e.Type == "dir" && !e.Factorio), r.Raw);
            Assert.False(r.Json.GetProperty("truncated").GetBoolean());
        }
    }

    [Fact]
    public async Task FsList_Truncates_At2000Entries()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var failures = new List<string>();
        foreach (var (count, expectTruncated) in new[] { (2000, false), (2001, true), (2300, true) })
        {
            var root = env.Tools.Combine("many" + count);
            Directory.CreateDirectory(root);
            for (var i = 0; i < count; i++) Directory.CreateDirectory(Path.Combine(root, "d" + i.ToString("D5")));
            var r = await Fs(env, root);
            if (r.Status != HttpStatusCode.OK) { failures.Add($"{count}: {(int)r.Status}"); continue; }
            var n = r.Json.GetProperty("entries").GetArrayLength();
            if (n != Math.Min(count, 2000)) failures.Add($"{count}: {n} entries returned");
            if (r.Json.GetProperty("truncated").GetBoolean() != expectTruncated) failures.Add($"{count}: truncated flag wrong");
            var names = Entries(r).Select(e => e.Name).ToList();
            if (!names.SequenceEqual(names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))) failures.Add($"{count}: returned entries not sorted");
        }
        NoFailures(failures);
    }

    [Fact]
    public async Task FsList_Unreadable_Directory_Is403_NotAServerError()
    {
        if (Unsupported) return;
        if (Environment.UserName == "root" || Environment.GetEnvironmentVariable("USER") == "root" || (OperatingSystem.IsLinux() && File.ReadAllText("/proc/self/status").Contains("Uid:\t0\t"))) return;   // root ignores permissions
        using var env = await Env.Create();
        var root = env.Tools.Combine("locked");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.None);
        try
        {
            var r = await Fs(env, root);
            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
            Assert.False(string.IsNullOrWhiteSpace(r.Str("error")));
        }
        finally { File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task FsList_Symlinks_DoNotLeak_DoNotHang_AndAreNot5xx()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var root = env.Tools.Combine("links");
        var secret = env.Tools.Combine("secretdir");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "passwords.txt"), "TOP-SECRET-CONTENT");
        File.WriteAllText(Path.Combine(secret, "factorio"), "TOP-SECRET-FACTORIO");
        Directory.CreateSymbolicLink(Path.Combine(root, "link-to-secret"), secret);
        Directory.CreateSymbolicLink(Path.Combine(root, "loop"), root);                 // points at its own parent
        Directory.CreateSymbolicLink(Path.Combine(root, "dangling"), env.Tools.Combine("does-not-exist"));
        File.CreateSymbolicLink(Path.Combine(root, "factorio"), "/etc/passwd");        // file symlink with the magic name
        var sw = Stopwatch.StartNew();
        var r = await Fs(env, root);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.DoesNotContain("TOP-SECRET", r.Raw);
        Assert.DoesNotContain("root:", r.Raw);
        var ents = Entries(r);
        Assert.True(ents.Any(e => e.Name == "link-to-secret" && e.Type == "dir"), "symlinked dir should be listed as a plain dir: " + r.Raw);
        Assert.True(ents.All(e => e.Type is "dir" or "file"));

        foreach (var sub in new[] { "link-to-secret", "loop", "dangling", "loop/loop/loop/link-to-secret" })
        {
            var lr = await Fs(env, Path.Combine(root, sub));
            Assert.True((int)lr.Status < 500, $"{sub}: {(int)lr.Status} {lr.Raw}");
            Assert.DoesNotContain("TOP-SECRET", lr.Raw);
            if (lr.Status == HttpStatusCode.OK)
                Assert.True(Entries(lr).All(e => e.Type == "dir" || e.Name is "factorio" or "factorio.exe"), lr.Raw);
        }
    }

    [Fact]
    public async Task FsList_Fifo_NamedFactorio_DoesNotHangTheServer()
    {
        if (Unsupported || !File.Exists("/usr/bin/mkfifo")) return;
        using var env = await Env.Create();
        var root = env.Tools.Combine("fifo");
        Directory.CreateDirectory(root);
        using (var p = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo", new[] { Path.Combine(root, "factorio") }) { UseShellExecute = false })!) p.WaitForExit();
        var r = await Fs(env, root);   // HttpClient timeout (60 s) turns a hang into a failure
        Assert.True((int)r.Status < 500, r.Raw);
        // and saving it must be rejected or at least not hang / crash
        var put = await Add(env, Path.Combine(root, "factorio"));
        Assert.True((int)put.Status < 500, put.Raw);
        Assert.Equal(HttpStatusCode.OK, (await ListInstalls(env)).Status);
    }

    [Fact]
    public async Task FsList_ResponseNeverHasMetadata_AndUnknownQueryParamsAreIgnored()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var root = env.Tools.Combine("meta");
        Directory.CreateDirectory(Path.Combine(root, "d"));
        File.WriteAllText(Path.Combine(root, "factorio"), "x");
        var r = await Send(env.Http, HttpMethod.Get, "/api/fs/list?path=" + Uri.EscapeDataString(root) + "&all=1&files=true&showHidden=1");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.DoesNotMatch("(?i)\"(size|length|mtime|modified|created|timestamp|lastWrite|bytes|mode|permissions|owner)\"", r.Raw);
        Assert.Equal(2, Entries(r).Count);
    }

    [Fact]
    public async Task FsList_IsNotCacheable_AndPostIsNotAllowed()
    {
        using var env = await Env.Create();
        var r = await Send(env.Http, HttpMethod.Post, "/api/fs/list?path=/", "{}");
        Assert.True(r.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"{(int)r.Status}");
        var d = await Send(env.Http, HttpMethod.Delete, "/api/fs/list?path=/");
        Assert.True((int)d.Status is 404 or 405 or 403, $"{(int)d.Status}");
        Assert.Equal(HttpStatusCode.OK, (await ListInstalls(env)).Status);
    }

    // ------------------------------------------------------------------ browser -> add flow

    [Fact]
    public async Task Browse_ThenAdd_TheExecutableTheListingMarkedAsFactorio()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var (install, exe) = env.MakeInstall("browse-me");
        Directory.CreateDirectory(Path.Combine(install, "data", "base"));
        File.WriteAllText(Path.Combine(install, "data", "base", "info.json"), "{\"name\":\"base\",\"version\":\"2.0.76\"}");
        // walk the way the dialog does: parent of the install -> install -> bin -> x64
        var l0 = await Fs(env, env.Tools.Path);
        Assert.Contains(Entries(l0), e => e.Name == "browse-me" && e.Type == "dir");
        var l1 = await Fs(env, install);
        Assert.Contains(Entries(l1), e => e.Name == "bin" && e.Type == "dir");
        var l2 = await Fs(env, Path.Combine(install, "bin", "x64"));
        var f = Entries(l2).Single(e => e.Factorio);
        Assert.Equal("factorio", f.Name);
        Assert.Equal("file", f.Type);
        var chosen = Path.Combine(l2.Str("path")!, f.Name);
        var added = await Add(env, chosen);
        Assert.Equal(HttpStatusCode.Created, added.Status);
        Assert.Contains(added.Json.GetProperty("installations").EnumerateArray(), e => e.GetProperty("path").GetString() == Path.GetFullPath(exe));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "calls.txt")), "browsing and adding must never run the executable");
    }

    [Fact]
    public async Task Browse_ThenAdd_TheInstallationDirectoryTheUserTyped()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var (install, exe) = env.MakeInstall("typed");
        var l = await Fs(env, install);          // the dialog prefills the input with the browsed directory
        var typed = l.Str("path")!;
        var added = await Add(env, typed);
        Assert.Equal(HttpStatusCode.Created, added.Status);
        Assert.Contains(added.Json.GetProperty("installations").EnumerateArray(), e => e.GetProperty("path").GetString() == Path.GetFullPath(exe));
    }

    [Fact]
    public async Task Add_AcceptsPathsWithSurroundingWhitespaceOrQuotes_OnlyIfTheyResolve_NeverAServerError()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var (_, exe) = env.MakeInstall("quoted");
        var failures = new List<string>();
        foreach (var raw in new[] { "\"" + exe + "\"", " " + exe, exe + " ", "'" + exe + "'" })
        {
            var r = await Add(env, raw);
            if ((int)r.Status >= 500) failures.Add($"'{raw}': {(int)r.Status} {r.Raw}");
            if (r.Status == HttpStatusCode.BadRequest && string.IsNullOrWhiteSpace(r.Str("error"))) failures.Add($"'{raw}': 400 without message");
        }
        NoFailures(failures);
    }

    [Fact]
    public async Task Add_FileTheListingNeverShows_IsRejected()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var evil = env.MakeScript("evil", "run-me.sh");
        var l = await Fs(env, Path.GetDirectoryName(evil)!);
        Assert.Empty(Entries(l));                            // not listed ...
        Assert.Equal(HttpStatusCode.BadRequest, (await Add(env, evil)).Status);   // ... and not accepted
    }

    [Fact]
    public async Task OldSettingsEndpoints_AreRemoved()
    {
        using var env = await Env.Create();
        foreach (var m in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            var r = await Send(env.Http, m, "/api/settings/factorio", m == HttpMethod.Put ? "{\"path\":null}" : null);
            Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{m}: {(int)r.Status} {r.Raw}");
        }
        Assert.False(File.Exists(env.SettingsFile));
    }
}
