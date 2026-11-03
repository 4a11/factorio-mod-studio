using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

internal sealed record ApiResp(HttpStatusCode Status, string Raw, System.Net.Http.Headers.HttpResponseHeaders Headers, System.Net.Http.Headers.HttpContentHeaders ContentHeaders)
{
    public JsonElement Json => JsonDocument.Parse(Raw).RootElement.Clone();
    public string? Str(string p) => Json.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public string? Header(string name) => Headers.TryGetValues(name, out var v) ? string.Join(",", v) : ContentHeaders.TryGetValues(name, out var c) ? string.Join(",", c) : null;
    public string Error => Str("error") ?? "";
    public int Code => (int)Status;

    public List<JsonElement> Installs => Json.GetProperty("installations").EnumerateArray().Select(x => x.Clone()).ToList();
    public List<JsonElement> Invalid => Json.GetProperty("invalid").EnumerateArray().Select(x => x.Clone()).ToList();

    public List<(int N, string Text)> Lines =>
        Json.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => (x.GetProperty("n").GetInt32(), x.GetProperty("text").GetString() ?? "")).ToList()
            : new();
}

internal static class J
{
    public static string? S(this JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static List<string> Strs(this JsonElement e, string p) => e.GetProperty(p).EnumerateArray().Select(x => x.GetString()!).ToList();
}

/// <summary>Server + temp project/workspace/tools folders for the Factorio installation API tests.</summary>
internal sealed class ApiEnv : IDisposable
{
    public TempDir Project = new();
    public TempDir Workspace = new();
    public TempDir Tools = new();          // fake installations live here (outside the project)
    public RunningUi Ui = null!;
    public HttpClient Http = null!;
    public string SettingsDir => Project.Combine(".fmstudio");
    public string SettingsFile => Project.Combine(".fmstudio", "settings.json");

    public static bool Unsupported => FakeFactorio.Unsupported || !Directory.Exists("/proc");

    public string Install(string name, string? version, bool info = true) => FakeFactorio.Make(Tools.Path, name, version, info);

    public string Script(string dirName, string fileName, string content = "#!/bin/sh\necho hi\n")
    {
        var p = Path.Combine(Tools.Combine(dirName), fileName);
        FakeFactorio.WriteScript(p, content);
        return p;
    }

    public static string ModInfo(string name, string? factorioVersion) =>
        factorioVersion is null
            ? $$"""{ "name": "{{name}}", "version": "1.2.3", "title": "{{name}}", "author": "Someone", "dependencies": ["base"] }"""
            : $$"""{ "name": "{{name}}", "version": "1.2.3", "title": "{{name}}", "author": "Someone", "factorio_version": "{{factorioVersion}}", "dependencies": ["base >= {{factorioVersion}}.0"] }""";

    public static void AddMod(TempDir root, string name, string? factorioVersion = "2.0", string? rawInfo = null)
    {
        root.Write($"{name}/info.json", rawInfo ?? ModInfo(name, factorioVersion));
        root.Write($"{name}/changelog.txt", new string('-', 99) + "\nVersion: 1.2.3\nDate: 2026-01-01\n  Features:\n    - Initial release.\n");
        root.Write($"{name}/data.lua", "data:extend({})\n");
        Fixtures.WritePngHeader(root.Combine(name, "thumbnail.png"), 144, 144);
    }

    public static async Task<ApiEnv> Create(string? configuredFactorio = null, Action<ApiEnv>? seed = null)
    {
        Environment.SetEnvironmentVariable("FACTORIO_PATH", null);
        var env = new ApiEnv();
        AddMod(env.Workspace, "alpha", "2.0");
        AddMod(env.Workspace, "legacy", "1.1");
        AddMod(env.Workspace, "future", "2.1");
        AddMod(env.Workspace, "farout", "3.0");
        AddMod(env.Workspace, "oddver", rawInfo: """{ "name": "oddver", "version": "1.2.3", "title": "Odd", "author": "x", "factorio_version": "abc", "dependencies": ["base"] }""");
        seed?.Invoke(env);
        await env.StartServer(configuredFactorio);
        return env;
    }

