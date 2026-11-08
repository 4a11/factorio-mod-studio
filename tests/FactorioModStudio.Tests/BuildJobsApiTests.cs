using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>
/// Black-box tests of the Build &amp; Run job API (validate / pack / smoke / run), written from the contract only.
/// A fake Factorio (a shell script inside the temp project) is driven through a <c>mode</c> file next to it.
/// Tests that need the fake (or /proc) are skipped on Windows.
/// </summary>
public class BuildJobsApiTests
{
    private static bool Unsupported => OperatingSystem.IsWindows() || !Directory.Exists("/proc") || !File.Exists("/bin/sh");

    private const string FakeScript = """
        #!/bin/sh
        D="$(dirname "$0")"
        echo "$$ $*" >> "$D/calls.txt"
        MODE="$(cat "$D/mode" 2>/dev/null || echo ok)"
        prev=""
        for a in "$@"; do
          if [ "$prev" = "--create" ]; then : > "$a"; fi
          prev="$a"
        done
        case "$MODE" in
          ok)
            echo "   0.100 Info Something.cpp:1: ok"
            echo "   0.200 Warning Foo.cpp:2: deprecated thing"
            exit 0 ;;
          logerror)
            echo "   0.100 Info Something.cpp:1: starting"
            echo "   0.351 Error ModManager.cpp:1744: Failed to load mod \"alpha\": __alpha__/data.lua:3: attempt to index a nil value"
            exit 0 ;;
          exit3)
            echo "boom"
            exit 3 ;;
          hang)
            echo "hanging"
            sleep 600 &
            echo "$!" >> "$D/pids.txt"
            echo "$$" >> "$D/pids.txt"
            wait ;;
          slow)
            i=1
            while [ $i -le 14 ]; do echo "slow line $i"; sleep 0.25; i=$((i+1)); done
            exit 0 ;;
          longline)
            head -c 10000 /dev/zero | tr '\0' 'x'
            echo
            exit 0 ;;
          flood)
            i=1
            while [ $i -le 6000 ]; do echo "flood $i"; i=$((i+1)); done
            exit 0 ;;
          dump)
            cfg=""; mods=""; prev=""
            for a in "$@"; do
              if [ "$prev" = "--config" ]; then cfg="$a"; fi
              if [ "$prev" = "--mod-directory" ]; then mods="$a"; fi
              prev="$a"
            done
            W="$(sed -n 's/^write-data=//p' "$cfg")"
            mkdir -p "$W/script-output"
            if tr -d ' \n\r\t' < "$mods/mod-list.json" | grep -q '"name":"alpha","enabled":true'; then
              echo '{"item":{"iron-plate":{"type":"item","name":"iron-plate","stack_size":200},"alpha-gear":{"type":"item","name":"alpha-gear","stack_size":50}}}' > "$W/script-output/data-raw-dump.json"
            else
              echo '{"item":{"iron-plate":{"type":"item","name":"iron-plate","stack_size":100},"old-thing":{"type":"item","name":"old-thing","stack_size":1}}}' > "$W/script-output/data-raw-dump.json"
            fi
            echo "   9.998 Goodbye"
            exit 0 ;;
        esac
        exit 0
        """;

