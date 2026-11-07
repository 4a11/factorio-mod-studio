using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Black-box tests of the drag-and-drop mod import API (zip upload and folder-upload sessions).</summary>
public class ImportTests
{
    private const long MiB = 1024 * 1024;

    private sealed record Resp(HttpStatusCode Status, string Raw)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement.Clone();
        public string? Str(string property) => Json.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private static string InfoJson(string name, string version = "1.0.0") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name, ["version"] = version, ["title"] = name, ["author"] = "a",
            ["factorio_version"] = "2.0", ["dependencies"] = new[] { "base" },
        });

    private static async Task<RunningUi> Start(TempDir project, TempDir workspace) =>
        await UiServer.StartAsync(
            new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false),
            new UiOptions(Port: 0, WorkspaceDirectory: workspace.Path));

    private static HttpClient Client(RunningUi ui, bool withToken = true)
    {
        var http = new HttpClient { BaseAddress = new Uri(ui.Url), Timeout = TimeSpan.FromMinutes(5) };
        if (withToken) http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        return http;
    }

    private static async Task<Resp> Read(HttpResponseMessage response)
    {
        using (response) return new Resp(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> GetJson(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<List<JsonElement>> Mods(HttpClient http) => (await GetJson(http, "/api/mods")).EnumerateArray().ToList();

    private static async Task<Resp> PostZip(HttpClient http, byte[] zip) =>
        await Read(await http.PostAsync("/api/workspace/import-zip", new ByteArrayContent(zip)));

    /// <summary>Builds a zip from arbitrary (possibly hostile) entry names.</summary>
    private static byte[] Zip(params (string Name, string Content)[] entries) =>
        Zip(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content))).ToArray());

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (name.EndsWith('/')) continue;
                using var s = entry.Open();
                s.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static byte[] BigZip(string name, int entries, long bytesPerEntry)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("info.json").Open())) w.Write(InfoJson(name));
            var chunk = new byte[MiB]; // zeros: compresses ~1000:1
            for (var i = 0; i < entries; i++)
            {
                using var s = zip.CreateEntry($"big{i}.lua", CompressionLevel.Fastest).Open();
                for (long written = 0; written < bytesPerEntry; written += chunk.Length) s.Write(chunk, 0, (int)Math.Min(chunk.Length, bytesPerEntry - written));
            }
        }
        return ms.ToArray();
    }

    private static (string, string) Info(string name, string prefix = "") => (prefix + "info.json", InfoJson(name));

    /// <summary>Asserts a failed import left the workspace without any new folder or staging data.</summary>
    private static void AssertWorkspaceClean(TempDir workspace, params string[] allowedTopLevel)
    {
        if (!Directory.Exists(workspace.Path)) return;
        foreach (var entry in Directory.GetFileSystemEntries(workspace.Path))
        {
            var name = Path.GetFileName(entry);
            if (name == ".fmstudio")
            {
                var imports = Path.Combine(entry, "imports");
                if (Directory.Exists(imports)) Assert.Empty(Directory.GetFileSystemEntries(imports));
                continue;
            }
            Assert.True(allowedTopLevel.Contains(name), $"unexpected workspace entry '{name}'");
        }
    }

    private static void AssertError(Resp r, HttpStatusCode expected)
    {
        Assert.Equal(expected, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Str("error")), $"expected JSON {{\"error\":...}}, got: {r.Raw}");
    }

    /// <summary>A GUID-named file beside the workspace that must never be touched, plus escape detection.</summary>
    private sealed class Canary : IDisposable
    {
        private readonly string _dir;
        public string Marker { get; } = "fms-evil-" + Guid.NewGuid().ToString("N");
        public string CanaryPath { get; }

        public Canary(TempDir workspace)
        {
            _dir = Path.GetDirectoryName(workspace.Path)!;
            CanaryPath = Path.Combine(_dir, $"fms-canary-{Guid.NewGuid():N}.lua");
            File.WriteAllText(CanaryPath, "keep");
        }

        public void AssertIntact(TempDir workspace)
        {
            Assert.Equal("keep", File.ReadAllText(CanaryPath));
            Assert.Empty(Directory.GetFiles(_dir, Marker + "*"));
            if (Directory.Exists(workspace.Path)) Assert.Empty(Directory.GetFiles(workspace.Path, Marker + "*", SearchOption.AllDirectories));
        }

        public void Dispose() { try { File.Delete(CanaryPath); } catch (IOException) { } }
    }

    // ------------------------------------------------------------------ happy paths

    [Fact]
    public async Task Zip_RootLayout_ImportsAndAppearsAsEditableWorkspaceMod()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var r = await PostZip(http, Zip(Info("rootmod"), ("data.lua", "data:extend({})"), ("locale/en/a.cfg", "[x]\na=b"), ("graphics/", "")));

        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal("rootmod", r.Str("name"));
        Assert.Equal("1.0.0", r.Str("version"));
        Assert.True(Path.GetFileName(r.Str("folder")!.TrimEnd('/', '\\')) == "rootmod", "folder: " + r.Str("folder"));
        Assert.Equal(3, r.Json.GetProperty("files").GetInt32());
        Assert.Equal("data:extend({})", File.ReadAllText(workspace.Combine("rootmod", "data.lua")));
        Assert.True(File.Exists(workspace.Combine("rootmod", "locale", "en", "a.cfg")));

        var mod = (await Mods(http)).Single(m => m.GetProperty("name").GetString() == "rootmod");
        Assert.Equal("workspace", mod.GetProperty("source").GetString());
        Assert.True(mod.GetProperty("editable").GetBoolean());
        Assert.Equal(r.Str("id"), mod.GetProperty("id").GetString());

        using var file = await http.GetAsync($"/api/mods/{r.Str("id")}/file?path=data.lua");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        AssertWorkspaceClean(workspace, "rootmod");
    }

    [Fact]
    public async Task Zip_SingleTopLevelFolder_IsFlattenedAndNamedByInfoJson()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var r = await PostZip(http, Zip(Info("realname", "wrongname_9.9.9/"), ("wrongname_9.9.9/data.lua", "-- x"), ("wrongname_9.9.9/sub/y.lua", "-- y")));

        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.True(Path.GetFileName(r.Str("folder")!.TrimEnd('/', '\\')) == "realname", "folder: " + r.Str("folder"));
        Assert.True(File.Exists(workspace.Combine("realname", "info.json")));
        Assert.True(File.Exists(workspace.Combine("realname", "sub", "y.lua")));
        Assert.False(Directory.Exists(workspace.Combine("wrongname_9.9.9")));
        Assert.False(Directory.Exists(workspace.Combine("realname", "wrongname_9.9.9")));
        AssertWorkspaceClean(workspace, "realname");
    }

    [Fact]
    public async Task Zip_CreatesWorkspaceFolder_WhenMissing()
    {
        using var project = new TempDir();
        using var parent = new TempDir();
        var workspacePath = parent.Combine("not-yet", "ws");
        await using var ui = await UiServer.StartAsync(
            new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false),
            new UiOptions(Port: 0, WorkspaceDirectory: workspacePath));
        using var http = Client(ui);
        Assert.False(Directory.Exists(workspacePath));

        var r = await PostZip(http, Zip(Info("fresh"), ("data.lua", "")));

        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.True(File.Exists(Path.Combine(workspacePath, "fresh", "info.json")));
        Assert.Contains("fresh", (await Mods(http)).Select(m => m.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Zip_PublishesModsEvent()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var events = await http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(cts.Token));

        var r = await PostZip(http, Zip(Info("evmod"), ("data.lua", "")));
        Assert.Equal(HttpStatusCode.Created, r.Status);

        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("stream ended");
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (JsonDocument.Parse(line[6..]).RootElement.GetProperty("type").GetString() == "mods") break;
        }
    }

    [Fact]
    public async Task Zip_AcceptsSameOriginBrowserRequests_AndUtf8BomInfoJson()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var bomInfo = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(InfoJson("bommod"))).ToArray();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/import-zip") { Content = new ByteArrayContent(Zip(("info.json", bomInfo))) };
        request.Headers.TryAddWithoutValidation("Origin", ui.Url.TrimEnd('/'));
        var r = await Read(await http.SendAsync(request));

        Assert.Equal(HttpStatusCode.Created, r.Status);
    }

    [Fact]
    public async Task Zip_NameOfExactly100Characters_IsAccepted()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var name = new string('a', 100);

        var r = await PostZip(http, Zip(Info(name)));

        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.True(Directory.Exists(workspace.Combine(name)));
    }

    [Fact]
    public async Task Zip_SkipsDisallowedFileTypes_AndSymlinks()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content, int? unixMode = null)
            {
                var e = zip.CreateEntry(name);
                if (unixMode is not null) e.ExternalAttributes = unixMode.Value << 16;
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
            Add("info.json", InfoJson("skipmod"));
            Add("data.lua", "-- ok");
            Add("readme.md", "# hi");
            Add("evil.exe", "MZ");
            Add("lib/native.dll", "MZ");
            Add("run.sh", "#!/bin/sh");
            Add("link.lua", "/etc/passwd", unchecked((int)0xA1FF)); // unix symlink: S_IFLNK | 0777
        }

        var r = await PostZip(http, ms.ToArray());

        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal(4, r.Json.GetProperty("skipped").GetInt32());
        Assert.Equal(3, r.Json.GetProperty("files").GetInt32());
        var root = workspace.Combine("skipmod");
        Assert.False(File.Exists(Path.Combine(root, "evil.exe")));
        Assert.False(File.Exists(Path.Combine(root, "lib", "native.dll")));
        Assert.False(File.Exists(Path.Combine(root, "run.sh")));
        Assert.False(File.Exists(Path.Combine(root, "link.lua")) || new FileInfo(Path.Combine(root, "link.lua")).LinkTarget is not null);
        Assert.True(File.Exists(Path.Combine(root, "readme.md")));
    }

    [Fact]
    public async Task Zip_Exactly20000Entries_IsAccepted_AndOneMoreIsRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        byte[] Make(string name, int total) =>
            Zip(new[] { Info(name) }.Concat(Enumerable.Range(0, total - 1).Select(i => ($"d{i % 50}/f{i}.lua", ""))).ToArray());

        var tooMany = await PostZip(http, Make("many2", 20001));
        AssertError(tooMany, HttpStatusCode.BadRequest);
        Assert.False(Directory.Exists(workspace.Combine("many2")));
        AssertWorkspaceClean(workspace);

        var ok = await PostZip(http, Make("many1", 20000));
        Assert.Equal(HttpStatusCode.Created, ok.Status);
        Assert.Equal(20000, ok.Json.GetProperty("files").GetInt32());
    }

    // ------------------------------------------------------------------ hostile zips

    [Theory]
    [InlineData("../EVIL.lua")]
    [InlineData("..\\EVIL.lua")]
    [InlineData("sub/../../EVIL.lua")]
    [InlineData("sub\\..\\..\\EVIL.lua")]
    [InlineData("sub/../../../EVIL.lua")]
    [InlineData("/EVIL.lua")]
    [InlineData("\\EVIL.lua")]
    [InlineData("C:/EVIL.lua")]
    [InlineData("C:\\EVIL.lua")]
    [InlineData("C:EVIL.lua")]
    [InlineData("a\0b/EVIL.lua")]
    [InlineData("sub/EVIL\0.lua")]
    [InlineData("CON.lua")]
    [InlineData("sub/aux/EVIL.lua")]
    [InlineData("nul.txt")]
    [InlineData("COM1.lua")]
    [InlineData("LPT9/EVIL.lua")]
    [InlineData("../.EVIL.lua")]
    [InlineData("sub/../../.hidden/EVIL.lua")]
    [InlineData(".hidden\\..\\..\\EVIL.lua")]
    [InlineData("../")]
    [InlineData("sub//../../EVIL.lua")]
    public async Task Zip_UnsafeEntryPath_IsRejected_WithoutEscapingOrLeaving(string entryName)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        using var canary = new Canary(workspace);
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var name = entryName.Replace("EVIL", canary.Marker);

        // Both layouts: files at root, and inside the single top-level folder.
        foreach (var prefix in new[] { "", "okmod_1.0.0/" })
        {
            var r = await PostZip(http, Zip(Info("okmod", prefix), (name.StartsWith('/') || name.StartsWith('\\') || name.Contains(':') ? name : prefix + name, "owned"), (prefix + "data.lua", "")));
            AssertError(r, HttpStatusCode.BadRequest);
            Assert.False(Directory.Exists(workspace.Combine("okmod")));
            AssertWorkspaceClean(workspace);
            canary.AssertIntact(workspace);
        }
    }

    [Fact]
    public async Task Zip_HiddenFilesAndFolders_AreSkipped_NotRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        // As in real mods from the portal: dotfiles left over from git, editors and macOS.
        foreach (var prefix in new[] { "", "dotmod_1.0.0/" })
        {
            var r = await PostZip(http, Zip(Info("dotmod", prefix), (prefix + "data.lua", "-- ok"),
                (prefix + ".gitignore", "*.zip"), (prefix + ".DS_Store", "x"), (prefix + ".github/workflows/ci.json", "{}"),
                (prefix + "graphics/.hidden.png", "x")));

            Assert.Equal(HttpStatusCode.Created, r.Status);
            Assert.Equal(2, r.Json.GetProperty("files").GetInt32());
            Assert.Equal(4, r.Json.GetProperty("skipped").GetInt32());
            var root = workspace.Combine("dotmod");
            Assert.True(File.Exists(Path.Combine(root, "data.lua")));
            Assert.False(File.Exists(Path.Combine(root, ".gitignore")));
            Assert.False(File.Exists(Path.Combine(root, ".DS_Store")));
            Assert.False(Directory.Exists(Path.Combine(root, ".github")));
            Assert.False(File.Exists(Path.Combine(root, "graphics", ".hidden.png")));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Zip_AbsoluteRealPathEntry_DoesNotWriteOutsideWorkspace()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        using var canary = new Canary(workspace);
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var target = Path.Combine(Path.GetDirectoryName(workspace.Path)!, canary.Marker + ".lua");

        var r = await PostZip(http, Zip(Info("absmod"), ("/" + target.TrimStart('/'), "owned")));

        AssertError(r, HttpStatusCode.BadRequest);
        Assert.False(File.Exists(target));
        AssertWorkspaceClean(workspace);
        canary.AssertIntact(workspace);
    }

    [Theory]
    [InlineData(6, 100)]   // many entries, 600 MB total
    [InlineData(1, 600)]   // single 600 MB entry
    public async Task Zip_Bomb_IsRejected_AndLeavesNothing(int entries, int mibPerEntry)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var zip = BigZip("bomb", entries, mibPerEntry * MiB);
        Assert.True(zip.Length < 20 * MiB, "test bomb should be small compressed");

        var r = await PostZip(http, zip);

        AssertError(r, HttpStatusCode.BadRequest);
        Assert.False(Directory.Exists(workspace.Combine("bomb")));
        AssertWorkspaceClean(workspace);
        Assert.DoesNotContain("bomb", (await Mods(http)).Select(m => m.GetProperty("name").GetString()));

        // The name is not poisoned: a sane mod of the same name imports fine afterwards.
        var again = await PostZip(http, Zip(Info("bomb"), ("data.lua", "")));
        Assert.Equal(HttpStatusCode.Created, again.Status);
    }

    [Fact]
    public async Task Zip_BelowUncompressedLimit_WithLargeCompressibleFiles_IsAccepted()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var r = await PostZip(http, BigZip("bigbutok", 4, 100 * MiB)); // 400 MB < 500 MB

        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal(100 * MiB, new FileInfo(workspace.Combine("bigbutok", "big3.lua")).Length);
    }

    [Fact]
    public async Task Zip_Over200MiB_IsRejectedWith413()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/import-zip") { Content = new ByteArrayContent(new byte[200 * MiB + 1]) };
        request.Headers.ExpectContinue = true;
        var r = await Read(await http.SendAsync(request));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.Status);
        AssertWorkspaceClean(workspace);
    }

    [Fact]
    public async Task Zip_TwoTopLevelFolders_IsRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var r = await PostZip(http, Zip(Info("twin", "a/"), ("a/data.lua", ""), ("b/data.lua", "")));

        AssertError(r, HttpStatusCode.BadRequest);
        AssertWorkspaceClean(workspace);
    }

    [Fact]
    public async Task Zip_InfoJsonNestedTwoLevelsDeep_IsRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var r = await PostZip(http, Zip(Info("deep", "a/b/"), ("a/b/data.lua", "")));

        AssertError(r, HttpStatusCode.BadRequest);
        AssertWorkspaceClean(workspace);
    }

    [Fact]
    public async Task Zip_EmptyArchive_EmptyBody_Corrupt_AndTruncated_AreRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        using var emptyMs = new MemoryStream();
        using (new ZipArchive(emptyMs, ZipArchiveMode.Create, leaveOpen: true)) { }
        AssertError(await PostZip(http, emptyMs.ToArray()), HttpStatusCode.BadRequest);

        AssertError(await PostZip(http, Array.Empty<byte>()), HttpStatusCode.BadRequest);

        var rnd = new byte[4096];
        new Random(1).NextBytes(rnd);
        AssertError(await PostZip(http, rnd), HttpStatusCode.BadRequest);
        AssertError(await PostZip(http, new byte[] { 0x50, 0x4B, 0x03, 0x04 }.Concat(rnd).ToArray()), HttpStatusCode.BadRequest);
        AssertError(await PostZip(http, Encoding.UTF8.GetBytes("this is not a zip")), HttpStatusCode.BadRequest);

        var valid = Zip(Info("trunc"), ("data.lua", new string('x', 5000)));
        AssertError(await PostZip(http, valid.Take(valid.Length / 2).ToArray()), HttpStatusCode.BadRequest);

        AssertWorkspaceClean(workspace);
        Assert.Empty(await Mods(http));
    }

    [Fact]
    public async Task Zip_DirectoriesOnly_IsRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        AssertError(await PostZip(http, Zip(("a/", ""), ("a/b/", ""))), HttpStatusCode.BadRequest);
        AssertWorkspaceClean(workspace);
    }

    [Fact]
    public async Task Zip_DuplicateEntryNames_NeverCrash_AndNeverLeavePartialMods()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var r = await PostZip(http, Zip(Info("dupe"), ("data.lua", "one"), ("data.lua", "two")));

        Assert.True((int)r.Status < 500, $"server error: {r.Raw}");
        if (r.Status != HttpStatusCode.Created) { AssertError(r, HttpStatusCode.BadRequest); AssertWorkspaceClean(workspace); }
    }

    // ------------------------------------------------------------------ info.json validation

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"version":"1.0.0"}""")]
    [InlineData("""{"name":"nover"}""")]
    [InlineData("""{"name":"x","version":"abc"}""")]
    [InlineData("""{"name":"x","version":""}""")]
    [InlineData("""{"name":"x","version":1}""")]
    [InlineData("""{"name":"","version":"1.0.0"}""")]
    [InlineData("""{"name":null,"version":"1.0.0"}""")]
    [InlineData("""{"name":42,"version":"1.0.0"}""")]
    public async Task Zip_InvalidInfoJson_IsRejected(string info)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        AssertError(await PostZip(http, Zip(("info.json", info), ("data.lua", ""))), HttpStatusCode.BadRequest);
        AssertWorkspaceClean(workspace);
    }

    [Fact]
    public async Task Zip_MissingInfoJson_IsRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        AssertError(await PostZip(http, Zip(("data.lua", ""), ("control.lua", ""))), HttpStatusCode.BadRequest);
        AssertError(await PostZip(http, Zip(("m/data.lua", ""), ("m/control.lua", ""))), HttpStatusCode.BadRequest);
        AssertWorkspaceClean(workspace);
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".fmstudio")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/abs")]
    [InlineData("C:x")]
    [InlineData("my mod")]
    [InlineData("tab\there")]
    [InlineData("nul\0byte")]
    [InlineData("semi;colon")]
    [InlineData("")]
    public async Task Zip_InvalidModName_IsRejected_AndNothingEscapes(string name)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        using var canary = new Canary(workspace);
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var tricky = name.Replace("escape", canary.Marker);

        AssertError(await PostZip(http, Zip(("info.json", InfoJson(tricky)), ("data.lua", ""))), HttpStatusCode.BadRequest);
        AssertWorkspaceClean(workspace);
        canary.AssertIntact(workspace);
    }

    [Fact]
    public async Task Zip_ModNameOf101Characters_IsRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        AssertError(await PostZip(http, Zip(Info(new string('a', 101)))), HttpStatusCode.BadRequest);
        AssertWorkspaceClean(workspace);
    }

    // ------------------------------------------------------------------ duplicates & concurrency

    [Fact]
    public async Task Zip_DuplicateName_Returns409_AndNeverOverwritesOrMerges()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var first = await PostZip(http, Zip(Info("dup"), ("data.lua", "original")));
        Assert.Equal(HttpStatusCode.Created, first.Status);

        var second = await PostZip(http, Zip(Info("dup"), ("data.lua", "REPLACED"), ("extra.lua", "merged")));
        AssertError(second, HttpStatusCode.Conflict);
        Assert.Equal(first.Str("id"), second.Str("existing"));
        Assert.Equal("original", File.ReadAllText(workspace.Combine("dup", "data.lua")));
        Assert.False(File.Exists(workspace.Combine("dup", "extra.lua")));
        AssertWorkspaceClean(workspace, "dup");
    }

    [Fact]
    public async Task Zip_ExistingHandMadeFolder_Returns409()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("mine/info.json", InfoJson("mine"));
        workspace.Write("mine/data.lua", "precious");
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var existingId = (await Mods(http)).Single().GetProperty("id").GetString();

        var r = await PostZip(http, Zip(Info("mine"), ("data.lua", "new")));

        AssertError(r, HttpStatusCode.Conflict);
        Assert.Equal(existingId, r.Str("existing"));
        Assert.Equal("precious", File.ReadAllText(workspace.Combine("mine", "data.lua")));
    }

    [Fact]
    public async Task Zip_ExistingFolderWithoutInfoJson_StillBlocksOverwrite()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("shell/notes.txt", "keep me");
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var r = await PostZip(http, Zip(Info("shell"), ("data.lua", "")));

        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal("keep me", File.ReadAllText(workspace.Combine("shell", "notes.txt")));
        Assert.False(File.Exists(workspace.Combine("shell", "data.lua")));
    }

    [Fact]
    public async Task Zip_ConcurrentImportsOfSameName_ExactlyOneWins_WithoutMixedFiles()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var tasks = Enumerable.Range(0, 8).Select(i => PostZip(http, Zip(Info("race"), ($"only{i}.lua", $"v{i}"), ("data.lua", $"v{i}")))).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Conflict));
        var winner = Enumerable.Range(0, 8).Single(i => results[i].Status == HttpStatusCode.Created);
        Assert.Equal($"v{winner}", File.ReadAllText(workspace.Combine("race", "data.lua")));
        Assert.Equal(new[] { "data.lua", "info.json", $"only{winner}.lua" }, Directory.GetFiles(workspace.Combine("race")).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        AssertWorkspaceClean(workspace, "race");
    }

    // ------------------------------------------------------------------ folder sessions

    private static async Task<string> NewSession(HttpClient http)
    {
        var r = await Read(await http.PostAsync("/api/workspace/imports", null));
        Assert.Equal(HttpStatusCode.Created, r.Status);
        var id = r.Str("importId");
        Assert.False(string.IsNullOrEmpty(id));
        return id!;
    }

    private static async Task<Resp> PutFile(HttpClient http, string id, string path, byte[] bytes) =>
        await Read(await http.PutAsync($"/api/workspace/imports/{id}/file?path={Uri.EscapeDataString(path)}", new ByteArrayContent(bytes)));

    private static Task<Resp> PutText(HttpClient http, string id, string path, string text) => PutFile(http, id, path, Encoding.UTF8.GetBytes(text));

    private static async Task<Resp> Commit(HttpClient http, string id) =>
        await Read(await http.PostAsync($"/api/workspace/imports/{id}/commit", null));

    private static async Task<Resp> Cancel(HttpClient http, string id) =>
        await Read(await http.DeleteAsync($"/api/workspace/imports/{id}"));

    private static void AssertOk(Resp r) => Assert.True((int)r.Status is >= 200 and < 300, $"expected 2xx, got {(int)r.Status}: {r.Raw}");

    [Fact]
    public async Task Session_UploadAndCommit_CreatesMod_AndCleansStaging()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var id = await NewSession(http);
        AssertOk(await PutText(http, id, "info.json", InfoJson("folmod", "2.3.4")));
        AssertOk(await PutText(http, id, "data.lua", "data:extend({})"));
        AssertOk(await PutText(http, id, "locale/en/strings.cfg", "[a]\nb=c"));
        AssertOk(await PutFile(http, id, "graphics/icon.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

        // Staging lives under the workspace and must not show up as a mod while pending.
        Assert.True(Directory.Exists(workspace.Combine(".fmstudio", "imports", id)));
        Assert.DoesNotContain("folmod", (await Mods(http)).Select(m => m.GetProperty("name").GetString()));

        var r = await Commit(http, id);

        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal("folmod", r.Str("name"));
        Assert.Equal("2.3.4", r.Str("version"));
        Assert.True(Path.GetFileName(r.Str("folder")!.TrimEnd('/', '\\')) == "folmod", "folder: " + r.Str("folder"));
        Assert.Equal(4, r.Json.GetProperty("files").GetInt32());
        Assert.Equal("[a]\nb=c", File.ReadAllText(workspace.Combine("folmod", "locale", "en", "strings.cfg")));
        var mod = (await Mods(http)).Single(m => m.GetProperty("name").GetString() == "folmod");
        Assert.Equal("workspace", mod.GetProperty("source").GetString());
        Assert.True(mod.GetProperty("editable").GetBoolean());
        Assert.Equal(r.Str("id"), mod.GetProperty("id").GetString());
        AssertWorkspaceClean(workspace, "folmod");
        if (Directory.Exists(workspace.Combine(".fmstudio", "imports"))) Assert.Empty(Directory.GetDirectories(workspace.Combine(".fmstudio", "imports")));

        // A consumed session cannot be committed again.
        var again = await Commit(http, id);
        Assert.NotEqual(HttpStatusCode.Created, again.Status);
        Assert.Equal(4, Directory.GetFiles(workspace.Combine("folmod"), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Session_Commit_PublishesModsEvent()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var events = await http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(cts.Token));

        var id = await NewSession(http);
        AssertOk(await PutText(http, id, "info.json", InfoJson("evfolder")));
        Assert.Equal(HttpStatusCode.Created, (await Commit(http, id)).Status);

        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("stream ended");
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (JsonDocument.Parse(line[6..]).RootElement.GetProperty("type").GetString() == "mods") break;
        }
    }

    [Fact]
    public async Task Session_Commit_CreatesWorkspaceFolderIfMissing()
    {
        using var project = new TempDir();
        using var parent = new TempDir();
        var workspacePath = parent.Combine("nested", "ws");
        await using var ui = await UiServer.StartAsync(
            new ProjectContext(project.Path, new ProjectConfig(), configFileExists: false),
            new UiOptions(Port: 0, WorkspaceDirectory: workspacePath));
        using var http = Client(ui);

        var id = await NewSession(http);
        AssertOk(await PutText(http, id, "info.json", InfoJson("lazy")));
        Assert.Equal(HttpStatusCode.Created, (await Commit(http, id)).Status);

        Assert.True(File.Exists(Path.Combine(workspacePath, "lazy", "info.json")));
    }

    [Fact]
    public async Task Session_Commit_WithoutInfoJson_OrInvalidInfo_IsRejected_AndLeavesNoMod()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var noInfo = await NewSession(http);
        AssertOk(await PutText(http, noInfo, "data.lua", "-- x"));
        AssertError(await Commit(http, noInfo), HttpStatusCode.BadRequest);

        var badInfo = await NewSession(http);
        AssertOk(await PutText(http, badInfo, "info.json", InfoJson("bad.name")));
        AssertError(await Commit(http, badInfo), HttpStatusCode.BadRequest);

        var empty = await NewSession(http);
        AssertError(await Commit(http, empty), HttpStatusCode.BadRequest);

        await Cancel(http, noInfo);
        await Cancel(http, badInfo);
        await Cancel(http, empty);
        AssertWorkspaceClean(workspace);
        Assert.Empty(await Mods(http));
    }

    [Fact]
    public async Task Session_Commit_DuplicateName_Returns409_AndKeepsOriginal()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var a = await NewSession(http);
        AssertOk(await PutText(http, a, "info.json", InfoJson("sdup")));
        AssertOk(await PutText(http, a, "data.lua", "original"));
        var first = await Commit(http, a);
        Assert.Equal(HttpStatusCode.Created, first.Status);

        var b = await NewSession(http);
        AssertOk(await PutText(http, b, "info.json", InfoJson("sdup")));
        AssertOk(await PutText(http, b, "data.lua", "REPLACED"));
        AssertOk(await PutText(http, b, "extra.lua", "merged"));
        var second = await Commit(http, b);

        AssertError(second, HttpStatusCode.Conflict);
        Assert.Equal(first.Str("id"), second.Str("existing"));
        Assert.Equal("original", File.ReadAllText(workspace.Combine("sdup", "data.lua")));
        Assert.False(File.Exists(workspace.Combine("sdup", "extra.lua")));
        await Cancel(http, b);
        AssertWorkspaceClean(workspace, "sdup");
    }

    [Fact]
    public async Task Session_ConcurrentCommitsOfSameName_ExactlyOneWins()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var ids = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var id = await NewSession(http);
            AssertOk(await PutText(http, id, "info.json", InfoJson("crace")));
            AssertOk(await PutText(http, id, "data.lua", $"v{i}"));
            AssertOk(await PutText(http, id, $"only{i}.lua", ""));
            ids.Add(id);
        }

        var results = await Task.WhenAll(ids.Select(id => Commit(http, id)));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.Status == HttpStatusCode.Conflict));
        var winner = Enumerable.Range(0, 6).Single(i => results[i].Status == HttpStatusCode.Created);
        Assert.Equal($"v{winner}", File.ReadAllText(workspace.Combine("crace", "data.lua")));
        Assert.Equal(3, Directory.GetFiles(workspace.Combine("crace")).Length);
        foreach (var id in ids) await Cancel(http, id);
        AssertWorkspaceClean(workspace, "crace");
    }

    [Fact]
    public async Task Session_Cancel_RemovesStaging_AndInvalidatesTheId()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var id = await NewSession(http);
        AssertOk(await PutText(http, id, "info.json", InfoJson("cancelled")));
        AssertOk(await PutText(http, id, "data.lua", "x"));
        Assert.True(Directory.Exists(workspace.Combine(".fmstudio", "imports", id)));

        AssertOk(await Cancel(http, id));

        Assert.False(Directory.Exists(workspace.Combine(".fmstudio", "imports", id)));
        Assert.Equal(HttpStatusCode.NotFound, (await PutText(http, id, "late.lua", "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Commit(http, id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Cancel(http, id)).Status);
        AssertWorkspaceClean(workspace);
        Assert.Empty(await Mods(http));
    }

    [Fact]
    public async Task Session_UnknownId_Returns404_ForAllOperations()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var unknown = Guid.NewGuid().ToString("N");

        AssertError(await PutText(http, unknown, "info.json", InfoJson("x")), HttpStatusCode.NotFound);
        AssertError(await Commit(http, unknown), HttpStatusCode.NotFound);
        AssertError(await Cancel(http, unknown), HttpStatusCode.NotFound);
        AssertWorkspaceClean(workspace);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    [InlineData("..%2F..%2Fescape")]
    [InlineData("..%5C..%5Cescape")]
    [InlineData("%00")]
    public async Task Session_TraversalLikeId_NeverTouchesAnythingOutsideStaging(string id)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        using var canary = new Canary(workspace);
        workspace.Write("victim/info.json", InfoJson("victim"));
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        var del = await Read(await http.DeleteAsync($"/api/workspace/imports/{id}"));
        var commit = await Read(await http.PostAsync($"/api/workspace/imports/{id}/commit", null));
        var put = await Read(await http.PutAsync($"/api/workspace/imports/{id}/file?path=a.lua", new ByteArrayContent(new byte[] { 1 })));

        foreach (var r in new[] { del, commit, put }) Assert.True((int)r.Status is >= 400 and < 500, $"expected 4xx, got {(int)r.Status}");
        Assert.True(File.Exists(workspace.Combine("victim", "info.json")));
        Assert.True(Directory.Exists(workspace.Path));
        canary.AssertIntact(workspace);
    }

    [Theory]
    [InlineData("../EVIL.lua")]
    [InlineData("..\\EVIL.lua")]
    [InlineData("sub/../../EVIL.lua")]
    [InlineData("sub\\..\\..\\EVIL.lua")]
    [InlineData("/EVIL.lua")]
    [InlineData("\\EVIL.lua")]
    [InlineData("C:/EVIL.lua")]
    [InlineData("C:\\EVIL.lua")]
    [InlineData("C:EVIL.lua")]
    [InlineData("EVIL\0.lua")]
    [InlineData("CON.lua")]
    [InlineData("sub/prn/EVIL.lua")]
    [InlineData("NUL")]
    [InlineData(".hidden/EVIL.lua")]
    [InlineData("sub/.EVIL.lua")]
    [InlineData("")]
    [InlineData("sub/")]
    [InlineData("a//EVIL.lua")]
    public async Task Session_Put_UnsafePath_IsRejected(string path)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        using var canary = new Canary(workspace);
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await NewSession(http);

        var r = await PutText(http, id, path.Replace("EVIL", canary.Marker), "owned");

        AssertError(r, HttpStatusCode.BadRequest);
        canary.AssertIntact(workspace);
        // The session survives a rejected file and still commits cleanly.
        AssertOk(await PutText(http, id, "info.json", InfoJson("afterbad")));
        var commit = await Commit(http, id);
        Assert.Equal(HttpStatusCode.Created, commit.Status);
        Assert.Equal(1, commit.Json.GetProperty("files").GetInt32());
        canary.AssertIntact(workspace);
    }

    [Fact]
    public async Task Session_Put_MissingPathParameter_IsRejected()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await NewSession(http);

        var r = await Read(await http.PutAsync($"/api/workspace/imports/{id}/file", new ByteArrayContent(new byte[] { 1 })));

        AssertError(r, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("evil.exe")]
    [InlineData("lib/native.dll")]
    [InlineData("run.sh")]
    [InlineData("page.html")]
    [InlineData("pic.svg")]
    [InlineData("nested.zip")]
    [InlineData("noextension")]
    [InlineData("double.lua.exe")]
    public async Task Session_Put_DisallowedExtension_Returns415(string path)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await NewSession(http);

        var r = await PutText(http, id, path, "MZ");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Str("error")));
        AssertOk(await PutText(http, id, "info.json", InfoJson("noexe")));
        var commit = await Commit(http, id);
        Assert.Equal(HttpStatusCode.Created, commit.Status);
        Assert.Equal(1, commit.Json.GetProperty("files").GetInt32());
        Assert.Equal(new[] { "info.json" }, Directory.GetFiles(workspace.Combine("noexe"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData("trailingdot.")]
    [InlineData("script.lua.")]
    [InlineData("script.lua ")]
    public async Task Session_Put_TrailingDotOrSpace_IsNotAccepted(string path)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await NewSession(http);

        var r = await PutText(http, id, path, "x");

        Assert.True((int)r.Status is >= 400 and < 500, $"got {(int)r.Status}");
        await Cancel(http, id);
    }

    [Theory]
    [InlineData("a.lua")]
    [InlineData("a.cfg")]
    [InlineData("a.json")]
    [InlineData("a.txt")]
    [InlineData("a.md")]
    [InlineData("a.csv")]
    [InlineData("a.png")]
    [InlineData("a.jpg")]
    [InlineData("a.jpeg")]
    [InlineData("a.ogg")]
    [InlineData("a.wav")]
    [InlineData("a.mp3")]
    [InlineData("a.flac")]
    public async Task Session_Put_AllowedExtensions_AreAccepted(string path)
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await NewSession(http);

        AssertOk(await PutFile(http, id, path, new byte[] { 1, 2, 3 }));
        await Cancel(http, id);
    }

    [Fact]
    public async Task Session_Put_FileCap_50MiBAccepted_OneByteMoreIs413()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await NewSession(http);

        var bigRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/workspace/imports/{id}/file?path=huge.lua") { Content = new ByteArrayContent(new byte[50 * MiB + 1]) };
        bigRequest.Headers.ExpectContinue = true;
        var tooBig = await Read(await http.SendAsync(bigRequest));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.Status);
        Assert.False(File.Exists(workspace.Combine(".fmstudio", "imports", id, "huge.lua")));

        AssertOk(await PutFile(http, id, "exact.lua", new byte[50 * MiB]));
        AssertOk(await PutText(http, id, "info.json", InfoJson("capmod")));
        var commit = await Commit(http, id);
        Assert.Equal(HttpStatusCode.Created, commit.Status);
        Assert.False(File.Exists(workspace.Combine("capmod", "huge.lua")));
        Assert.Equal(50 * MiB, new FileInfo(workspace.Combine("capmod", "exact.lua")).Length);
    }

    [Fact]
    public async Task Session_MoreThan20000Files_IsRejected_AndNoModAppears()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        var id = await NewSession(http);
        AssertOk(await PutText(http, id, "info.json", InfoJson("lots")));

        var failedPut = 0;
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(Enumerable.Range(0, 20000).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                var r = await PutText(http, id, $"d{i % 100}/f{i}.lua", "");
                if ((int)r.Status >= 400) Interlocked.Increment(ref failedPut);
            }
            finally { gate.Release(); }
        })); // 20001 files in total including info.json

        var commit = await Commit(http, id);
        Assert.True(failedPut > 0 || commit.Status == HttpStatusCode.BadRequest || commit.Status == HttpStatusCode.RequestEntityTooLarge,
            $"20001 files accepted: commit gave {(int)commit.Status}");
        await Cancel(http, id);
        if (commit.Status != HttpStatusCode.Created)
        {
            Assert.False(Directory.Exists(workspace.Combine("lots")));
            AssertWorkspaceClean(workspace);
        }
    }

    // ------------------------------------------------------------------ auth

    [Fact]
    public async Task AllImportEndpoints_RequireToken_AndRejectForeignOrigin()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);
        using var anonymous = Client(ui, withToken: false);
        var id = await NewSession(http);
        AssertOk(await PutText(http, id, "info.json", InfoJson("authmod")));

        // No token.
        Assert.Equal(HttpStatusCode.Forbidden, (await Read(await anonymous.PostAsync("/api/workspace/import-zip", new ByteArrayContent(Zip(Info("authmod")))))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Read(await anonymous.PostAsync("/api/workspace/imports", null))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutText(anonymous, id, "x.lua", "x")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Commit(anonymous, id)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cancel(anonymous, id)).Status);

        // Wrong token.
        using var wrong = new HttpClient { BaseAddress = new Uri(ui.Url) };
        wrong.DefaultRequestHeaders.Add("X-FMS-Token", "nope");
        Assert.Equal(HttpStatusCode.Forbidden, (await Commit(wrong, id)).Status);

        // Foreign origin with a valid token.
        async Task<HttpStatusCode> WithEvilOrigin(HttpMethod method, string url, HttpContent? content)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Forbidden, await WithEvilOrigin(HttpMethod.Post, "/api/workspace/import-zip", new ByteArrayContent(Zip(Info("authmod")))));
        Assert.Equal(HttpStatusCode.Forbidden, await WithEvilOrigin(HttpMethod.Post, "/api/workspace/imports", null));
        Assert.Equal(HttpStatusCode.Forbidden, await WithEvilOrigin(HttpMethod.Put, $"/api/workspace/imports/{id}/file?path=x.lua", new ByteArrayContent(new byte[] { 1 })));
        Assert.Equal(HttpStatusCode.Forbidden, await WithEvilOrigin(HttpMethod.Post, $"/api/workspace/imports/{id}/commit", null));
        Assert.Equal(HttpStatusCode.Forbidden, await WithEvilOrigin(HttpMethod.Delete, $"/api/workspace/imports/{id}", null));

        // Nothing was created or cancelled by the rejected calls; the session still works.
        Assert.False(Directory.Exists(workspace.Combine("authmod")));
        Assert.False(File.Exists(workspace.Combine(".fmstudio", "imports", id, "x.lua")));
        Assert.Equal(HttpStatusCode.Created, (await Commit(http, id)).Status);
    }

    // ------------------------------------------------------------------ failure hygiene

    [Fact]
    public async Task FailedImports_LeaveNoPartialFolderStagingOrEscapedFiles()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        using var canary = new Canary(workspace);
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        // File 1 is fine, file 2 (after it in the archive) is hostile -> everything must be rolled back.
        var late = await PostZip(http, Zip(Info("late"), ("a/ok1.lua", "1"), ("a/ok2.lua", "2"), ("../" + canary.Marker + ".lua", "x"), ("z.lua", "3")));
        AssertError(late, HttpStatusCode.BadRequest);

        // Valid info but the archive is cut off in the middle of the data.
        var big = Zip(Info("cut"), ("data.lua", string.Join("\n", Enumerable.Range(0, 20000).Select(i => "local x" + i + " = " + i))));
        var cut = await PostZip(http, big.Take(big.Length * 2 / 3).ToArray());
        AssertError(cut, HttpStatusCode.BadRequest);

        Assert.False(Directory.Exists(workspace.Combine("late")));
        Assert.False(Directory.Exists(workspace.Combine("cut")));
        AssertWorkspaceClean(workspace);
        Assert.Empty(await Mods(http));
        canary.AssertIntact(workspace);

        // Neither name is poisoned.
        Assert.Equal(HttpStatusCode.Created, (await PostZip(http, Zip(Info("late"), ("data.lua", "")))).Status);
        Assert.Equal(HttpStatusCode.Created, (await PostZip(http, Zip(Info("cut"), ("data.lua", "")))).Status);
    }

    [Fact]
    public async Task Imports_DoNotDisturbExistingMods()
    {
        using var project = new TempDir();
        using var workspace = new TempDir();
        workspace.Write("old/info.json", InfoJson("old"));
        workspace.Write("old/data.lua", "keep");
        await using var ui = await Start(project, workspace);
        using var http = Client(ui);

        Assert.Equal(HttpStatusCode.Created, (await PostZip(http, Zip(Info("fresh2")))).Status);
        AssertError(await PostZip(http, Zip(Info("old"), ("data.lua", "x"))), HttpStatusCode.Conflict);
        AssertError(await PostZip(http, Zip(Info("bad.one"))), HttpStatusCode.BadRequest);

        Assert.Equal("keep", File.ReadAllText(workspace.Combine("old", "data.lua")));
        var names = (await Mods(http)).Select(m => m.GetProperty("name").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "fresh2", "old" }, names);
    }
}