    public async Task StartServer(string? configuredFactorio)
    {
        Ui = await UiServer.StartAsync(
            new ProjectContext(Project.Path, new ProjectConfig { FactorioPath = configuredFactorio }, configFileExists: false),
            new UiOptions(Port: 0, ExtraModFolders: Array.Empty<string>(), WorkspaceDirectory: Workspace.Path));
        Http = Client(Ui);
        Http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task Restart(string? configuredFactorio = null)
    {
        Http.Dispose();
        await Ui.DisposeAsync();
        await StartServer(configuredFactorio);
    }

    public static HttpClient Client(RunningUi ui, bool token = true)
    {
        var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        if (token) http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        return http;
    }

    public static async Task<ApiResp> Send(HttpClient http, HttpMethod method, string url, string? body = null, Action<HttpRequestMessage>? tweak = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
        tweak?.Invoke(req);
        using var res = await http.SendAsync(req);
        return new ApiResp(res.StatusCode, await res.Content.ReadAsStringAsync(), res.Headers, res.Content.Headers);
    }

    // ---- installations API
    public Task<ApiResp> List(Action<HttpRequestMessage>? tweak = null) => Send(Http, HttpMethod.Get, "/api/factorio/installations", tweak: tweak);
    public Task<ApiResp> PostRaw(string raw, Action<HttpRequestMessage>? tweak = null, HttpClient? http = null) =>
        Send(http ?? Http, HttpMethod.Post, "/api/factorio/installations", raw, tweak);
    public Task<ApiResp> Add(string? path, string? label = null, Action<HttpRequestMessage>? tweak = null) =>
        PostRaw(label is null ? JsonSerializer.Serialize(new { path }) : JsonSerializer.Serialize(new { path, label }), tweak);
    public Task<ApiResp> Remove(string id, Action<HttpRequestMessage>? tweak = null, HttpClient? http = null) =>
        Send(http ?? Http, HttpMethod.Delete, "/api/factorio/installations/" + id, tweak: tweak);
    public Task<ApiResp> Rescan(Action<HttpRequestMessage>? tweak = null, HttpClient? http = null) =>
        Send(http ?? Http, HttpMethod.Post, "/api/factorio/installations/rescan", "{}", tweak);

    public async Task<ApiResp> Environment_(string? modName = null)
    {
        var url = "/api/environment";
        if (modName != null) url += "?modId=" + Uri.EscapeDataString(await ModId(modName));
        return await Send(Http, HttpMethod.Get, url);
    }

    public async Task<string> ModId(string name)
    {
        var text = await Http.GetStringAsync("/api/mods");
        return JsonDocument.Parse(text).RootElement.EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    /// <summary>Installations of the response that live in this environment's tools folder.</summary>
    public List<JsonElement> Mine(ApiResp r) => r.Installs.Where(i => FakeFactorio.Under(i.S("path")!, Tools.Path)).ToList();

    /// <summary>True when the machine has a real Factorio (or anything outside the temp tree) that would distort exact expectations.</summary>
    public async Task<bool> HasForeign()
    {
        var r = await Rescan();
        return r.Installs.Any(i => !FakeFactorio.Under(i.S("path")!, Tools.Path));
    }

    // ---- jobs
    public async Task<ApiResp> StartJobRaw(string modName, string raw) =>
        await Send(Http, HttpMethod.Post, $"/api/mods/{await ModId(modName)}/jobs", raw);

    public async Task<ApiResp> RunJob(string modName, object body)
    {
        var r = await StartJobRaw(modName, JsonSerializer.Serialize(body));
        if (r.Status != HttpStatusCode.Accepted) throw new InvalidOperationException($"job start expected 202, got {(int)r.Status}: {r.Raw}");
        return await WaitTerminal(r.Str("id")!);
    }

    public async Task<ApiResp> WaitTerminal(string jobId, int seconds = 40)
    {
        var sw = Stopwatch.StartNew();
        ApiResp last;
        do
        {
            last = await Send(Http, HttpMethod.Get, $"/api/jobs/{jobId}");
            if (last.Status == HttpStatusCode.OK && last.Str("state") is "succeeded" or "failed" or "cancelled") return last;
            await Task.Delay(100);
        } while (sw.Elapsed < TimeSpan.FromSeconds(seconds));
        throw new InvalidOperationException("Timeout waiting for job: " + last.Raw);
    }

    public async Task<int> JobCount()
    {
        var r = await Send(Http, HttpMethod.Get, "/api/jobs");
        return r.Json.GetArrayLength();
    }

    public void Dispose()
    {
        try { Http?.Dispose(); } catch { }
        try { Ui?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        Project.Dispose(); Workspace.Dispose(); Tools.Dispose();
    }
}