    private sealed record Resp(HttpStatusCode Status, string Raw, System.Net.Http.Headers.HttpResponseHeaders Headers, System.Net.Http.Headers.HttpContentHeaders ContentHeaders)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement.Clone();
        public string? Str(string p) => Json.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public string? Header(string name) => Headers.TryGetValues(name, out var v) ? string.Join(",", v) : ContentHeaders.TryGetValues(name, out var c) ? string.Join(",", c) : null;
        public string State => Str("state") ?? "";
        public string Id => Str("id") ?? "";
        public List<(int N, string Text)> Lines =>
            Json.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => (x.GetProperty("n").GetInt32(), x.GetProperty("text").GetString() ?? "")).ToList()
                : new();
        public JsonElement Result => Json.GetProperty("result");
    }

    private static bool IsTerminal(string s) => s is "succeeded" or "failed" or "cancelled";

    private sealed class Env : IDisposable
    {
        public TempDir Project = new();
        public TempDir Workspace = new();
        public TempDir Installed = new();
        public RunningUi Ui = null!;
        public HttpClient Http = null!;
        public string Dir => Project.Path;
        public string FakePath => Project.Combine("fake-factorio.sh");
        public HashSet<int> Pids = new();

        public void Mode(string mode) => File.WriteAllText(Project.Combine("mode"), mode);

        public string Calls
        {
            get
            {
                var p = Project.Combine("calls.txt");
                if (!File.Exists(p)) return "";
                using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
        }

        public List<int> RecordedPids()
        {
            var p = Project.Combine("pids.txt");
            if (!File.Exists(p)) return new();
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s.Trim(), out var n) ? n : -1).Where(n => n > 0).ToList();
        }

        public static void AddMod(TempDir root, string folder, string name, bool valid = true, bool thumbnail = true)
        {
            var info = valid
                ? Fixtures.ValidInfo.Replace("my-mod", name)
                : $$"""{ "name": "{{name}}", "version": "1.2.3", "title": "Flawed", "factorio_version": "2.0", "dependencies": ["base >= 2.0.0"] }""";
            root.Write($"{folder}/info.json", info);
            root.Write($"{folder}/changelog.txt", new string('-', 99) + "\nVersion: 1.2.3\nDate: 2026-01-01\n  Features:\n    - Initial release.\n");
            root.Write($"{folder}/data.lua", "data:extend({})\n");
            if (thumbnail) Fixtures.WritePngHeader(root.Combine(folder, "thumbnail.png"), 144, 144);
        }

        public static async Task<Env> Create(string? factorioPath = "fake-factorio.sh", Action<Env>? seed = null, ProjectConfig? config = null)
        {
            Environment.SetEnvironmentVariable("FACTORIO_PATH", null);
            var env = new Env();
            File.WriteAllText(env.FakePath, FakeScript.Replace("\r\n", "\n"));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(env.FakePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            env.Mode("ok");
            // versions-contract: jobs resolve the game by its version; the fake lives in the project root, which is its install root
            Directory.CreateDirectory(env.Project.Combine("data", "base"));
            File.WriteAllText(env.Project.Combine("data", "base", "info.json"), "{\"name\":\"base\",\"version\":\"2.0.76\"}");
            AddMod(env.Workspace, "alpha", "alpha");
            AddMod(env.Workspace, "beta", "beta");
            AddMod(env.Workspace, "gamma", "gamma");
            AddMod(env.Workspace, "delta", "delta");
            AddMod(env.Workspace, "flawed", "flawed", valid: false);
            AddMod(env.Installed, "ro-mod_1.2.3", "ro-mod");
            Fixtures.WriteModZip(env.Installed.Path, "zipmod", "1.0.0");
            seed?.Invoke(env);

            var cfg = config ?? new ProjectConfig();
            cfg.FactorioPath = factorioPath;
            env.Ui = await UiServer.StartAsync(
                new ProjectContext(env.Project.Path, cfg, configFileExists: false),
                new UiOptions(Port: 0, ExtraModFolders: new[] { env.Installed.Path }, WorkspaceDirectory: env.Workspace.Path));
            env.Http = Client(env.Ui);
            env.Http.Timeout = TimeSpan.FromSeconds(60);
            return env;
        }

        public void Dispose()
        {
            try { Http?.Dispose(); } catch { }
            // make sure no fake process survives a failed test
            foreach (var pid in RecordedPids()) { try { Process.GetProcessById(pid).Kill(true); } catch { } }
            try { Ui?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            Project.Dispose(); Workspace.Dispose(); Installed.Dispose();
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

    private static Task<Resp> Get(HttpClient http, string url) => Send(http, HttpMethod.Get, url);

    private static async Task<string> ModId(HttpClient http, string name)
    {
        var text = await http.GetStringAsync("/api/mods");
        return JsonDocument.Parse(text).RootElement.EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    private static Task<Resp> StartRaw(HttpClient http, string modId, string rawBody, Action<HttpRequestMessage>? tweak = null) =>
        Send(http, HttpMethod.Post, $"/api/mods/{modId}/jobs", rawBody, tweak);

    private static Task<Resp> Start(HttpClient http, string modId, object body, Action<HttpRequestMessage>? tweak = null) =>
        StartRaw(http, modId, JsonSerializer.Serialize(body), tweak);

    private static Task<Resp> Cancel(HttpClient http, string jobId) => Send(http, HttpMethod.Post, $"/api/jobs/{jobId}/cancel", "{}");

    private static async Task<Resp> Start202(Env env, string mod, object body)
    {
        var id = await ModId(env.Http, mod);
        var r = await Start(env.Http, id, body);
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        Assert.True(r.Id.Length > 0, "job id expected: " + r.Raw);
        return r;
    }

    private static async Task<Resp> WaitFor(HttpClient http, string jobId, Func<Resp, bool> done, int seconds = 40, int after = 0)
    {
        var sw = Stopwatch.StartNew();
        Resp last;
        do
        {
            last = await Get(http, $"/api/jobs/{jobId}?after={after}");
            if (last.Status == HttpStatusCode.OK && done(last)) return last;
            await Task.Delay(100);
        } while (sw.Elapsed < TimeSpan.FromSeconds(seconds));
        throw new XunitException($"Timeout waiting for job {jobId}; last: {last.Status} {last.Raw}");
    }

    private static Task<Resp> WaitTerminal(HttpClient http, string jobId, int seconds = 40) => WaitFor(http, jobId, r => IsTerminal(r.State), seconds);

    private static async Task<Resp> RunToEnd(Env env, string mod, object body)
    {
        var started = await Start202(env, mod, body);
        return await WaitTerminal(env.Http, started.Id);
    }

    private static async Task WaitForPids(Env env, int count = 2, int seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        while (env.RecordedPids().Count < count)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds)) throw new XunitException("fake factorio never recorded its pids; calls: " + env.Calls);
            await Task.Delay(100);
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')');
            return close < 0 || stat[(close + 2)] != 'Z';
        }
        catch { return false; }
    }

    private static async Task AssertAllDead(IEnumerable<int> pids, int seconds = 8)
    {
        var list = pids.ToList();
        var sw = Stopwatch.StartNew();
        while (list.Any(Alive) && sw.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(100);
        var alive = list.Where(Alive).ToList();
        Assert.True(alive.Count == 0, "processes still alive after cancel/shutdown: " + string.Join(",", alive));
    }

    private static async Task<List<JsonElement>> List(HttpClient http, string url = "/api/jobs")
    {
        var r = await Get(http, url);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Json.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    // ------------------------------------------------------------------ environment

    [Fact]
    public async Task Environment_FoundWithFakeFactorio()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var r = await Get(env.Http, "/api/environment");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var f = r.Json.GetProperty("factorio");
        Assert.True(f.GetProperty("found").GetBoolean(), r.Raw);
        Assert.Equal(Path.GetFullPath(env.FakePath), Path.GetFullPath(f.GetProperty("path").GetString()!));
        Assert.Equal(JsonValueKind.Null, f.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Environment_NotFound_ReportsError_AndNoPath()
    {
        using var env = await Env.Create("definitely/missing/factorio");
        var r = await Get(env.Http, "/api/environment");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var f = r.Json.GetProperty("factorio");
        Assert.False(f.GetProperty("found").GetBoolean(), r.Raw);
        Assert.Equal(JsonValueKind.Null, f.GetProperty("path").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(f.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Environment_ForeignHost_IsRejected()
    {
        using var env = await Env.Create();
        var r = await Send(env.Http, HttpMethod.Get, "/api/environment", tweak: q => q.Headers.Host = "attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
    }

    // ------------------------------------------------------------------ request validation

    [Fact]
    public async Task Start_RequiresToken_SameOrigin_AndLocalHost_AndCreatesNothing()
    {
        using var env = await Env.Create();
        var id = await ModId(env.Http, "alpha");
        using var anonymous = Client(env.Ui, token: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Start(anonymous, id, new { kind = "validate" })).Status);

        using var wrong = new HttpClient { BaseAddress = new Uri(env.Ui.Url) };
        wrong.DefaultRequestHeaders.Add("X-FMS-Token", "nope");
        Assert.Equal(HttpStatusCode.Forbidden, (await Start(wrong, id, new { kind = "validate" })).Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await Start(env.Http, id, new { kind = "validate" }, r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.example"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Start(env.Http, id, new { kind = "validate" }, r => r.Headers.Host = "attacker.example")).Status);
        Assert.Empty(await List(env.Http));

        var ok = await Start(env.Http, id, new { kind = "validate" }, r => r.Headers.TryAddWithoutValidation("Origin", env.Ui.Url.TrimEnd('/')));
        Assert.Equal(HttpStatusCode.Accepted, ok.Status);
    }

    [Fact]
    public async Task Cancel_RequiresToken_AndOrigin()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        var job = await Start202(env, "alpha", new { kind = "smoke" });
        await WaitForPids(env);
        using var anonymous = Client(env.Ui, token: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cancel(anonymous, job.Id)).Status);
        var evil = await Send(env.Http, HttpMethod.Post, $"/api/jobs/{job.Id}/cancel", "{}", r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, evil.Status);
        Assert.Equal("running", (await Get(env.Http, $"/api/jobs/{job.Id}")).State);
        Assert.Equal(HttpStatusCode.OK, (await Cancel(env.Http, job.Id)).Status);
    }

    [Fact]
    public async Task Start_BadInput_Returns400_404_403_413()
    {
        using var env = await Env.Create();
        var alpha = await ModId(env.Http, "alpha");
        var ro = await ModId(env.Http, "ro-mod");

        async Task Bad(string raw, HttpStatusCode expected, string? mod = null)
        {
            var r = await StartRaw(env.Http, mod ?? alpha, raw);
            Assert.True(r.Status == expected, $"{mod ?? "alpha"} {raw.Substring(0, Math.Min(60, raw.Length))} expected {expected} got {r.Status}");
            Assert.False(string.IsNullOrWhiteSpace(r.Str("error")), "error JSON expected for " + raw + ": " + r.Raw);
        }

        await Bad("{ not json", HttpStatusCode.BadRequest);
        await Bad("", HttpStatusCode.BadRequest);
        await Bad("[]", HttpStatusCode.BadRequest);
        await Bad("{}", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"deploy"}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":42}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"SMOKE"}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","ticks":0}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","ticks":-5}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","ticks":216001}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","ticks":"600"}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","ticks":1.5}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","ticks":99999999999999}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","seed":"x"}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","seed":1.5}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","timeoutMinutes":0}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","timeoutMinutes":61}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"smoke","timeoutMinutes":"5"}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"pack","force":"yes"}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"pack","force":1}""", HttpStatusCode.BadRequest);
        await Bad("""{"kind":"validate"}""", HttpStatusCode.NotFound, "no-such-mod");
        await Bad("""{"kind":"pack"}""", HttpStatusCode.Forbidden, ro);
        await Bad("""{"kind":"smoke"}""", HttpStatusCode.Forbidden, ro);
        await Bad("""{"kind":"run"}""", HttpStatusCode.Forbidden, ro);
        await Bad("{\"kind\":\"validate\",\"pad\":\"" + new string('x', 5000) + "\"}", HttpStatusCode.RequestEntityTooLarge);

        Assert.Empty(await List(env.Http));
        Assert.False(File.Exists(env.Project.Combine("calls.txt")), "no Factorio must be started by rejected requests");
        Assert.False(Directory.Exists(env.Project.Combine("dist")));
    }

    [Fact]
    public async Task Start_BodyJustUnder4KB_Works_AndBoundariesOfOptionsAreAccepted()
    {
        using var env = await Env.Create();
        var alpha = await ModId(env.Http, "alpha");
        var pad = new string('x', 3000);
        var r = await StartRaw(env.Http, alpha, "{\"kind\":\"validate\",\"pad\":\"" + pad + "\"}");
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        await WaitTerminal(env.Http, r.Id);

        if (Unsupported) return;
        foreach (var opts in new[] { "\"ticks\":1,\"seed\":-7,\"timeoutMinutes\":1", "\"ticks\":216000,\"seed\":0,\"timeoutMinutes\":60" })
        {
            var s = await StartRaw(env.Http, alpha, "{\"kind\":\"smoke\"," + opts + "}");
            Assert.Equal(HttpStatusCode.Accepted, s.Status);
            Assert.Equal("succeeded", (await WaitTerminal(env.Http, s.Id)).State);
        }
        Assert.Contains("--benchmark-ticks 216000", env.Calls);
        Assert.Contains("--benchmark-ticks 1 ", env.Calls.Replace("\n", " \n"));
    }

    [Fact]
    public async Task Start_ReturnsSnapshotFields_And_ModAndJobEndpoints404ForUnknown()
    {
        using var env = await Env.Create();
        var alpha = await ModId(env.Http, "alpha");
        var r = await Start(env.Http, alpha, new { kind = "validate" });
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        Assert.Equal(alpha, r.Str("modId"));
        Assert.Equal("validate", r.Str("kind"));
        Assert.True(r.State is "queued" or "running" or "succeeded", r.Raw);
        Assert.True(DateTime.TryParse(r.Str("startedAt"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var started) && started.Kind == DateTimeKind.Utc, "startedAt must be ISO UTC: " + r.Raw);

        Assert.Equal(HttpStatusCode.NotFound, (await Get(env.Http, "/api/jobs/nope")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(env.Http, "/api/jobs/00000000000000000000000000000000")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Cancel(env.Http, "nope")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Cancel(env.Http, "00000000000000000000000000000000")).Status);
    }

    // ------------------------------------------------------------------ validate

    [Fact]
    public async Task Validate_CleanMod_Succeeds_WithResultShape_AndStateMachineIsMonotonic()
    {
        using var env = await Env.Create();
        var job = await RunToEnd(env, "alpha", new { kind = "validate" });
        Assert.Equal("succeeded", job.State);
        Assert.Equal(JsonValueKind.Null, job.Json.GetProperty("error").ValueKind);
        Assert.True(DateTime.TryParse(job.Str("finishedAt"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var fin) && fin.Kind == DateTimeKind.Utc, job.Raw);
        Assert.Equal(0, job.Result.GetProperty("errors").GetInt32());
        Assert.True(job.Result.GetProperty("warnings").GetInt32() >= 0);
        Assert.True(job.Result.GetProperty("infos").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Array, job.Result.GetProperty("issues").ValueKind);
        Assert.False(job.Json.GetProperty("truncated").GetBoolean());
        Assert.NotEmpty(job.Lines); // at least the summary line
        // terminal state never changes again
        await Task.Delay(300);
        var again = await Get(env.Http, $"/api/jobs/{job.Id}");
        Assert.Equal("succeeded", again.State);
        Assert.Equal(job.Str("finishedAt"), again.Str("finishedAt"));
        Assert.False(File.Exists(env.Project.Combine("calls.txt")));
    }

    [Fact]
    public async Task Validate_FlawedMod_Fails_WithIssues_AndNoDistOrProfileCreated()
    {
        using var env = await Env.Create();
        var job = await RunToEnd(env, "flawed", new { kind = "validate" });
        Assert.Equal("failed", job.State);
        Assert.False(string.IsNullOrWhiteSpace(job.Str("error")));
        Assert.True(job.Result.GetProperty("errors").GetInt32() >= 1, job.Raw);
        var issues = job.Result.GetProperty("issues").EnumerateArray().ToList();
        Assert.True(issues.Count >= 1);
        Assert.Contains(issues, i => i.GetProperty("severity").GetString() == "error");
        foreach (var i in issues)
        {
            Assert.True(i.GetProperty("severity").GetString() is "error" or "warning" or "info");
            Assert.StartsWith("FMS", i.GetProperty("code").GetString());
            Assert.False(string.IsNullOrEmpty(i.GetProperty("message").GetString()));
            Assert.True(i.TryGetProperty("file", out _) && i.TryGetProperty("line", out _), "file/line properties must be present (nullable)");
        }
        Assert.Equal(job.Result.GetProperty("errors").GetInt32(), issues.Count(i => i.GetProperty("severity").GetString() == "error"));
        Assert.True(job.Lines.Count >= issues.Count, "one log line per issue + summary");
        Assert.Contains(job.Lines, l => l.Text.Contains("FMS"));
        Assert.False(Directory.Exists(env.Project.Combine("dist")));
        Assert.False(Directory.Exists(env.Project.Combine(".fmstudio")));
    }

    [Fact]
    public async Task Validate_WorksOnReadOnlyAndZipMods()
    {
        using var env = await Env.Create();
        foreach (var name in new[] { "ro-mod", "zipmod" })
        {
            var started = await Start202(env, name, new { kind = "validate" });
            var job = await WaitTerminal(env.Http, started.Id);
            Assert.True(job.State is "succeeded" or "failed", job.Raw);
            Assert.Equal(JsonValueKind.Object, job.Result.ValueKind);
            Assert.True(job.Result.TryGetProperty("issues", out _));
        }
        var roJob = (await List(env.Http, "/api/jobs?modId=" + await ModId(env.Http, "ro-mod"))).Single();
        Assert.Equal("succeeded", roJob.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Validate_WarningsOnly_StillSucceeds()
    {
        using var env = await Env.Create(seed: e => Env.AddMod(e.Workspace, "nothumb", "nothumb", thumbnail: false));
        var job = await RunToEnd(env, "nothumb", new { kind = "validate" });
        if (job.Result.GetProperty("errors").GetInt32() == 0 && job.Result.GetProperty("warnings").GetInt32() > 0)
            Assert.Equal("succeeded", job.State);
        else if (job.Result.GetProperty("errors").GetInt32() == 0)
            Assert.Equal("succeeded", job.State);
        else
            Assert.Equal("failed", job.State);
    }

    // ------------------------------------------------------------------ pack + downloads

    [Fact]
    public async Task Pack_Success_ProducesArchive_Download_AndResultShape()
    {
        using var env = await Env.Create();
        var job = await RunToEnd(env, "alpha", new { kind = "pack" });
        Assert.Equal("succeeded", job.State);
        var res = job.Result;
        Assert.True(res.GetProperty("success").GetBoolean());
        Assert.Equal("alpha_1.2.3.zip", res.GetProperty("archive").GetString());
        Assert.Equal("/api/downloads/alpha_1.2.3.zip", res.GetProperty("downloadUrl").GetString());
        Assert.True(res.GetProperty("fileCount").GetInt32() >= 4);
        var archive = env.Project.Combine("dist", "alpha_1.2.3.zip");
        Assert.True(File.Exists(archive), "archive must be written to <project root>/dist");
        Assert.Equal(new FileInfo(archive).Length, res.GetProperty("bytes").GetInt64());
        Assert.Equal(JsonValueKind.Array, res.GetProperty("issues").ValueKind);
        Assert.Contains(job.Lines, l => l.Text.Contains("Packed"));

        using var dl = await env.Http.GetAsync("/api/downloads/alpha_1.2.3.zip");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("application/zip", dl.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", dl.Content.Headers.ContentDisposition?.ToString() ?? "");
        var bytes = await dl.Content.ReadAsByteArrayAsync();
        Assert.Equal(new FileInfo(archive).Length, (long)bytes.Length);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries.Select(e => e.FullName), e => e.EndsWith("info.json"));
    }

    [Fact]
    public async Task Pack_WithErrors_FailsWithoutForce_AndNothingIsWritten()
    {
        using var env = await Env.Create();
        var job = await RunToEnd(env, "flawed", new { kind = "pack" });
        Assert.Equal("failed", job.State);
        Assert.False(string.IsNullOrWhiteSpace(job.Str("error")));
        Assert.False(job.Result.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, job.Result.GetProperty("archive").ValueKind);
        Assert.Equal(JsonValueKind.Null, job.Result.GetProperty("downloadUrl").ValueKind);
        Assert.Contains(job.Result.GetProperty("issues").EnumerateArray(), i => i.GetProperty("severity").GetString() == "error");
        Assert.True(!Directory.Exists(env.Project.Combine("dist")) || Directory.GetFiles(env.Project.Combine("dist")).Length == 0);

        // force:false explicitly is the same
        var explicitFalse = await RunToEnd(env, "flawed", new { kind = "pack", force = false });
        Assert.Equal("failed", explicitFalse.State);
    }

    [Fact]
    public async Task Pack_WithErrors_AndForce_Succeeds()
    {
        using var env = await Env.Create();
        var job = await RunToEnd(env, "flawed", new { kind = "pack", force = true });
        Assert.Equal("succeeded", job.State);
        Assert.True(job.Result.GetProperty("success").GetBoolean());
        var name = job.Result.GetProperty("archive").GetString()!;
        Assert.True(File.Exists(env.Project.Combine("dist", name)));
        using var dl = await env.Http.GetAsync(job.Result.GetProperty("downloadUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
    }

    [Fact]
    public async Task Pack_ParallelOnDifferentMods_IsAllowed()
    {
        using var env = await Env.Create();
        var a = await Start202(env, "alpha", new { kind = "pack" });
        var b = await Start202(env, "beta", new { kind = "pack" });
        var c = await Start202(env, "gamma", new { kind = "validate" });
        foreach (var j in new[] { a, b, c }) Assert.Equal("succeeded", (await WaitTerminal(env.Http, j.Id)).State);
        Assert.True(File.Exists(env.Project.Combine("dist", "alpha_1.2.3.zip")));
        Assert.True(File.Exists(env.Project.Combine("dist", "beta_1.2.3.zip")));
    }

    [Fact]
    public async Task Downloads_Allowlist_RejectsEverythingElse()
    {
        using var env = await Env.Create();
        await RunToEnd(env, "alpha", new { kind = "pack" });
        var dist = env.Project.Combine("dist");
        File.WriteAllText(Path.Combine(dist, "notes.txt"), "secret");
        File.WriteAllText(Path.Combine(dist, "a b.zip"), "PK");
        Directory.CreateDirectory(Path.Combine(dist, "sub"));
        File.WriteAllBytes(Path.Combine(dist, "sub", "x.zip"), new byte[] { 1, 2, 3 });
        File.WriteAllText(env.Project.Combine("outside.zip"), "outside-secret");
        File.WriteAllText(env.Project.Combine("fmstudio.json"), "{}");

        var rejected = new[]
        {
            "notes.txt", "a%20b.zip", "sub%2fx.zip", "sub%2Fx.zip", "sub%5cx.zip", "..%2foutside.zip", "..%2Foutside.zip", "%2e%2e%2foutside.zip",
            "%2e%2e%5coutside.zip", "..%5coutside.zip", "..", "%2e%2e", "alpha_1.2.3.zip%00.txt", "alpha_1.2.3.zip%00", "alpha_1.2.3.ZIPX",
            "alpha_1.2.3.zip.txt", "missing_1.0.0.zip", ".zip.", "%252e%252e%252foutside.zip", "..%c0%afoutside.zip"
        };
        foreach (var name in rejected)
        {
            var r = await Send(env.Http, HttpMethod.Get, "/api/downloads/" + name);
            Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"/api/downloads/{name} -> {(int)r.Status}");
            Assert.DoesNotContain("secret", r.Raw);
        }
        foreach (var path in new[] { "/api/downloads/", "/api/downloads", "/api/downloads/../outside.zip", "/api/downloads/sub/x.zip", "/api/downloads/%2e%2e/outside.zip" })
        {
            var r = await Send(env.Http, HttpMethod.Get, path);
            Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed, $"{path} -> {(int)r.Status}");
            Assert.DoesNotContain("secret", r.Raw);
        }
        Assert.Equal(HttpStatusCode.OK, (await Get(env.Http, "/api/downloads/alpha_1.2.3.zip")).Status);
    }

    [Fact]
    public async Task Downloads_SymlinkInDist_PointingOutside_IsNotServed()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        await RunToEnd(env, "alpha", new { kind = "pack" });
        using var outside = new TempDir();
        var target = outside.Write("stolen.zip", "TOP-SECRET");
        try { File.CreateSymbolicLink(env.Project.Combine("dist", "link.zip"), target); }
        catch { return; }
        var r = await Get(env.Http, "/api/downloads/link.zip");
        Assert.True(r.Status != HttpStatusCode.OK && !r.Raw.Contains("TOP-SECRET"), $"symlink escaping dist must not be served (got {(int)r.Status})");
    }

    [Fact]
    public async Task Downloads_ForeignHost_IsRejected_AndMissingIs404()
    {
        using var env = await Env.Create();
        await RunToEnd(env, "alpha", new { kind = "pack" });
        var r = await Send(env.Http, HttpMethod.Get, "/api/downloads/alpha_1.2.3.zip", tweak: q => q.Headers.Host = "attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(env.Http, "/api/downloads/nothing_1.0.0.zip")).Status);
    }

    // ------------------------------------------------------------------ smoke

    [Fact]
    public async Task Smoke_Passes_WithFake_AndResultShape_AndFactorioArguments()
    {
        if (Unsupported) return;
        var cfg = new ProjectConfig();
        cfg.Profiles["default"] = new ProfileConfig { Args = { "--fake-extra-arg" } };
        using var env = await Env.Create(config: cfg);
        var job = await RunToEnd(env, "alpha", new { kind = "smoke", ticks = 777, seed = 42 });
        Assert.Equal("succeeded", job.State);
        Assert.Equal(JsonValueKind.Null, job.Json.GetProperty("error").ValueKind);
        var res = job.Result;
        Assert.True(res.GetProperty("passed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, res.GetProperty("failureReason").ValueKind);
        Assert.Equal(0, res.GetProperty("errorCount").GetInt32());
        Assert.True(res.GetProperty("warningCount").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Array, res.GetProperty("diagnostics").ValueKind);
        Assert.Contains(job.Lines, l => l.Text.Contains("deprecated thing"));
        Assert.Contains(job.Lines, l => l.Text.Contains("Something.cpp:1: ok"));

        var calls = env.Calls;
        Assert.Contains("--create", calls);
        Assert.Contains("--map-gen-seed 42", calls);
        Assert.Contains("--benchmark-ticks 777", calls);
        Assert.Contains("--fake-extra-arg", calls);
        Assert.Contains("ui-alpha-2.0", calls);

        // line numbers: strictly increasing, starting at 1
        var ns = job.Lines.Select(l => l.N).ToList();
        Assert.Equal(1, ns.First());
        for (var i = 1; i < ns.Count; i++) Assert.True(ns[i] > ns[i - 1]);
    }

    [Fact]
    public async Task Smoke_DefaultsAre600TicksSeed1()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var job = await RunToEnd(env, "alpha", new { kind = "smoke" });
        Assert.Equal("succeeded", job.State);
        Assert.Contains("--benchmark-ticks 600", env.Calls);
        Assert.Contains("--map-gen-seed 1", env.Calls);
    }

    [Fact]
    public async Task Smoke_LogErrorLine_FailsWithDiagnostics()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("logerror");
        var job = await RunToEnd(env, "alpha", new { kind = "smoke" });
        Assert.Equal("failed", job.State);
        Assert.False(string.IsNullOrWhiteSpace(job.Str("error")));
        Assert.False(job.Result.GetProperty("passed").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(job.Result.GetProperty("failureReason").GetString()));
        Assert.True(job.Result.GetProperty("errorCount").GetInt32() >= 1);
        Assert.Contains(job.Lines, l => l.Text.Contains("Failed to load mod"));
        var diag = job.Result.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.NotEmpty(diag);
    }

    [Fact]
    public async Task Smoke_NonZeroExit_Fails_WithReason()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("exit3");
        var job = await RunToEnd(env, "alpha", new { kind = "smoke" });
        Assert.Equal("failed", job.State);
        Assert.False(job.Result.GetProperty("passed").GetBoolean());
        Assert.Contains("3", job.Result.GetProperty("failureReason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(job.Str("error")));
    }

    [Fact]
    public async Task Smoke_WithValidationErrors_DoesNotStartFactorio()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var job = await RunToEnd(env, "flawed", new { kind = "smoke" });
        Assert.Equal("failed", job.State);
        Assert.Contains("validation", job.Str("error")!.ToLowerInvariant());
        Assert.False(File.Exists(env.Project.Combine("calls.txt")), "Factorio must not be started; calls: " + env.Calls);
        Assert.False(Directory.Exists(env.Project.Combine(".fmstudio", "profiles", "ui-flawed-2.0")) && Directory.GetFiles(env.Project.Combine(".fmstudio", "profiles", "ui-flawed-2.0"), "smoke-save.zip", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task SmokeAndRun_WithoutFactorio_FailWithError_AndStartNothing()
    {
        using var env = await Env.Create("definitely/missing/factorio");
        foreach (var kind in new[] { "smoke", "run" })
        {
            var job = await RunToEnd(env, "alpha", new { kind });
            Assert.Equal("failed", job.State);
            Assert.False(string.IsNullOrWhiteSpace(job.Str("error")));
        }
        Assert.False(File.Exists(env.Project.Combine("calls.txt")));
        // other kinds are unaffected
        Assert.Equal("succeeded", (await RunToEnd(env, "alpha", new { kind = "validate" })).State);
        Assert.Equal("succeeded", (await RunToEnd(env, "alpha", new { kind = "pack" })).State);
    }

    [Fact]
    public async Task Smoke_NeverTouchesTheUsersDefaultProfile_AndUsesUiProfile()
    {
        if (Unsupported) return;
        using var env = await Env.Create(seed: e =>
        {
            e.Project.Write(".fmstudio/profiles/default/mods/mod-list.json", "{\"mods\":[{\"name\":\"base\",\"enabled\":true}]}");
            e.Project.Write(".fmstudio/profiles/default/marker.txt", "mine");
        });
        string Snapshot()
        {
            var root = env.Project.Combine(".fmstudio", "profiles", "default");
            return string.Join("|", Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
                .Select(f => Path.GetRelativePath(root, f) + "=" + File.ReadAllText(f)));
        }
        var before = Snapshot();
        Assert.Equal("succeeded", (await RunToEnd(env, "alpha", new { kind = "smoke" })).State);
        Assert.Equal("succeeded", (await RunToEnd(env, "beta", new { kind = "smoke" })).State);
        Assert.Equal(before, Snapshot());
        Assert.True(Directory.Exists(env.Project.Combine(".fmstudio", "profiles", "ui-alpha-2.0")), "UI profile directory ui-<modname>-<MAJOR>.<MINOR> expected");
        Assert.True(Directory.Exists(env.Project.Combine(".fmstudio", "profiles", "ui-beta-2.0")));
        foreach (var line in env.Calls.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.Contains("--mod-directory")) Assert.DoesNotContain("profiles/default", line);
        // the mod copy used by Factorio is in the UI profile
        Assert.True(File.Exists(env.Project.Combine(".fmstudio", "profiles", "ui-alpha-2.0", "mods", "mod-list.json")));
    }

    [Fact]
    public async Task Run_ExitZero_Succeeds_WithExitCode_AndNonZeroFails()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        var ok = await RunToEnd(env, "alpha", new { kind = "run" });
        Assert.Equal("succeeded", ok.State);
        Assert.Equal(0, ok.Result.GetProperty("exitCode").GetInt32());
        Assert.Contains(ok.Lines, l => l.Text.Contains("Something.cpp:1: ok"));

        env.Mode("exit3");
        var bad = await RunToEnd(env, "alpha", new { kind = "run" });
        Assert.Equal("failed", bad.State);
        Assert.Equal(3, bad.Result.GetProperty("exitCode").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(bad.Str("error")));
    }

    [Fact]
    public async Task Run_ReportsWhatTheGameLogged_AsDiagnostics()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("logerror");
        var job = await RunToEnd(env, "alpha", new { kind = "run" });

        // The game ran and closed normally; its log still had an error, which the result carries (here from its output:
        // the fake game has no install folder and so no factorio-current.log).
        Assert.Equal("succeeded", job.State);
        Assert.True(job.Result.GetProperty("errorCount").GetInt32() >= 1);
        var diag = job.Result.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.Contains(diag, d => d.GetProperty("message").GetString()!.Contains("Failed to load mod"));
        Assert.Equal(JsonValueKind.Null, job.Result.TryGetProperty("logFile", out var log) ? log.ValueKind : JsonValueKind.Null);
        Assert.Contains(job.Lines, l => l.Text.Contains("Game output: 1 error(s)"));
    }

    // ------------------------------------------------------------------ busy rules

    [Fact]
    public async Task Busy_PerMod_And_ServerWideSmokeRun_Return409WithActiveJobId()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        var alpha = await ModId(env.Http, "alpha");
        var beta = await ModId(env.Http, "beta");
        var first = await Start202(env, "alpha", new { kind = "smoke" });
        await WaitForPids(env);

        async Task AssertBusy(string mod, string kind)
        {
            var r = await Start(env.Http, mod, new { kind });
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.False(string.IsNullOrWhiteSpace(r.Str("error")));
            Assert.Equal(first.Id, r.Str("jobId"));
        }
        // same mod: every kind is busy
        foreach (var kind in new[] { "validate", "pack", "smoke", "run" }) await AssertBusy(alpha, kind);
        // other mod: smoke and run are blocked server-wide
        await AssertBusy(beta, "smoke");
        await AssertBusy(beta, "run");
        // validate/pack on another mod still run in parallel
        var v = await Start(env.Http, beta, new { kind = "validate" });
        Assert.Equal(HttpStatusCode.Accepted, v.Status);
        Assert.Equal("succeeded", (await WaitTerminal(env.Http, v.Id)).State);
        var p = await Start(env.Http, beta, new { kind = "pack" });
        Assert.Equal(HttpStatusCode.Accepted, p.Status);
        Assert.Equal("succeeded", (await WaitTerminal(env.Http, p.Id)).State);

        Assert.Equal(HttpStatusCode.OK, (await Cancel(env.Http, first.Id)).Status);
        // once cancelled, the slot is free again
        env.Mode("ok");
        var next = await Start(env.Http, beta, new { kind = "smoke" });
        Assert.Equal(HttpStatusCode.Accepted, next.Status);
        Assert.Equal("succeeded", (await WaitTerminal(env.Http, next.Id)).State);
    }

    [Fact]
    public async Task Busy_RunBlocksSmokeAndViceVersa()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        var beta = await ModId(env.Http, "beta");
        var run = await Start202(env, "alpha", new { kind = "run" });
        await WaitForPids(env);
        var r = await Start(env.Http, beta, new { kind = "smoke" });
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal(run.Id, r.Str("jobId"));
        Assert.Equal(HttpStatusCode.OK, (await Cancel(env.Http, run.Id)).Status);
    }

    [Fact]
    public async Task Busy_ConcurrentStarts_ExactlyOneWins()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        var ids = new[] { "alpha", "beta", "gamma", "delta" };
        var modIds = new List<string>();
        foreach (var n in ids) modIds.Add(await ModId(env.Http, n));

        // 12 simultaneous smoke/run starts across 4 mods (server-wide limit) and 3 on the same mod
        var attempts = Enumerable.Range(0, 12).Select(i => Task.Run(() => Start(env.Http, modIds[i % 4], new { kind = i % 2 == 0 ? "smoke" : "run" }))).ToList();
        var results = await Task.WhenAll(attempts);
        var winners = results.Where(r => r.Status == HttpStatusCode.Accepted).ToList();
        Assert.Equal(1, winners.Count);
        foreach (var loser in results.Where(r => r.Status != HttpStatusCode.Accepted))
        {
            Assert.Equal(HttpStatusCode.Conflict, loser.Status);
            Assert.Equal(winners[0].Id, loser.Str("jobId"));
        }
        Assert.Equal(1, (await List(env.Http)).Count);
        Assert.Equal(HttpStatusCode.OK, (await Cancel(env.Http, winners[0].Id)).Status);

        // same mod, validate races: only one active at a time; after it finishes a new one is accepted
        env.Mode("ok");
        var races = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => Start(env.Http, modIds[0], new { kind = "pack" }))));
        Assert.True(races.Count(r => r.Status == HttpStatusCode.Accepted) >= 1);
        Assert.True(races.All(r => r.Status is HttpStatusCode.Accepted or HttpStatusCode.Conflict), string.Join(",", races.Select(r => (int)r.Status)));
        foreach (var w in races.Where(r => r.Status == HttpStatusCode.Accepted)) await WaitTerminal(env.Http, w.Id);
    }

    // ------------------------------------------------------------------ cancel

    [Fact]
    public async Task Cancel_RunningSmoke_KillsProcessTree_AndStateIsCancelled()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        var job = await Start202(env, "alpha", new { kind = "smoke" });
        await WaitForPids(env);
        var pids = env.RecordedPids();
        Assert.True(pids.All(Alive), "fake should be running before cancel");
        var running = await Get(env.Http, $"/api/jobs/{job.Id}");
        Assert.Equal("running", running.State);
        Assert.Equal(JsonValueKind.Null, running.Json.GetProperty("finishedAt").ValueKind);
        Assert.Contains(running.Lines, l => l.Text.Contains("hanging"));

        var c = await Cancel(env.Http, job.Id);
        Assert.Equal(HttpStatusCode.OK, c.Status);
        Assert.Equal("cancelled", c.State);
        await AssertAllDead(pids);
        var after = await Get(env.Http, $"/api/jobs/{job.Id}");
        Assert.Equal("cancelled", after.State);
        Assert.True(after.Str("finishedAt") is { Length: > 0 });
        await Task.Delay(500);
        Assert.Equal("cancelled", (await Get(env.Http, $"/api/jobs/{job.Id}")).State);
        // the map-generation step was cancelled: the benchmark step must never be started
        Assert.DoesNotContain("--benchmark", env.Calls);
        Assert.Equal(1, env.Calls.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Cancel_RunningRun_KillsProcessTree()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        var job = await Start202(env, "alpha", new { kind = "run" });
        await WaitForPids(env);
        var pids = env.RecordedPids();
        var c = await Cancel(env.Http, job.Id);
        Assert.Equal(HttpStatusCode.OK, c.Status);
        Assert.Equal("cancelled", c.State);
        await AssertAllDead(pids);
    }

    [Fact]
    public async Task Cancel_FinishedJob_Returns409_AndKeepsState()
    {
        using var env = await Env.Create();
        var done = await RunToEnd(env, "alpha", new { kind = "validate" });
        var r = await Cancel(env.Http, done.Id);
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Str("error")));
        Assert.Equal("succeeded", (await Get(env.Http, $"/api/jobs/{done.Id}")).State);
        var failed = await RunToEnd(env, "flawed", new { kind = "validate" });
        Assert.Equal(HttpStatusCode.Conflict, (await Cancel(env.Http, failed.Id)).Status);
        Assert.Equal("failed", (await Get(env.Http, $"/api/jobs/{failed.Id}")).State);
    }

    [Fact]
    public async Task Cancel_Twice_SecondIs409()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        var job = await Start202(env, "alpha", new { kind = "smoke" });
        await WaitForPids(env);
        Assert.Equal(HttpStatusCode.OK, (await Cancel(env.Http, job.Id)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Cancel(env.Http, job.Id)).Status);
    }

    [Fact]
    public async Task Cancel_RaceWithNaturalFinish_IsOkOr409_AndStateIsTerminal()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("slow");
        var job = await Start202(env, "alpha", new { kind = "smoke" });
        await Task.Delay(300);
        var c = await Cancel(env.Http, job.Id);
        Assert.True(c.Status is HttpStatusCode.OK or HttpStatusCode.Conflict);
        var final = await WaitTerminal(env.Http, job.Id);
        if (c.Status == HttpStatusCode.OK) Assert.Equal("cancelled", final.State);
        await Task.Delay(300);
        Assert.Equal(final.State, (await Get(env.Http, $"/api/jobs/{job.Id}")).State);
    }

    // ------------------------------------------------------------------ shutdown

    [Fact]
    public async Task ServerShutdown_KillsRunningChildren()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        await Start202(env, "alpha", new { kind = "run" });
        await WaitForPids(env);
        var pids = env.RecordedPids();
        await env.Ui.DisposeAsync();
        await AssertAllDead(pids, 10);
    }

    // ------------------------------------------------------------------ log: streaming, cursor, truncation

    [Fact]
    public async Task Log_StreamsLiveWhileRunning_AndAfterCursorReturnsOnlyNewerLines()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("slow");
        var job = await Start202(env, "alpha", new { kind = "smoke" });
        var counts = new List<int>();
        var sw = Stopwatch.StartNew();
        var sawRunningWithLines = false;
        var maxN = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            var snap = await Get(env.Http, $"/api/jobs/{job.Id}");
            if (snap.State == "running") { counts.Add(snap.Lines.Count); if (snap.Lines.Count > 0) sawRunningWithLines = true; }
            if (IsTerminal(snap.State)) break;
            await Task.Delay(120);
        }
        Assert.True(sawRunningWithLines, "lines must be visible while the job is still running");
        Assert.True(counts.Distinct().Count() >= 3, "log must grow while running; counts: " + string.Join(",", counts));

        var full = await WaitTerminal(env.Http, job.Id);
        Assert.Equal("succeeded", full.State);
        var lines = full.Lines;
        Assert.True(lines.Count >= 14);
        maxN = lines.Max(l => l.N);
        var mid = lines[lines.Count / 2].N;
        var tail = await Get(env.Http, $"/api/jobs/{job.Id}?after={mid}");
        Assert.True(tail.Lines.Count > 0 && tail.Lines.All(l => l.N > mid));
        Assert.Equal(mid + 1, tail.Lines.First().N);
        Assert.Equal(lines.Count(l => l.N > mid), tail.Lines.Count);
        var none = await Get(env.Http, $"/api/jobs/{job.Id}?after={maxN}");
        Assert.Empty(none.Lines);
        Assert.Equal("succeeded", none.State);
        var beyond = await Get(env.Http, $"/api/jobs/{job.Id}?after=999999");
        Assert.Empty(beyond.Lines);
        var neg = await Get(env.Http, $"/api/jobs/{job.Id}?after=-5");
        Assert.True(neg.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest);
        var junk = await Get(env.Http, $"/api/jobs/{job.Id}?after=abc");
        Assert.True(junk.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest);
        Assert.Contains("no-store", full.Header("Cache-Control") ?? "");
    }

    [Fact]
    public async Task Log_LongLinesAreCappedAt4000Chars()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("longline");
        var job = await RunToEnd(env, "alpha", new { kind = "run" });
        var xs = job.Lines.Where(l => l.Text.StartsWith("xxxx")).ToList();
        Assert.NotEmpty(xs);
        Assert.True(job.Lines.All(l => l.Text.Length <= 4000), "max line length " + job.Lines.Max(l => l.Text.Length));
        Assert.True(xs.All(l => l.Text.Length >= 3000), "line should be truncated, not dropped");
    }

    [Fact]
    public async Task Log_KeepsAtMost5000Lines_OldestDropped_Truncated()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("flood");
        var job = await RunToEnd(env, "alpha", new { kind = "run" });
        Assert.Equal("succeeded", job.State);
        Assert.True(job.Json.GetProperty("truncated").GetBoolean(), "truncated must be true when lines were dropped");
        var lines = job.Lines;
        Assert.True(lines.Count <= 5000 && lines.Count >= 4000, "kept " + lines.Count);
        Assert.Contains(lines.TakeLast(3), l => l.Text == "flood 6000");
        Assert.True(lines.All(l => l.Text != "flood 1"), "oldest line must be dropped");
        for (var i = 1; i < lines.Count; i++) Assert.True(lines[i].N > lines[i - 1].N);
        // cursor beyond the dropped head still works
        var tail = await Get(env.Http, $"/api/jobs/{job.Id}?after={lines.Last().N - 3}");
        Assert.Equal(3, tail.Lines.Count);
        // the list view carries no log lines
        var listed = (await List(env.Http)).Single();
        Assert.True(!listed.TryGetProperty("lines", out var l) || l.ValueKind != JsonValueKind.Array || l.GetArrayLength() == 0);
    }

    // ------------------------------------------------------------------ history

    [Fact]
    public async Task History_KeepsTheTwentyMostRecent_NewestFirst_AndFilterByMod()
    {
        using var env = await Env.Create();
        var alpha = await ModId(env.Http, "alpha");
        var beta = await ModId(env.Http, "beta");
        var ids = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var r = await Start(env.Http, i % 5 == 0 ? beta : alpha, new { kind = "validate" });
            Assert.Equal(HttpStatusCode.Accepted, r.Status);
            await WaitTerminal(env.Http, r.Id);
            ids.Add(r.Id);
        }
        var all = await List(env.Http);
        Assert.Equal(20, all.Count);
        Assert.Equal(ids.AsEnumerable().Reverse().Take(20).ToList(), all.Select(j => j.GetProperty("id").GetString()!).ToList());
        var times = all.Select(j => DateTime.Parse(j.GetProperty("startedAt").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind)).ToList();
        for (var i = 1; i < times.Count; i++) Assert.True(times[i] <= times[i - 1]);
        foreach (var old in ids.Take(5)) Assert.Equal(HttpStatusCode.NotFound, (await Get(env.Http, $"/api/jobs/{old}")).Status);
        foreach (var kept in ids.Skip(5)) Assert.Equal(HttpStatusCode.OK, (await Get(env.Http, $"/api/jobs/{kept}")).Status);

        var onlyBeta = await List(env.Http, "/api/jobs?modId=" + beta);
        Assert.NotEmpty(onlyBeta);
        Assert.True(onlyBeta.All(j => j.GetProperty("modId").GetString() == beta));
        var none = await List(env.Http, "/api/jobs?modId=unknown-mod");
        Assert.Empty(none);
    }

    [Fact]
    public async Task History_NeverEvictsARunningJob()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("hang");
        var running = await Start202(env, "alpha", new { kind = "smoke" });
        await WaitForPids(env);
        var beta = await ModId(env.Http, "beta");
        for (var i = 0; i < 25; i++)
        {
            var r = await Start(env.Http, beta, new { kind = "validate" });
            Assert.Equal(HttpStatusCode.Accepted, r.Status);
            await WaitTerminal(env.Http, r.Id);
        }
        Assert.Equal(HttpStatusCode.OK, (await Get(env.Http, $"/api/jobs/{running.Id}")).Status);
        var all = await List(env.Http);
        Assert.Contains(running.Id, all.Select(j => j.GetProperty("id").GetString()!));
        Assert.True(all.Count >= 20 && all.Count <= 21);
        Assert.Equal(HttpStatusCode.OK, (await Cancel(env.Http, running.Id)).Status);
    }

    [Fact]
    public async Task List_SnapshotsHaveTheContractFields()
    {
        using var env = await Env.Create();
        await RunToEnd(env, "flawed", new { kind = "validate" });
        var job = (await List(env.Http)).Single();
        foreach (var p in new[] { "id", "modId", "kind", "state", "startedAt", "finishedAt", "truncated", "error", "result" })
            Assert.True(job.TryGetProperty(p, out _), "missing property " + p);
        Assert.Equal("validate", job.GetProperty("kind").GetString());
        Assert.Equal("failed", job.GetProperty("state").GetString());
    }

    // ------------------------------------------------------------ dump: the game's own data (Prototypes tab, "In the game")

    [Fact]
    public async Task Dump_ComparesTheGameWithAndWithoutTheMod_AndReusesTheDumpWithout()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("dump");
        var alpha = await ModId(env.Http, "alpha");
        Assert.False((await Get(env.Http, $"/api/mods/{alpha}/gamedata")).Json.GetProperty("exists").GetBoolean());

        var job = await Start202(env, "alpha", new { kind = "dump" });
        var done = await WaitFor(env.Http, job.Id, r => IsTerminal(r.State));
        Assert.True(done.State == "succeeded", done.Raw);
        Assert.Equal((1, 1, 1), (done.Result.GetProperty("added").GetInt32(), done.Result.GetProperty("changed").GetInt32(), done.Result.GetProperty("removed").GetInt32()));
        Assert.Equal(2, env.Calls.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains("--dump-data")));

        var data = (await Get(env.Http, $"/api/mods/{alpha}/gamedata")).Json;
        Assert.True(data.GetProperty("exists").GetBoolean());
        Assert.False(data.GetProperty("stale").GetBoolean());
        Assert.Equal("2.0.76", data.GetProperty("gameVersion").GetString());
        Assert.Equal(new[] { "added item/alpha-gear", "changed item/iron-plate", "removed item/old-thing" },
            data.GetProperty("items").EnumerateArray().Select(i => $"{i.GetProperty("change").GetString()} {i.GetProperty("type").GetString()}/{i.GetProperty("name").GetString()}"));

        var item = (await Get(env.Http, $"/api/mods/{alpha}/gamedata/item?type=item&name=iron-plate")).Json;
        var field = Assert.Single(item.GetProperty("fields").EnumerateArray());
        Assert.Equal(("stack_size", 100, 200), (field.GetProperty("path").GetString(), field.GetProperty("before").GetInt32(), field.GetProperty("after").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, (await Get(env.Http, $"/api/mods/{alpha}/gamedata/item?type=item&name=nope")).Status);

        // The second dump only runs the game with the mod; an edit of the mod makes the saved data outdated.
        var again = await WaitFor(env.Http, (await Start202(env, "alpha", new { kind = "dump" })).Id, r => IsTerminal(r.State));
        Assert.True(again.State == "succeeded", again.Raw);
        Assert.Equal(3, env.Calls.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains("--dump-data")));
        env.Workspace.Write("alpha/data.lua", "data:extend({})\n-- edited\n");
        Assert.True((await Get(env.Http, $"/api/mods/{alpha}/gamedata")).Json.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task Dump_ThatFails_SavesNothing_AndReadOnlyModsAreRefused()
    {
        if (Unsupported) return;
        using var env = await Env.Create();
        env.Mode("exit3");
        var alpha = await ModId(env.Http, "alpha");

        var done = await WaitFor(env.Http, (await Start202(env, "alpha", new { kind = "dump" })).Id, r => IsTerminal(r.State));
        Assert.Equal("failed", done.State);
        Assert.Contains("exit code 3", done.Str("error"));
        Assert.False((await Get(env.Http, $"/api/mods/{alpha}/gamedata")).Json.GetProperty("exists").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await Start(env.Http, await ModId(env.Http, "ro-mod"), new { kind = "dump" })).Status);
    }
}
