using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;
using static FactorioModStudio.Tests.ApiEnv;

namespace FactorioModStudio.Tests;

/// <summary>
/// Black-box tests of the Factorio installations API (contract section 2 of versions-contract.md): list/add/remove/rescan,
/// version resolution in /api/environment, automatic and manual installation choice for jobs, templates and new-mod versions.
/// Fake installs are shell scripts that record every start in calls.txt. Skipped on Windows.
/// FACTORIO_PATH is never set here (process-global; other classes clear it), so the "env" source is covered in the Core tests only.
/// </summary>
public class FactorioInstallationsApiTests
{
    private static void NoFailures(List<string> failures) => Assert.True(failures.Count == 0, string.Join("\n  ", failures));
    private static string Id(string exe) => FactorioInstallations.IdOf(FakeFactorio.Norm(exe));
    private static JsonElement Entry(ApiResp r, string exe) => r.Installs.Single(i => i.S("path") == FakeFactorio.Norm(exe));
    private static bool Has(ApiResp r, string exe) => r.Installs.Any(i => i.S("path") == FakeFactorio.Norm(exe));

    // ------------------------------------------------------------------ GET

    [Fact]
    public async Task Get_Shape_WithAnEmptyProject()
    {
        if (Unsupported) return;
        using var env = await Create();
        var r = await env.List();
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Contains("json", r.Header("Content-Type") ?? "");
        Assert.Equal(JsonValueKind.Array, r.Json.GetProperty("installations").ValueKind);
        Assert.Equal(JsonValueKind.Array, r.Json.GetProperty("invalid").ValueKind);
        Assert.Empty(env.Mine(r));
        Assert.Empty(r.Invalid);
    }

    [Fact]
    public async Task Get_ForeignHost_IsRejected()
    {
        if (Unsupported) return;
        using var env = await Create();
        Assert.Equal(HttpStatusCode.Forbidden, (await env.List(q => q.Headers.Host = "attacker.example")).Status);
    }

    [Fact]
    public async Task Get_EntryShape_ForARegisteredInstall()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exe = env.Install("fx", "2.0.76");
        Assert.Equal(HttpStatusCode.Created, (await env.Add(exe, "My label")).Status);
        var e = Entry(await env.List(), exe);
        foreach (var key in new[] { "id", "path", "version", "label", "sources", "valid", "removable" })
            Assert.True(e.TryGetProperty(key, out _), "missing property " + key + ": " + e);
        Assert.Equal(Id(exe), e.S("id"));
        Assert.Equal("2.0.76", e.S("version"));
        Assert.Equal("My label", e.S("label"));
        Assert.Contains("ui", e.Strs("sources"));
        Assert.True(e.GetProperty("valid").GetBoolean());
        Assert.True(e.GetProperty("removable").GetBoolean());
    }

    [Fact]
    public async Task Get_InstallWithoutInfoJson_HasNullVersion()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exe = env.Install("nover", null, info: false);
        Assert.Equal(HttpStatusCode.Created, (await env.Add(exe)).Status);
        var e = Entry(await env.List(), exe);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("label").ValueKind);
    }

    [Fact]
    public async Task Get_ConfigPath_IsACandidate_NotRemovable_AndCannotBeDeleted()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var cfg = env.Install("cfg", "2.0.76");
        AddMod(env.Workspace, "alpha");
        await env.StartServer(cfg);
        var e = Entry(await env.List(), cfg);
        Assert.Equal(new[] { "config" }, e.Strs("sources"));
        Assert.False(e.GetProperty("removable").GetBoolean());
        Assert.True(e.GetProperty("valid").GetBoolean());
        var d = await env.Remove(Id(cfg));
        Assert.Equal(HttpStatusCode.NotFound, d.Status);
        Assert.True(Has(await env.List(), cfg));
    }

    [Fact]
    public async Task Get_SiblingDiscovery_FindsFactorioNamedNeighbours_AsAutoAndNotRemovable()
    {
        if (Unsupported) return;
        using var env = await Create();
        var anchor = env.Install("base-install", "2.0.76");
        var s1 = env.Install("Factorio-1.1.110", "1.1.110");
        var s2 = env.Install("factorio_2.1.3", "2.1.3");
        var s3 = env.Install("Factorio 1.0.0", "1.0.0");
        var other = env.Install("my-game-1.1", "1.1.5");        // name does not start with factorio
        Assert.Equal(HttpStatusCode.Created, (await env.Add(anchor)).Status);
        var r = await env.List();
        foreach (var s in new[] { s1, s2, s3 })
        {
            var e = Entry(r, s);
            Assert.Equal(new[] { "auto" }, e.Strs("sources"));
            Assert.False(e.GetProperty("removable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, e.GetProperty("label").ValueKind);
            Assert.Equal(HttpStatusCode.NotFound, (await env.Remove(Id(s))).Status);
        }
        Assert.False(Has(r, other), "non-matching sibling name must not be discovered");
        Assert.Equal("1.1.110", Entry(r, s1).S("version"));
    }

    [Fact]
    public async Task Get_RegisteredAndConfigured_MergeSources_UiFirst()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var exe = env.Install("both", "2.0.76");
        AddMod(env.Workspace, "alpha");
        await env.StartServer(exe);
        Assert.Equal(HttpStatusCode.Created, (await env.Add(exe, "L")).Status);
        var e = Entry(await env.List(), exe);
        Assert.Equal(new[] { "ui", "config" }, e.Strs("sources"));
        Assert.True(e.GetProperty("removable").GetBoolean());
        // removing the registration keeps the config candidate
        var d = await env.Remove(Id(exe));
        Assert.Equal(HttpStatusCode.OK, d.Status);
        var after = Entry(d, exe);
        Assert.Equal(new[] { "config" }, after.Strs("sources"));
        Assert.False(after.GetProperty("removable").GetBoolean());
    }

    // ------------------------------------------------------------------ POST: add

    [Fact]
    public async Task Post_Executable_Returns201_WithListBody_StoresResolvedPath()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exe = env.Install("fx1", "2.0.76");
        var weird = Path.Combine(Path.GetDirectoryName(exe)!, ".", "sub", "..", "factorio");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "sub"));
        var r = await env.Add(weird, "primary");
        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Contains("json", r.Header("Content-Type") ?? "");
        var e = Entry(r, exe);
        Assert.Equal("primary", e.S("label"));
        Assert.Equal(Id(exe), e.S("id"));
        Assert.Equal(Id(exe), FactorioInstallations.IdOf(e.S("path")!));
        Assert.True(File.Exists(env.SettingsFile));
        var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        var arr = doc.GetProperty("factorioInstallations").EnumerateArray().ToList();
        var stored = Assert.Single(arr);
        Assert.Equal(FakeFactorio.Norm(exe), stored.GetProperty("path").GetString());
        Assert.Equal("primary", stored.GetProperty("label").GetString());
        Assert.False(doc.TryGetProperty("factorioPath", out var lp) && lp.ValueKind == JsonValueKind.String, "legacy factorioPath must not be written");
        Assert.False(File.Exists(env.Project.Combine("fmstudio.json")));
        // GET returns the same list
        Assert.Equal(r.Installs.Select(i => i.S("id")).OrderBy(x => x).ToList(), (await env.List()).Installs.Select(i => i.S("id")).OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Post_InstallationDirectory_StoresResolvedExecutable()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exe = env.Install("fx dir ünï", "2.0.76");
        var install = FakeFactorio.RootOf(exe);
        foreach (var variant in new[] { install, install + "/" })
        {
            var r = await env.Add(variant);
            Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, r.Raw);
            Assert.True(Has(r, exe));
            var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
            Assert.Equal(FakeFactorio.Norm(exe), doc.GetProperty("factorioInstallations")[0].GetProperty("path").GetString());
        }
    }

    [Fact]
    public async Task Post_MacOsLikeInstallDir_IsResolved_AndVersionDetected()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exe = FakeFactorio.MakeMac(env.Tools.Path, "mac", "2.0.76");
        var r = await env.Add(Path.Combine(env.Tools.Path, "mac"));
        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal("2.0.76", Entry(r, exe).S("version"));
    }

    [Fact]
    public async Task Post_CaseInsensitiveFileName_AndExeExtension_AreAccepted()
    {
        if (Unsupported) return;
        using var env = await Create();
        var failures = new List<string>();
        foreach (var name in new[] { "Factorio", "factorio.exe", "FACTORIO.EXE" })
        {
            var p = env.Script("case-" + name, name);
            var r = await env.Add(p);
            if (r.Status != HttpStatusCode.Created) failures.Add($"{name}: expected 201, got {r.Code} {r.Raw}");
        }
        NoFailures(failures);
    }

    [Fact]
    public async Task Post_RejectsInvalidPaths_With400_AndKeepsPreviousSettings()
    {
        if (Unsupported) return;
        using var env = await Create();
        var good = env.Install("good", "2.0.76");
        Assert.Equal(HttpStatusCode.Created, (await env.Add(good)).Status);
        var before = File.ReadAllText(env.SettingsFile);

        var emptyDir = env.Tools.Combine("emptydir"); Directory.CreateDirectory(emptyDir);
        var noBin = env.Tools.Combine("nobin"); Directory.CreateDirectory(Path.Combine(noBin, "bin", "x64"));
        var dirNamedFactorio = env.Tools.Combine("dnf", "factorio"); Directory.CreateDirectory(dirNamedFactorio);
        var realTarget = env.Install("symtarget", "2.0.1");
        var link = env.Tools.Combine("lnk", "factorio"); Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, realTarget);
        var cases = new Dictionary<string, string>
        {
            ["relative name"] = "factorio",
            ["relative bin path"] = "bin/x64/factorio",
            ["relative traversal"] = "../../" + Path.GetFileName(env.Tools.Path) + "/good/bin/x64/factorio",
            ["tilde"] = "~/factorio",
            ["nonexistent"] = env.Tools.Combine("nope", "bin", "x64", "factorio"),
            ["nonexistent dir"] = env.Tools.Combine("nope"),
            ["empty dir"] = emptyDir,
            ["dir with bin/x64 but no exe"] = noBin,
            ["directory named factorio"] = dirNamedFactorio,
            ["/bin/sh"] = "/bin/sh",
            ["/bin"] = "/bin",
            ["root"] = "/",
            ["evil.sh"] = env.Script("evil", "evil.sh"),
            ["factorio.txt"] = env.Script("ftxt", "factorio.txt"),
            ["notfactorio"] = env.Script("nf", "notfactorio"),
            ["factorio-bak"] = env.Script("fb", "factorio-bak"),
            ["factorio.sh"] = env.Script("fsh", "factorio.sh"),
            ["factorio.exe.sh"] = env.Script("fes", "factorio.exe.sh"),
            ["trailing dot name"] = env.Script("fdot", "factorio."),
            ["symlink named factorio"] = link,
            ["NUL"] = good + "\0",
            ["NUL middle"] = good.Insert(good.Length - 3, "\0"),
            ["control 0x01"] = "\u0001" + good,
            ["tab"] = good.Replace("good", "go\tod"),
            ["empty"] = "",
            ["whitespace only"] = "   ",
        };
        var failures = new List<string>();
        foreach (var (label, value) in cases)
        {
            var r = await env.Add(value);
            if (r.Status != HttpStatusCode.BadRequest) { failures.Add($"{label}: expected 400, got {r.Code} {r.Raw}"); continue; }
            if (string.IsNullOrWhiteSpace(r.Error)) failures.Add($"{label}: 400 without error message: {r.Raw}");
        }
        // "trailing newline" is ambiguous (trim or reject) but never a server error and never a second registration
        var nl = await env.Add(good + "\n");
        if (nl.Code >= 500) failures.Add("trailing newline: " + nl.Code);
        var after = await env.List();
        if (env.Mine(after).Count(i => i.GetProperty("removable").GetBoolean()) != 1) failures.Add("registered set changed by rejected POSTs: " + after.Raw);
        NoFailures(failures);
        var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        Assert.Equal(1, doc.GetProperty("factorioInstallations").GetArrayLength());
        Assert.Equal(before, File.ReadAllText(env.SettingsFile));
    }

    [Fact]
    public async Task Post_Rejected_DoesNotCreateSettingsFile()
    {
        if (Unsupported) return;
        using var env = await Create();
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Add(env.Script("evil", "evil.sh"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Add("/bin/sh")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.PostRaw("{\"path\":5}")).Status);
        Assert.False(File.Exists(env.SettingsFile), "rejected POST must not persist anything");
    }

    [Fact]
    public async Task Post_NeverExecutesTheFile_NorDoListOrRescan()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exe = env.Install("noexec", "2.0.76");
        var other = env.Install("Factorio-1.1.110", "1.1.110");
        Assert.Equal(HttpStatusCode.Created, (await env.Add(exe, "x")).Status);
        await env.List();
        await env.Rescan();
        await env.Environment_("alpha");
        await env.Environment_();
        Assert.False(FakeFactorio.WasStarted(exe), "registering/listing must not run the executable");
        Assert.False(FakeFactorio.WasStarted(other), "discovery must not run executables");
    }

    [Fact]
    public async Task Post_PathLength_1024Accepted_1025Rejected()
    {
        if (Unsupported) return;
        using var env = await Create();
        string Make(int total)
        {
            var root = env.Tools.Path;
            var remaining = total - root.Length - "/factorio".Length;
            var sb = root;
            while (remaining > 0)
            {
                var comp = Math.Min(200, remaining - 1);
                if (remaining - 1 - comp is > 0 and < 2) comp -= 2;
                sb += "/" + new string('d', comp);
                remaining -= comp + 1;
            }
            Directory.CreateDirectory(sb);
            var exe = sb + "/factorio";
            FakeFactorio.WriteScript(exe, "#!/bin/sh\n");
            return exe;
        }
        var p1024 = Make(1024);
        Assert.Equal(1024, p1024.Length);
        Assert.Equal(HttpStatusCode.Created, (await env.Add(p1024)).Status);
        var p1025 = Make(1025);
        Assert.Equal(1025, p1025.Length);
        var bad = await env.Add(p1025);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.False(string.IsNullOrWhiteSpace(bad.Error));
    }

    [Fact]
    public async Task Post_BadBodiesAndTypes_Are400_NotCrashes()
    {
        if (Unsupported) return;
        using var env = await Create();
        var good = env.Install("good", "2.0.76");
        Assert.Equal(HttpStatusCode.Created, (await env.Add(good)).Status);
        var failures = new List<string>();
        var g = good.Replace("\\", "\\\\");
        var bodies = new[]
        {
            "", "{", "not json", "[]", "\"str\"", "42", "null", "true", "{}",
            "{\"path\":5}", "{\"path\":true}", "{\"path\":null}", "{\"path\":\"\"}", "{\"path\":{}}", "{\"path\":[]}", "{\"path\":[\"" + g + "\"]}",
            "{\"path\":1.5}", "{\"path\":\"" + g + "\"", "{\"label\":\"x\"}",
            "{\"path\":\"" + g + "\",\"label\":5}", "{\"path\":\"" + g + "\",\"label\":[]}", "{\"path\":\"" + g + "\",\"label\":{}}", "{\"path\":\"" + g + "\",\"label\":true}",
            "ï»¿{garbage",
        };
        foreach (var b in bodies)
        {
            var r = await env.PostRaw(b);
            if (r.Status != HttpStatusCode.BadRequest) failures.Add($"body '{b[..Math.Min(b.Length, 50)]}': expected 400, got {r.Code} {r.Raw}");
        }
        var list = await env.List();
        if (env.Mine(list).Count != 1) failures.Add("state changed by bad bodies: " + list.Raw);
        if ((await env.Environment_()).Status != HttpStatusCode.OK) failures.Add("environment endpoint broken afterwards");
        NoFailures(failures);
    }

    [Fact]
    public async Task Post_NonJsonContentType_IsNotAServerError()
    {
        if (Unsupported) return;
        using var env = await Create();
        var good = env.Install("good", "2.0.76");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/factorio/installations") { Content = new StringContent("path=" + Uri.EscapeDataString(good), Encoding.UTF8, "application/x-www-form-urlencoded") };
        using var res = await env.Http.SendAsync(req);
        Assert.True((int)res.StatusCode is >= 400 and < 500, "got " + res.StatusCode);
        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/factorio/installations");
        using var res2 = await env.Http.SendAsync(req2);
        Assert.True((int)res2.StatusCode is >= 400 and < 500, "got " + res2.StatusCode);
        Assert.False(File.Exists(env.SettingsFile));
    }

    [Fact]
    public async Task Post_BodyOver4KB_Is413_AndChangesNothing()
    {
        if (Unsupported) return;
        using var env = await Create();
        var good = env.Install("good", "2.0.76");
        var big = JsonSerializer.Serialize(new { path = good, pad = new string('x', 5000) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await env.PostRaw(big)).Status);
        Assert.False(File.Exists(env.SettingsFile));
        var huge = JsonSerializer.Serialize(new { path = new string('a', 100_000) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await env.PostRaw(huge)).Status);
        var bigLabel = JsonSerializer.Serialize(new { path = good, label = new string('l', 5000) });
        Assert.True((await env.PostRaw(bigLabel)).Status is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest);
        Assert.Empty(env.Mine(await env.List()));
    }

    [Fact]
    public async Task Mutations_RequireToken_SameOrigin_AndLocalHost()
    {
        if (Unsupported) return;
        using var env = await Create();
        var good = env.Install("good", "2.0.76");
        Assert.Equal(HttpStatusCode.Created, (await env.Add(good)).Status);
        var second = env.Install("second", "1.1.110");
        var body = JsonSerializer.Serialize(new { path = second });
        var id = Id(good);
        using var anonymous = Client(env.Ui, token: false);
        using var wrong = new HttpClient { BaseAddress = new Uri(env.Ui.Url) };
        wrong.DefaultRequestHeaders.Add("X-FMS-Token", "nope");

        var failures = new List<string>();
        void Expect403(string what, ApiResp r) { if (r.Status != HttpStatusCode.Forbidden) failures.Add($"{what}: expected 403, got {r.Code} {r.Raw}"); }
        foreach (var (name, http) in new[] { ("no token", anonymous), ("wrong token", wrong) })
        {
            Expect403(name + " POST", await env.PostRaw(body, http: http));
            Expect403(name + " DELETE", await env.Remove(id, http: http));
            Expect403(name + " rescan", await env.Rescan(http: http));
        }
        Action<HttpRequestMessage> evil = q => q.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        Action<HttpRequestMessage> host = q => q.Headers.Host = "attacker.example";
        Expect403("foreign origin POST", await env.PostRaw(body, evil));
        Expect403("foreign origin DELETE", await env.Remove(id, evil));
        Expect403("foreign origin rescan", await env.Rescan(evil));
        Expect403("foreign host POST", await env.PostRaw(body, host));
        Expect403("foreign host DELETE", await env.Remove(id, host));
        Expect403("foreign host rescan", await env.Rescan(host));
        NoFailures(failures);

        var list = await env.List();
        Assert.False(Has(list, second), "forbidden POST must not persist");
        Assert.True(Has(list, good), "forbidden DELETE must not remove");

        Action<HttpRequestMessage> origin = q => q.Headers.TryAddWithoutValidation("Origin", env.Ui.Url.TrimEnd('/'));
        Assert.Equal(HttpStatusCode.Created, (await env.PostRaw(body, origin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await env.Rescan(origin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await env.Remove(Id(second), origin)).Status);
    }

    // ------------------------------------------------------------------ labels

    [Fact]
    public async Task Label_Rules()
    {
        if (Unsupported) return;
        using var env = await Create();
        var failures = new List<string>();
        var n = 0;
        async Task<ApiResp> Try(string? label, bool raw = false)
        {
            var exe = env.Install("lab" + n++, "2.0.76");
            return raw ? await env.PostRaw(JsonSerializer.Serialize(new { path = exe, label })) : await env.Add(exe, label);
        }

        foreach (var ok in new[] { "x", "Steam 2.0", new string('a', 80), "Тест 日本語 ü", "<b>bold</b> & \"quotes\"", "with  double  spaces" })
        {
            var r = await Try(ok, raw: true);
            if (r.Status != HttpStatusCode.Created) { failures.Add($"label '{ok[..Math.Min(20, ok.Length)]}' ({ok.Length}): expected 201, got {r.Code} {r.Raw}"); continue; }
            if (!r.Installs.Any(i => i.S("label") == ok)) failures.Add($"label '{ok}' not stored verbatim");
        }
        foreach (var bad in new[] { new string('a', 81), new string('a', 500), "bell\u0007", "new\nline", "tab\there", "nul\0", "\u007fdel", "\u001bescape", "c\rr" })
        {
            var r = await Try(bad, raw: true);
            if (r.Status != HttpStatusCode.BadRequest) failures.Add($"label (len {bad.Length}, '{bad.Replace("\n", "\\n")[..Math.Min(8, bad.Length)]}') expected 400, got {r.Code}");
        }
        // null / absent label are fine
        var nullLabel = await Try(null, raw: true);
        if (nullLabel.Status != HttpStatusCode.Created) failures.Add("null label: " + nullLabel.Code + " " + nullLabel.Raw);
        var absent = await env.Add(env.Install("absent", "2.0.76"));
        if (absent.Status != HttpStatusCode.Created) failures.Add("absent label: " + absent.Code);
        // empty / whitespace label: either stored as null or rejected, never a server error
        foreach (var odd in new[] { "", "   " })
        {
            var r = await Try(odd, raw: true);
            if (r.Code >= 500) failures.Add($"label '{odd}': {r.Code}");
        }
        NoFailures(failures);
        // the stored file never contains a rejected label
        var text = File.ReadAllText(env.SettingsFile);
        Assert.DoesNotContain(new string('a', 81), text);
    }

    [Fact]
    public async Task Duplicate_SamePath_Returns200_AndUpdatesLabel_NoSecondEntry()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exe = env.Install("dup", "2.0.76");
        var first = await env.Add(exe, "first");
        Assert.Equal(HttpStatusCode.Created, first.Status);
        foreach (var spelling in new[] { exe, FakeFactorio.RootOf(exe), Path.Combine(Path.GetDirectoryName(exe)!, "..", "x64", "factorio") })
        {
            var r = await env.Add(spelling, "label via " + spelling.Length);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            var e = Entry(r, exe);
            Assert.Equal("label via " + spelling.Length, e.S("label"));
            Assert.Equal(1, r.Installs.Count(i => i.S("path") == FakeFactorio.Norm(exe)));
        }
        var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        Assert.Equal(1, doc.GetProperty("factorioInstallations").GetArrayLength());
    }

    [Fact]
    public async Task Post_Max50Registrations()
    {
        if (Unsupported) return;
        using var env = await Create();
        var failures = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var r = await env.Add(env.Install($"bulk{i:D2}", "2.0." + i));
            if (r.Status != HttpStatusCode.Created) { failures.Add($"#{i}: {r.Code} {r.Raw}"); break; }
        }
        NoFailures(failures);
        var over = await env.Add(env.Install("bulk-over", "2.0.99"));
        Assert.True(over.Code is >= 400 and < 500, $"51st registration: {over.Code} {over.Raw}");
        Assert.False(string.IsNullOrWhiteSpace(over.Error));
        var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        Assert.Equal(50, doc.GetProperty("factorioInstallations").GetArrayLength());
        // updating an existing entry at the limit still works
        var again = await env.Add(Path.Combine(env.Tools.Path, "bulk00", "bin", "x64", "factorio"), "renamed");
        Assert.Equal(HttpStatusCode.OK, again.Status);
    }

    // ------------------------------------------------------------------ DELETE

    [Fact]
    public async Task Delete_RemovesRegistered_Returns200WithList_AndUpdatesFile()
    {
        if (Unsupported) return;
        using var env = await Create();
        var a = env.Install("a", "2.0.76");
        var b = env.Install("b", "1.1.110");
        await env.Add(a, "A"); await env.Add(b, "B");
        var d = await env.Remove(Id(a));
        Assert.Equal(HttpStatusCode.OK, d.Status);
        Assert.False(Has(d, a));
        Assert.True(Has(d, b));
        Assert.False(Has(await env.List(), a));
        var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        var arr = doc.GetProperty("factorioInstallations").EnumerateArray().ToList();
        Assert.Equal(FakeFactorio.Norm(b), Assert.Single(arr).GetProperty("path").GetString());
        // second delete -> 404
        Assert.Equal(HttpStatusCode.NotFound, (await env.Remove(Id(a))).Status);
    }

    [Fact]
    public async Task Delete_UnknownOrWeirdIds_Are404_Never5xx()
    {
        if (Unsupported) return;
        using var env = await Create();
        await env.Add(env.Install("a", "2.0.76"));
        var failures = new List<string>();
        foreach (var id in new[] { "000000000000", "nope", "..", "%2e%2e", "a%2fb", "%00", new string('a', 2000), "rescan", "ABCDEF123456" })
        {
            var r = await env.Remove(id);
            if (r.Status != HttpStatusCode.NotFound && r.Status != HttpStatusCode.BadRequest && r.Status != HttpStatusCode.MethodNotAllowed)
                failures.Add($"{id[..Math.Min(20, id.Length)]}: {r.Code} {r.Raw}");
            if (r.Code >= 500) failures.Add($"5xx for {id}");
        }
        var empty = await Send(env.Http, HttpMethod.Delete, "/api/factorio/installations/");
        if (empty.Code >= 500) failures.Add("empty id: " + empty.Code);
        var root = await Send(env.Http, HttpMethod.Delete, "/api/factorio/installations");
        if (root.Code >= 500) failures.Add("collection delete: " + root.Code);
        NoFailures(failures);
        Assert.Equal(1, env.Mine(await env.List()).Count);
    }

    [Fact]
    public async Task Delete_IsCaseInsensitiveOnId_OrAtLeastConsistent()
    {
        if (Unsupported) return;
        using var env = await Create();
        var a = env.Install("a", "2.0.76");
        await env.Add(a);
        var r = await env.Remove(Id(a).ToUpperInvariant());
        Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.NotFound, r.Raw);
        if (r.Status == HttpStatusCode.NotFound) Assert.Equal(HttpStatusCode.OK, (await env.Remove(Id(a))).Status);
    }

    // ------------------------------------------------------------------ invalid / vanished entries

    [Fact]
    public async Task VanishedRegisteredExecutable_IsListedUnderInvalid_AndRemovable()
    {
        if (Unsupported) return;
        using var env = await Create();
        var keep = env.Install("keep", "2.0.76");
        var gone = env.Install("gone", "1.1.110");
        await env.Add(keep, "K"); await env.Add(gone, "Will vanish");
        File.Delete(gone);
        var r = await env.Rescan();
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(Has(r, keep));
        Assert.False(Has(r, gone), "a vanished executable must not stay in installations");
        var inv = Assert.Single(r.Invalid);
        Assert.Equal(FakeFactorio.Norm(gone), inv.S("path"));
        Assert.Equal("Will vanish", inv.S("label"));
        Assert.False(string.IsNullOrWhiteSpace(inv.S("error")));

        // it can be removed: by the id of its stored path (or by an explicit id if the server provides one)
        var id = inv.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString()! : FactorioInstallations.IdOf(inv.S("path")!);
        var d = await env.Remove(id);
        Assert.Equal(HttpStatusCode.OK, d.Status);
        Assert.Empty(d.Invalid);
        Assert.True(Has(d, keep));
        var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        Assert.Equal(1, doc.GetProperty("factorioInstallations").GetArrayLength());
    }

    [Fact]
    public async Task HandEditedSettings_WithEvilOrBrokenEntries_NeverBecomeCandidates()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var good = env.Install("good", "2.0.76");
        var evil = env.Script("evil", "evil.sh");
        var notFactorio = env.Script("nf", "factorio-bak");
        var target = env.Install("symtarget", "2.0.1");
        var link = env.Tools.Combine("lnk", "factorio"); Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);
        Directory.CreateDirectory(env.SettingsDir);
        File.WriteAllText(env.SettingsFile, JsonSerializer.Serialize(new
        {
            factorioInstallations = new object?[]
            {
                new { path = good, label = "good" },
                new { path = evil, label = "evil" },
                new { path = notFactorio },
                new { path = link },
                new { path = "relative/factorio" },
                new { path = "/bin/sh" },
                new { path = env.Tools.Combine("missing", "factorio") },
                null, 5, "str", new { label = "no path" }, new { path = 7 }, new { path = "" },
            }
        }));
        AddMod(env.Workspace, "alpha");
        await env.StartServer(null);
        var r = await env.List();
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(Has(r, good));
        foreach (var bad in new[] { evil, notFactorio, link })
            Assert.False(Has(r, bad), "must not be a candidate: " + bad);
        Assert.False(r.Installs.Any(i => i.S("path") == "/bin/sh" || i.S("path") == "relative/factorio"));
        var job = await env.RunJob("alpha", new { kind = "smoke" });
        Assert.Equal("succeeded", job.Str("state"));
        Assert.False(FakeFactorio.WasStarted(evil) || FakeFactorio.WasStarted(notFactorio) || FakeFactorio.WasStarted(target), "a rejected executable was started");
    }

    [Fact]
    public async Task SettingsWithMoreThan50Entries_OrOddTypes_DoNotBreakTheServer()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var entries = Enumerable.Range(0, 60).Select(i => (object)new { path = env.Install($"m{i:D2}", "2.0." + i), label = "n" + i }).ToList();
        Directory.CreateDirectory(env.SettingsDir);
        File.WriteAllText(env.SettingsFile, JsonSerializer.Serialize(new { factorioInstallations = entries }));
        AddMod(env.Workspace, "alpha");
        await env.StartServer(null);
        var r = await env.List();
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(env.Mine(r).Count <= 60);
        Assert.Equal(HttpStatusCode.OK, (await env.Environment_("alpha")).Status);
    }

    // ------------------------------------------------------------------ settings.json robustness / legacy

    [Theory]
    [InlineData("garbage {{{")]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"factorioInstallations\":5}")]
    [InlineData("{\"factorioInstallations\":\"x\"}")]
    [InlineData("{\"factorioInstallations\":{}}")]
    [InlineData("{\"factorioInstallations\":[1,2,null]}")]
    [InlineData("{\"factorioInstallations\":[{\"path\":5}]}")]
    [InlineData("{\"factorioInstallations\":[{\"label\":\"only\"}]}")]
    [InlineData("{\"factorioInstallations\":null}")]
    [InlineData("{\"factorioPath\":5}")]
    [InlineData("{\"factorioPath\":[\"x\"]}")]
    [InlineData("{\"factorioPath\":{}}")]
    [InlineData("{\"factorioPath\":null}")]
    [InlineData("{\"factorioPath\":\"\"}")]
    [InlineData("{\"factorioInstallations\":[")]
    [InlineData("ï»¿{\"factorioInstallations\":[],")]
    public async Task CorruptOrOddSettingsFile_IsTolerated_AndCanBeOverwritten(string content)
    {
        if (Unsupported) return;
        using var env = await Create(seed: e => { Directory.CreateDirectory(e.SettingsDir); File.WriteAllText(e.SettingsFile, content); });
        var g = await env.List();
        Assert.Equal(HttpStatusCode.OK, g.Status);
        Assert.Empty(env.Mine(g));
        Assert.Equal(HttpStatusCode.OK, (await env.Environment_("alpha")).Status);
        var exe = env.Install("fx", "2.0.76");
        var p = await env.Add(exe, "after corrupt");
        Assert.Equal(HttpStatusCode.Created, p.Status);
        var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        Assert.Equal(FakeFactorio.Norm(exe), doc.GetProperty("factorioInstallations")[0].GetProperty("path").GetString());
        Assert.True(Has(await env.List(), exe));
    }

    [Fact]
    public async Task UnknownSettingsFields_ArePreserved_OnAddAndRemove()
    {
        if (Unsupported) return;
        using var env = await Create(seed: e =>
        {
            Directory.CreateDirectory(e.SettingsDir);
            File.WriteAllText(e.SettingsFile, "{\"theme\":\"dark\",\"nested\":{\"x\":[1,2,3],\"y\":null},\"n\":7}");
        });
        var exe = env.Install("fx", "2.0.76");
        Assert.Equal(HttpStatusCode.Created, (await env.Add(exe)).Status);
        void Check(int count)
        {
            var d = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
            Assert.Equal("dark", d.GetProperty("theme").GetString());
            Assert.Equal(7, d.GetProperty("n").GetInt32());
            Assert.Equal(3, d.GetProperty("nested").GetProperty("x").GetArrayLength());
            var n = d.TryGetProperty("factorioInstallations", out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
            Assert.Equal(count, n);
        }
        Check(1);
        Assert.Equal(HttpStatusCode.OK, (await env.Remove(Id(exe))).Status);
        Check(0);
        Assert.Equal(HttpStatusCode.Created, (await env.Add(exe)).Status);
        Check(1);
    }

    [Fact]
    public async Task LegacyFactorioPath_IsMigratedOnRead_AndRemovedOnNextWrite()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var legacy = env.Install("legacy-install", "1.1.110");
        var fresh = env.Install("fresh-install", "2.0.76");
        Directory.CreateDirectory(env.SettingsDir);
        var original = JsonSerializer.Serialize(new { factorioPath = legacy, theme = "dark" });
        File.WriteAllText(env.SettingsFile, original);
        AddMod(env.Workspace, "alpha");
        await env.StartServer(null);

        var r = await env.List();
        var e = Entry(r, legacy);
        Assert.Contains("ui", e.Strs("sources"));
        Assert.True(e.GetProperty("removable").GetBoolean());
        Assert.Equal("1.1.110", e.S("version"));
        Assert.Equal(original, File.ReadAllText(env.SettingsFile));   // migration is in memory until the next write

        // next write: both entries are persisted, the legacy key is gone, unknown fields stay
        Assert.Equal(HttpStatusCode.Created, (await env.Add(fresh)).Status);
        var d = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        Assert.False(d.TryGetProperty("factorioPath", out var lp) && lp.ValueKind != JsonValueKind.Null, "legacy factorioPath must be dropped: " + d);
        Assert.Equal("dark", d.GetProperty("theme").GetString());
        var paths = d.GetProperty("factorioInstallations").EnumerateArray().Select(x => x.GetProperty("path").GetString()).ToList();
        Assert.Contains(FakeFactorio.Norm(legacy), paths);
        Assert.Contains(FakeFactorio.Norm(fresh), paths);
        Assert.Equal(2, paths.Count);

        // removing the migrated entry does not resurrect it
        Assert.Equal(HttpStatusCode.OK, (await env.Remove(Id(legacy))).Status);
        await env.Restart();
        Assert.False(Has(await env.List(), legacy));
        Assert.True(Has(await env.List(), fresh));
    }

    [Fact]
    public async Task LegacyFactorioPath_RemovedByDelete_EvenWithoutOtherWrites()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var legacy = env.Install("legacy-install", "2.0.76");
        Directory.CreateDirectory(env.SettingsDir);
        File.WriteAllText(env.SettingsFile, JsonSerializer.Serialize(new { factorioPath = legacy }));
        AddMod(env.Workspace, "alpha");
        await env.StartServer(null);
        Assert.True(Has(await env.List(), legacy));
        Assert.Equal(HttpStatusCode.OK, (await env.Remove(Id(legacy))).Status);
        await env.Restart();
        Assert.False(Has(await env.List(), legacy), "deleted legacy entry came back after a restart");
    }

    [Fact]
    public async Task LegacyFactorioPath_ThatNoLongerExists_IsListedAsInvalid()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        Directory.CreateDirectory(env.SettingsDir);
        var ghost = env.Tools.Combine("ghost", "bin", "x64", "factorio");
        File.WriteAllText(env.SettingsFile, JsonSerializer.Serialize(new { factorioPath = ghost }));
        AddMod(env.Workspace, "alpha");
        await env.StartServer(null);
        var r = await env.List();
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Contains(r.Invalid, i => i.S("path") != null && i.S("path")!.Contains("ghost"));
    }

    [Fact]
    public async Task Settings_WriteIsAtomic_NoTempFilesLeft_AndConcurrentAddsAreNotLost()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exes = Enumerable.Range(0, 12).Select(i => env.Install($"c{i:D2}", "2.0." + i)).ToList();
        var results = await Task.WhenAll(exes.Select(e => env.Add(e, "L" + Path.GetFileName(FakeFactorio.RootOf(e)))));
        foreach (var r in results)
            Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.Locked, $"{r.Code} {r.Raw}");
        var doc = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        Assert.Equal(JsonValueKind.Object, doc.ValueKind);
        var stored = doc.GetProperty("factorioInstallations").EnumerateArray().Select(x => x.GetProperty("path").GetString()).ToHashSet();
        for (var i = 0; i < exes.Count; i++)
            if (results[i].Status is HttpStatusCode.Created or HttpStatusCode.OK)
                Assert.True(stored.Contains(FakeFactorio.Norm(exes[i])), "acknowledged registration was lost (concurrent writes): " + exes[i]);
        var leftovers = Directory.GetFileSystemEntries(env.SettingsDir).Select(Path.GetFileName).Where(n => n != "settings.json" && n != "profiles").ToList();
        Assert.True(leftovers.Count == 0, "leftover files: " + string.Join(",", leftovers!));
    }

    [Fact]
    public async Task ConcurrentAddAndRemove_LeaveValidJson()
    {
        if (Unsupported) return;
        using var env = await Create();
        var a = env.Install("a", "2.0.76");
        var b = env.Install("b", "1.1.110");
        await env.Add(a);
        var tasks = new List<Task<ApiResp>>();
        for (var i = 0; i < 20; i++) tasks.Add(i % 2 == 0 ? env.Add(b) : env.Remove(Id(b)));
        tasks.Add(env.Rescan());
        var rs = await Task.WhenAll(tasks);
        Assert.All(rs, r => Assert.True(r.Code < 500, $"{r.Code} {r.Raw}"));
        JsonDocument.Parse(File.ReadAllText(env.SettingsFile));
        Assert.True(Has(await env.List(), a));
    }

    [Fact]
    public async Task Registrations_SurviveServerRestart()
    {
        if (Unsupported) return;
        using var env = await Create();
        var exe = env.Install("fx", "2.0.76");
        await env.Add(exe, "persisted");
        await env.Restart();
        var e = Entry(await env.List(), exe);
        Assert.Equal("persisted", e.S("label"));
        Assert.True(e.GetProperty("removable").GetBoolean());
    }

    [Fact]
    public async Task Settings_File_ContainsOnlyPathAndLabel_PerEntry()
    {
        if (Unsupported) return;
        using var env = await Create();
        await env.Add(env.Install("fx", "2.0.76"), "lbl");
        var props = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "factorioInstallations" }, props);
        var entry = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement.GetProperty("factorioInstallations")[0];
        Assert.Equal(new[] { "label", "path" }, entry.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task RemovedSettingsEndpoints_AreGone()
    {
        if (Unsupported) return;
        using var env = await Create();
        var good = env.Install("good", "2.0.76");
        foreach (var m in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            var r = await Send(env.Http, m, "/api/settings/factorio", m == HttpMethod.Put ? JsonSerializer.Serialize(new { path = good }) : null);
            Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{m}: {r.Code} {r.Raw}");
        }
        Assert.False(File.Exists(env.SettingsFile));
    }

    // ------------------------------------------------------------------ rescan / cache

    [Fact]
    public async Task Rescan_Returns200WithListBody_AndPicksUpNewSiblings()
    {
        if (Unsupported) return;
        using var env = await Create();
        var anchor = env.Install("base-install", "2.0.76");
        await env.Add(anchor);
        await env.List();
        var late = env.Install("Factorio-1.1.110", "1.1.110");     // appears after the list was computed
        var r = await env.Rescan();
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Contains("json", r.Header("Content-Type") ?? "");
        var e = Entry(r, late);
        Assert.Equal(new[] { "auto" }, e.Strs("sources"));
        // vanishing siblings disappear on rescan too
        File.Delete(late);
        var again = await env.Rescan();
        Assert.False(Has(again, late));
        Assert.True(Has(again, anchor));
    }

    [Fact]
    public async Task Rescan_WithGetOrNoBody_IsHandled()
    {
        if (Unsupported) return;
        using var env = await Create();
        var g = await Send(env.Http, HttpMethod.Get, "/api/factorio/installations/rescan");
        Assert.True(g.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound or HttpStatusCode.BadRequest, g.Code.ToString());
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/factorio/installations/rescan");
        using var res = await env.Http.SendAsync(req);
        Assert.True((int)res.StatusCode < 500);
    }

    [Fact]
    public async Task Mutations_InvalidateTheCache_Immediately()
    {
        if (Unsupported) return;
        using var env = await Create();
        var a = env.Install("a", "2.0.76");
        var b = env.Install("b", "1.1.110");
        Assert.False(Has(await env.List(), a));                  // fills the cache
        await env.Add(a);
        Assert.True(Has(await env.List(), a), "POST must invalidate the cache");
        Assert.True((await env.Environment_("alpha")).Json.GetProperty("factorio").GetProperty("found").GetBoolean());
        await env.Add(b);
        var legacy = await env.Environment_("legacy");
        Assert.Equal(Id(b), legacy.Json.GetProperty("resolution").GetProperty("chosenId").GetString());
        await env.Remove(Id(b));
        Assert.False(Has(await env.List(), b), "DELETE must invalidate the cache");
        Assert.Equal("none", (await env.Environment_("legacy")).Json.GetProperty("resolution").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Cache_ExpiresWithinAboutFiveSeconds_WithoutRescan()
    {
        if (Unsupported) return;
        using var env = await Create();
        var anchor = env.Install("base-install", "2.0.76");
        await env.Add(anchor);
        await env.List();
        var late = env.Install("Factorio-1.1.110", "1.1.110");
        await Task.Delay(5600);
        Assert.True(Has(await env.List(), late), "list must be recomputed at least every 5 seconds");
    }

    // ------------------------------------------------------------------ /api/environment resolution

    private static async Task<(JsonElement Factorio, JsonElement Resolution)> EnvFor(ApiEnv env, string? mod)
    {
        var r = await env.Environment_(mod);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return (r.Json.GetProperty("factorio"), r.Json.GetProperty("resolution"));
    }

    [Fact]
    public async Task Environment_Resolution_PicksTheInstallMatchingEachModsFactorioVersion()
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        var v11 = env.Install("v11", "1.1.110");
        var v20old = env.Install("v20old", "2.0.60");
        var v20 = env.Install("v20", "2.0.76");
        var v21 = env.Install("v21", "2.1.3");
        foreach (var e in new[] { v11, v20old, v20, v21 }) await env.Add(e);

        foreach (var (mod, expectedExe, expectedVersion, modVersion) in new[] { ("alpha", v20, "2.0.76", "2.0"), ("legacy", v11, "1.1.110", "1.1"), ("future", v21, "2.1.3", "2.1") })
        {
            var (f, res) = await EnvFor(env, mod);
            Assert.Equal("auto", res.S("mode"));
            Assert.Equal(modVersion, res.S("modVersion"));
            Assert.Equal(Id(expectedExe), res.S("chosenId"));
            Assert.False(string.IsNullOrWhiteSpace(res.S("reason")));
            Assert.True(f.GetProperty("found").GetBoolean());
            Assert.Equal(FakeFactorio.Norm(expectedExe), f.S("path"));
            Assert.Equal(expectedVersion, f.S("version"));
            Assert.Equal("ui", f.S("source"));
            Assert.Equal(JsonValueKind.Null, f.GetProperty("error").ValueKind);
        }
    }

    [Fact]
    public async Task Environment_Resolution_NoMatch_ReportsNone_WithReasonAsError()
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        await env.Add(env.Install("v20", "2.0.76"));
        await env.Add(env.Install("v21", "2.1.3"));
        var (f, res) = await EnvFor(env, "legacy");
        Assert.Equal("none", res.S("mode"));
        Assert.Equal("1.1", res.S("modVersion"));
        Assert.Equal(JsonValueKind.Null, res.GetProperty("chosenId").ValueKind);
        var reason = res.S("reason")!;
        Assert.Contains("1.1", reason);
        Assert.Contains("2.0.76", reason);
        Assert.Contains("2.1.3", reason);
        Assert.False(f.GetProperty("found").GetBoolean());
        Assert.Equal(reason, f.S("error"));
        Assert.Equal(JsonValueKind.Null, f.GetProperty("path").ValueKind);
        Assert.Equal("none", f.S("source"));
    }

    [Fact]
    public async Task Environment_NoInstallsAtAll_IsNone_ForEveryMod_AndWithoutModId()
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        foreach (var mod in new string?[] { null, "alpha", "legacy", "oddver" })
        {
            var (f, res) = await EnvFor(env, mod);
            Assert.Equal("none", res.S("mode"));
            Assert.False(f.GetProperty("found").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(res.S("reason")));
            Assert.False(string.IsNullOrWhiteSpace(f.S("error")));
        }
    }

    [Fact]
    public async Task Environment_WithoutModId_PicksTheHighestVersion_AndHasNullModVersion()
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        var v11 = env.Install("v11", "1.1.110");
        var v21 = env.Install("v21", "2.1.3");
        var v20 = env.Install("v20", "2.0.76");
        foreach (var e in new[] { v11, v21, v20 }) await env.Add(e);
        var (f, res) = await EnvFor(env, null);
        Assert.Equal("auto", res.S("mode"));
        Assert.Equal(JsonValueKind.Null, res.GetProperty("modVersion").ValueKind);
        Assert.Equal(Id(v21), res.S("chosenId"));
        Assert.Equal("2.1.3", f.S("version"));
        Assert.True(f.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Environment_ModWithInvalidFactorioVersion_PicksHighest_AndMentionsIt()
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        var v11 = env.Install("v11", "1.1.110");
        var v20 = env.Install("v20", "2.0.76");
        await env.Add(v11); await env.Add(v20);
        var (f, res) = await EnvFor(env, "oddver");
        Assert.Equal("auto", res.S("mode"));
        Assert.True(res.GetProperty("modVersion").ValueKind == JsonValueKind.Null || res.S("modVersion") == "abc", "modVersion: " + res);   // null or the raw invalid text
        Assert.Equal(Id(v20), res.S("chosenId"));
        Assert.Contains("factorio_version", res.S("reason")!);
    }

    [Fact]
    public async Task Environment_SourceIsTheFirstOfTheChosenInstallsSources()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var cfg = env.Install("cfg-install", "2.0.76");
        var sibling = env.Install("Factorio-1.1.110", "1.1.110");
        AddMod(env.Workspace, "alpha", "2.0"); AddMod(env.Workspace, "legacy", "1.1");
        await env.StartServer(cfg);
        if (await env.HasForeign()) return;
        Assert.Equal("config", (await EnvFor(env, "alpha")).Factorio.S("source"));
        Assert.Equal("auto", (await EnvFor(env, "legacy")).Factorio.S("source"));
        await env.Add(cfg);
        Assert.Equal("ui", (await EnvFor(env, "alpha")).Factorio.S("source"));
    }

    [Fact]
    public async Task Environment_SameVersionTwice_UiBeatsConfigBeatsAuto()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var cfg = env.Install("cfg-install", "2.0.76");
        var autoSib = env.Install("Factorio-2.0.76", "2.0.76");
        var ui = env.Install("ui-install", "2.0.76");
        AddMod(env.Workspace, "alpha", "2.0");
        await env.StartServer(cfg);
        if (await env.HasForeign()) return;
        Assert.Equal(Id(cfg), (await EnvFor(env, "alpha")).Resolution.S("chosenId"));    // config beats auto
        await env.Add(ui);
        Assert.Equal(Id(ui), (await EnvFor(env, "alpha")).Resolution.S("chosenId"));     // ui beats config
        Assert.True(Has(await env.List(), autoSib));
    }

    [Fact]
    public async Task Environment_UnknownOrOddModIds_AreNotServerErrors()
    {
        if (Unsupported) return;
        using var env = await Create();
        var failures = new List<string>();
        foreach (var id in new[] { "nope", "", "../../etc", "%00", new string('a', 3000), "alpha" })
        {
            var r = await Send(env.Http, HttpMethod.Get, "/api/environment?modId=" + Uri.EscapeDataString(id));
            if (r.Code >= 500) failures.Add($"modId '{id[..Math.Min(10, id.Length)]}': {r.Code} {r.Raw}");
            if (r.Status == HttpStatusCode.OK && !r.Json.TryGetProperty("factorio", out _)) failures.Add("200 without factorio object for " + id);
        }
        NoFailures(failures);
    }

    [Fact]
    public async Task Environment_KeepsExistingShape_AndResolutionObject()
    {
        if (Unsupported) return;
        using var env = await Create();
        var r = await env.Environment_("alpha");
        var f = r.Json.GetProperty("factorio");
        foreach (var k in new[] { "found", "path", "error", "source", "version" }) Assert.True(f.TryGetProperty(k, out _), "factorio." + k);
        var res = r.Json.GetProperty("resolution");
        foreach (var k in new[] { "modVersion", "mode", "chosenId", "reason" }) Assert.True(res.TryGetProperty(k, out _), "resolution." + k);
        Assert.True(res.S("mode") is "auto" or "manual" or "none");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(env.Http, HttpMethod.Get, "/api/environment", tweak: q => q.Headers.Host = "attacker.example")).Status);
    }

    // ------------------------------------------------------------------ jobs

    private static void AssertFirstLine(ApiResp job, string expected) =>
        Assert.True(job.Lines.Count > 0 && job.Lines[0].Text == expected, $"first line expected '{expected}', got '{(job.Lines.Count > 0 ? job.Lines[0].Text : "<none>")}'");

    [Theory]
    [InlineData("smoke")]
    [InlineData("run")]
    public async Task Jobs_AutoPickTheInstallMatchingTheMod(string kind)
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        var v11 = env.Install("v11", "1.1.110");
        var v20 = env.Install("v20", "2.0.76");
        var v21 = env.Install("v21", "2.1.3");
        foreach (var e in new[] { v11, v20, v21 }) await env.Add(e);

        var job = await env.RunJob("alpha", new { kind });
        Assert.Equal("succeeded", job.Str("state"));
        Assert.True(FakeFactorio.WasStarted(v20), "the 2.0 install must be started for a 2.0 mod");
        Assert.False(FakeFactorio.WasStarted(v11) || FakeFactorio.WasStarted(v21), "other installs must not be started");
        AssertFirstLine(job, $"Using Factorio 2.0.76 ({FakeFactorio.Norm(v20)}) [auto]");
        var f = job.Json.GetProperty("factorio");
        Assert.Equal(Id(v20), f.S("id"));
        Assert.Equal(FakeFactorio.Norm(v20), f.S("path"));
        Assert.Equal("2.0.76", f.S("version"));
        Assert.Equal("auto", f.S("mode"));
        Assert.False(string.IsNullOrWhiteSpace(f.S("reason")));

        var legacy = await env.RunJob("legacy", new { kind });
        Assert.Equal("succeeded", legacy.Str("state"));
        Assert.True(FakeFactorio.WasStarted(v11));
        Assert.False(FakeFactorio.WasStarted(v21));
        AssertFirstLine(legacy, $"Using Factorio 1.1.110 ({FakeFactorio.Norm(v11)}) [auto]");
        Assert.Equal(Id(v11), legacy.Json.GetProperty("factorio").S("id"));

        var future = await env.RunJob("future", new { kind });
        Assert.Equal("succeeded", future.Str("state"));
        Assert.True(FakeFactorio.WasStarted(v21));
        AssertFirstLine(future, $"Using Factorio 2.1.3 ({FakeFactorio.Norm(v21)}) [auto]");
    }

    [Fact]
    public async Task Jobs_ValidateAndPack_HaveNullFactorio_AndStartNothing()
    {
        if (Unsupported) return;
        using var env = await Create();
        var v20 = env.Install("v20", "2.0.76");
        await env.Add(v20);
        foreach (var kind in new[] { "validate", "pack" })
        {
            var job = await env.RunJob("alpha", new { kind });
            Assert.Equal("succeeded", job.Str("state"));
            Assert.True(job.Json.TryGetProperty("factorio", out var f), "factorio property expected: " + job.Raw);
            Assert.Equal(JsonValueKind.Null, f.ValueKind);
        }
        Assert.False(FakeFactorio.WasStarted(v20));
    }

    [Fact]
    public async Task Jobs_ManualInstallation_IsUsed_EvenOnMismatch_WithWarning()
    {
        if (Unsupported) return;
        using var env = await Create();
        var v11 = env.Install("v11", "1.1.110");
        var v20 = env.Install("v20", "2.0.76");
        await env.Add(v11); await env.Add(v20);

        // mismatching manual choice for the 2.0 mod "alpha"
        var job = await env.RunJob("alpha", new { kind = "smoke", installation = Id(v11) });
        Assert.Equal("succeeded", job.Str("state"));
        Assert.True(FakeFactorio.WasStarted(v11));
        Assert.False(FakeFactorio.WasStarted(v20));
        AssertFirstLine(job, $"Using Factorio 1.1.110 ({FakeFactorio.Norm(v11)}) [manual]");
        var f = job.Json.GetProperty("factorio");
        Assert.Equal("manual", f.S("mode"));
        Assert.Equal(Id(v11), f.S("id"));
        var rest = job.Lines.Skip(1).Select(l => l.Text.ToLowerInvariant()).ToList();
        Assert.True(rest.Any(l => (l.Contains("warn") || l.Contains("mismatch") || l.Contains("does not match")) && l.Contains("2.0")),
            "mismatch warning line expected; lines: " + string.Join(" | ", job.Lines.Select(l => l.Text)));
    }

    [Fact]
    public async Task Jobs_ManualInstallation_Matching_HasNoMismatchWarning_AndBeatsAutoChoice()
    {
        if (Unsupported) return;
        using var env = await Create();
        var older = env.Install("older", "2.0.60");
        var newer = env.Install("newer", "2.0.76");
        await env.Add(older); await env.Add(newer);
        var job = await env.RunJob("alpha", new { kind = "smoke", installation = Id(older) });
        Assert.Equal("succeeded", job.Str("state"));
        Assert.True(FakeFactorio.WasStarted(older));
        Assert.False(FakeFactorio.WasStarted(newer));
        AssertFirstLine(job, $"Using Factorio 2.0.60 ({FakeFactorio.Norm(older)}) [manual]");
        Assert.DoesNotContain(job.Lines.Skip(1), l => l.Text.ToLowerInvariant().Contains("mismatch") || l.Text.ToLowerInvariant().Contains("does not match"));
    }

    [Fact]
    public async Task Jobs_ManualInstallation_CanBeAConfigOrAutoCandidate_NotOnlyRegistered()
    {
        if (Unsupported) return;
        using var env = new ApiEnv();
        var cfg = env.Install("cfg-install", "2.0.76");
        var sibling = env.Install("Factorio-2.0.99", "2.0.99");
        AddMod(env.Workspace, "alpha", "2.0");
        await env.StartServer(cfg);
        var job = await env.RunJob("alpha", new { kind = "smoke", installation = Id(cfg) });
        Assert.Equal("succeeded", job.Str("state"));
        Assert.True(FakeFactorio.WasStarted(cfg));
        Assert.False(FakeFactorio.WasStarted(sibling));
        var job2 = await env.RunJob("alpha", new { kind = "smoke", installation = Id(sibling) });
        Assert.Equal("succeeded", job2.Str("state"));
        Assert.True(FakeFactorio.WasStarted(sibling));
    }

    [Fact]
    public async Task Jobs_UnknownInstallationId_Is400_AndCreatesNoJob_AndStartsNothing()
    {
        if (Unsupported) return;
        using var env = await Create();
        var v20 = env.Install("v20", "2.0.76");
        await env.Add(v20);
        var before = await env.JobCount();
        var failures = new List<string>();
        foreach (var raw in new[]
                 {
                     "{\"kind\":\"smoke\",\"installation\":\"000000000000\"}", "{\"kind\":\"run\",\"installation\":\"nope\"}",
                     "{\"kind\":\"smoke\",\"installation\":5}", "{\"kind\":\"smoke\",\"installation\":true}", "{\"kind\":\"smoke\",\"installation\":[\"x\"]}",
                     "{\"kind\":\"smoke\",\"installation\":{}}", "{\"kind\":\"smoke\",\"installation\":\"" + new string('a', 3000) + "\"}",
                     "{\"kind\":\"smoke\",\"installation\":\"../../etc/passwd\"}", "{\"kind\":\"smoke\",\"installation\":\"" + Id(v20) + "x\"}",
                 })
        {
            var r = await env.StartJobRaw("alpha", raw);
            if (r.Status != HttpStatusCode.BadRequest) failures.Add($"{raw[..Math.Min(raw.Length, 70)]}: expected 400, got {r.Code} {r.Raw}");
            else if (string.IsNullOrWhiteSpace(r.Error)) failures.Add("400 without error: " + raw[..30]);
        }
        NoFailures(failures);
        Assert.Equal(before, await env.JobCount());
        Assert.False(FakeFactorio.WasStarted(v20));
    }

    [Fact]
    public async Task Jobs_NullInstallation_MeansAutomatic()
    {
        if (Unsupported) return;
        using var env = await Create();
        var v20 = env.Install("v20", "2.0.76");
        await env.Add(v20);
        var r = await env.StartJobRaw("alpha", "{\"kind\":\"smoke\",\"installation\":null}");
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        var job = await env.WaitTerminal(r.Str("id")!);
        Assert.Equal("succeeded", job.Str("state"));
        Assert.Equal("auto", job.Json.GetProperty("factorio").S("mode"));
    }

    [Fact]
    public async Task Jobs_NoMatchingInstall_FailWithReason_AndStartNothing()
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        var v20 = env.Install("v20", "2.0.76");
        var v21 = env.Install("v21", "2.1.3");
        await env.Add(v20); await env.Add(v21);
        foreach (var kind in new[] { "smoke", "run" })
        {
            var job = await env.RunJob("farout", new { kind });          // factorio_version 3.0, valid
            Assert.Equal("failed", job.Str("state"));
            var error = job.Str("error")!;
            Assert.Contains("3.0", error);
            Assert.Contains("2.0.76", error);
            Assert.Contains("2.1.3", error);
            var f = job.Json.TryGetProperty("factorio", out var fe) ? fe : default;
            Assert.True(f.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined, "factorio must be null when nothing was chosen: " + job.Raw);
        }
        Assert.False(FakeFactorio.WasStarted(v20) || FakeFactorio.WasStarted(v21));
        // other mods are unaffected and validate/pack still work for the unmatched one
        Assert.Equal("succeeded", (await env.RunJob("farout", new { kind = "validate" })).Str("state"));
        Assert.Equal("succeeded", (await env.RunJob("alpha", new { kind = "smoke" })).Str("state"));
    }

    [Fact]
    public async Task Jobs_NothingInstalled_FailWithReason()
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        foreach (var kind in new[] { "smoke", "run" })
        {
            var job = await env.RunJob("alpha", new { kind });
            Assert.Equal("failed", job.Str("state"));
            Assert.False(string.IsNullOrWhiteSpace(job.Str("error")));
        }
    }

    [Fact]
    public async Task Jobs_InstallWithoutDetectableVersion_IsNotUsedForAutomaticChoice()
    {
        if (Unsupported) return;
        using var env = await Create();
        if (await env.HasForeign()) return;
        var blind = env.Install("blind", null, info: false);
        await env.Add(blind);
        var job = await env.RunJob("alpha", new { kind = "smoke" });
        Assert.Equal("failed", job.Str("state"));
        Assert.False(FakeFactorio.WasStarted(blind));
        // ... but it can be chosen manually
        var manual = await env.RunJob("alpha", new { kind = "smoke", installation = Id(blind) });
        Assert.Equal("succeeded", manual.Str("state"));
        Assert.True(FakeFactorio.WasStarted(blind));
        Assert.Equal("manual", manual.Json.GetProperty("factorio").S("mode"));
    }

    [Fact]
    public async Task Jobs_UiProfileDirectoryIncludesTheTargetVersion()
    {
        if (Unsupported) return;
        using var env = await Create();
        await env.Add(env.Install("v11", "1.1.110"));
        await env.Add(env.Install("v20", "2.0.76"));
        Assert.Equal("succeeded", (await env.RunJob("alpha", new { kind = "smoke" })).Str("state"));
        Assert.Equal("succeeded", (await env.RunJob("legacy", new { kind = "smoke" })).Str("state"));
        var profiles = env.Project.Combine(".fmstudio", "profiles");
        var names = Directory.GetDirectories(profiles).Select(Path.GetFileName).ToList();
        Assert.Contains("ui-alpha-2.0", names);
        Assert.Contains("ui-legacy-1.1", names);
        Assert.DoesNotContain("ui-alpha", names);
        Assert.DoesNotContain("ui-legacy", names);
    }

    [Fact]
    public async Task Jobs_SameModWithDifferentVersions_DoNotShareProfiles_ModVersionChangeUsesNewProfile()
    {
        if (Unsupported) return;
        using var env = await Create();
        await env.Add(env.Install("v11", "1.1.110"));
        await env.Add(env.Install("v20", "2.0.76"));
        Assert.Equal("succeeded", (await env.RunJob("alpha", new { kind = "smoke" })).Str("state"));
        // the user re-targets the mod to 1.1
        env.Workspace.Write("alpha/info.json", ModInfo("alpha", "1.1"));
        await Task.Delay(300);
        var job = await env.RunJob("alpha", new { kind = "smoke" });
        Assert.Equal("succeeded", job.Str("state"));
        Assert.Contains("1.1.110", job.Lines[0].Text);
        var names = Directory.GetDirectories(env.Project.Combine(".fmstudio", "profiles")).Select(Path.GetFileName).ToList();
        Assert.Contains("ui-alpha-2.0", names);
        Assert.Contains("ui-alpha-1.1", names);
    }

    [Fact]
    public async Task Jobs_ModWithInvalidFactorioVersion_FailsValidation_BeforeAnyInstallIsStarted()
    {
        if (Unsupported) return;
        using var env = await Create();
        var v20 = env.Install("v20", "2.0.76");
        await env.Add(v20);
        var job = await env.RunJob("oddver", new { kind = "smoke" });
        Assert.Equal("failed", job.Str("state"));
        Assert.False(FakeFactorio.WasStarted(v20));
    }

    [Fact]
    public async Task Jobs_Dlc_ModsTargetingOldFactorio_FailValidationWithFms015()
    {
        if (Unsupported) return;
        using var env = await Create(seed: e => AddMod(e.Workspace, "dlcold", rawInfo:
            """{ "name": "dlcold", "version": "1.2.3", "title": "x", "author": "x", "factorio_version": "1.1", "dependencies": ["base >= 1.1.0", "space-age"] }"""));
        var v11 = env.Install("v11", "1.1.110");
        await env.Add(v11);
        var job = await env.RunJob("dlcold", new { kind = "smoke" });
        Assert.Equal("failed", job.Str("state"));
        Assert.False(FakeFactorio.WasStarted(v11));
        var validate = await env.RunJob("dlcold", new { kind = "validate" });
        Assert.Contains(validate.Lines, l => l.Text.Contains("FMS015"));
    }

    [Fact]
    public async Task Jobs_ChosenInstallVanishedBeforeStart_FailsCleanly()
    {
        if (Unsupported) return;
        using var env = await Create();
        var v20 = env.Install("v20", "2.0.76");
        await env.Add(v20);
        File.Delete(v20);
        await env.Rescan();
        var job = await env.RunJob("alpha", new { kind = "smoke" });
        Assert.Equal("failed", job.Str("state"));
        Assert.False(string.IsNullOrWhiteSpace(job.Str("error")));
        // manual id of a now-invalid entry
        var manual = await env.StartJobRaw("alpha", JsonSerializer.Serialize(new { kind = "smoke", installation = Id(v20) }));
        Assert.True(manual.Status == HttpStatusCode.BadRequest || manual.Status == HttpStatusCode.Accepted, manual.Raw);
        if (manual.Status == HttpStatusCode.Accepted) Assert.Equal("failed", (await env.WaitTerminal(manual.Str("id")!)).Str("state"));
    }

    // ------------------------------------------------------------------ templates / new mod

    [Fact]
    public async Task Templates_ExposeSupportedVersions_MatchingCore()
    {
        if (Unsupported) return;
        using var env = await Create();
        var items = JsonDocument.Parse(await env.Http.GetStringAsync("/api/templates")).RootElement.EnumerateArray().ToList();
        Assert.Equal(ModScaffolder.Templates.Count, items.Count);
        foreach (var t in ModScaffolder.Templates)
        {
            var item = items.Single(i => i.S("id") == t.Id);
            Assert.True(item.TryGetProperty("supportedVersions", out var sv), "supportedVersions missing for " + t.Id);
            Assert.Equal(t.SupportedVersions.ToList(), sv.EnumerateArray().Select(x => x.GetString()!).ToList());
        }
        var empty = items.Single(i => i.S("id") == "empty");
        foreach (var v in new[] { "1.1", "2.0", "2.1" }) Assert.Contains(v, empty.Strs("supportedVersions"));
    }

    private static string? FindString(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                if (FindString(p.Value, name) is { } found) return found;
            }
        return null;
    }

    private async Task<ApiResp> New(ApiEnv env, object body, Action<HttpRequestMessage>? tweak = null) =>
        await Send(env.Http, HttpMethod.Post, "/api/workspace/new", JsonSerializer.Serialize(body), tweak);

    [Fact]
    public async Task NewMod_WithFactorioVersion_WritesInfoJson_AndOverviewShowsIt()
    {
        if (Unsupported) return;
        using var env = await Create();
        var failures = new List<string>();
        var n = 0;
        foreach (var t in ModScaffolder.Templates)
            foreach (var v in t.SupportedVersions)
            {
                var name = $"nm{n++}-{t.Id}";
                var r = await New(env, new { name, template = t.Id, factorioVersion = v });
                if (r.Status != HttpStatusCode.Created) { failures.Add($"{t.Id}@{v}: {r.Code} {r.Raw}"); continue; }
                var info = JsonDocument.Parse(File.ReadAllText(env.Workspace.Combine(name, "info.json"))).RootElement;
                if (info.GetProperty("factorio_version").GetString() != v) failures.Add($"{t.Id}@{v}: info.json says {info.GetProperty("factorio_version")}");
                if (!info.Strs("dependencies").Contains($"base >= {v}.0")) failures.Add($"{t.Id}@{v}: base dependency line");
                var modsJson = await env.Http.GetStringAsync("/api/mods");
                var entry = JsonDocument.Parse(modsJson).RootElement.EnumerateArray().FirstOrDefault(m => m.S("name") == name);
                if (entry.ValueKind == JsonValueKind.Undefined) { failures.Add($"{name}: not listed"); continue; }
                var overview = JsonDocument.Parse(await env.Http.GetStringAsync("/api/mods/" + Uri.EscapeDataString(entry.S("id")!))).RootElement;
                var shown = FindString(overview, "factorioVersion");
                if (shown != v) failures.Add($"{name}: overview factorioVersion '{shown}' != {v}");
            }
        NoFailures(failures);
    }

    [Fact]
    public async Task NewMod_DefaultFactorioVersionIs2_0()
    {
        if (Unsupported) return;
        using var env = await Create();
        var r = await New(env, new { name = "defaultver" });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        var info = JsonDocument.Parse(File.ReadAllText(env.Workspace.Combine("defaultver", "info.json"))).RootElement;
        Assert.Equal("2.0", info.GetProperty("factorio_version").GetString());
    }

    [Fact]
    public async Task NewMod_UnsupportedOrInvalidVersion_Is400_AndCreatesNothing()
    {
        if (Unsupported) return;
        using var env = await Create();
        var before = Directory.GetDirectories(env.Workspace.Path).Select(Path.GetFileName).OrderBy(x => x).ToList();
        var failures = new List<string>();
        var idx = 0;
        foreach (var t in ModScaffolder.Templates)
            foreach (var v in new[] { "9.9", "0.18", "3.0" }.Concat(new[] { "1.0", "1.1" }.Where(x => !t.SupportedVersions.Contains(x))))
            {
                var r = await New(env, new { name = "bad" + idx++, template = t.Id, factorioVersion = v });
                if (r.Status != HttpStatusCode.BadRequest) failures.Add($"{t.Id}@{v}: expected 400, got {r.Code} {r.Raw}");
                else if (string.IsNullOrWhiteSpace(r.Error)) failures.Add($"{t.Id}@{v}: no error message");
            }
        foreach (var raw in new[]
                 {
                     "\"abc\"", "\"2\"", "\"2.0.1\"", "\" \"", "5", "2.0", "true", "[]", "{}", "\"2.0\\u0000\"", "\"-2.0\"", "\"2.0; DROP\"",
                     "\"" + new string('1', 3000) + ".0\"",
                 })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/new") { Content = new StringContent("{\"name\":\"rawbad" + idx++ + "\",\"factorioVersion\":" + raw + "}", Encoding.UTF8, "application/json") };
            using var res = await env.Http.SendAsync(req);
            if (res.StatusCode != HttpStatusCode.BadRequest) failures.Add($"factorioVersion {raw[..Math.Min(20, raw.Length)]}: expected 400, got {(int)res.StatusCode}");
        }
        NoFailures(failures);
        Assert.Equal(before, Directory.GetDirectories(env.Workspace.Path).Select(Path.GetFileName).OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task NewMod_NullFactorioVersion_IsDefaultOr400_NeverAServerError()
    {
        if (Unsupported) return;
        using var env = await Create();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/new") { Content = new StringContent("{\"name\":\"nullver\",\"factorioVersion\":null}", Encoding.UTF8, "application/json") };
        using var res = await env.Http.SendAsync(req);
        Assert.True(res.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest, "got " + res.StatusCode);
    }
}
