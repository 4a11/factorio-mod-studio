using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>
/// Black-box tests of the Files tab v2 endpoints (fs/mkdir, fs/move, fs/copy, DELETE file + trash, restore, PUT asset, reveal),
/// written from the contract only.
///
/// Reveal seam: <c>UiOptions.FileManager</c> (<c>FileManagerLauncher(string executable, IReadOnlyList&lt;string&gt; arguments)</c>)
/// replaces the real process start; <see cref="Env.Create"/> records each call as a <see cref="ProcessStartInfo"/>.
/// </summary>
public class FilesApiTests
{
    private const string Mod = "wmod";      // editable workspace mod
    private const string Other = "other";   // second editable workspace mod (trash isolation)
    private const string Ro = "ro";         // read-only unpacked installed mod
    private const string Zipped = "zipped"; // read-only zip installed mod

    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };
    private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0 };
    private static readonly byte[] OggMagic = Encoding.ASCII.GetBytes("OggS\0\u0002\0\0\0\0\0\0\0\0");
    private static readonly byte[] WavMagic = Encoding.ASCII.GetBytes("RIFF\u0024\0\0\0WAVEfmt ");

    private static string InfoFor(string name) =>
        $$"""{ "name": "{{name}}", "version": "1.0.0", "title": "T", "author": "a", "factorio_version": "2.0", "dependencies": ["base"] }""";

    private sealed record Resp(HttpStatusCode Status, string Raw)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement.Clone();
        public string? Str(string p) => Json.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public int Code => (int)Status;
        public override string ToString() => $"{Code} {Raw}";
    }

    private sealed class Env : IDisposable
    {
        public TempDir Project = new();
        public TempDir Workspace = new();
        public TempDir Installed = new();
        public TempDir Outside = new();
        public RunningUi Ui = null!;
        public HttpClient Http = null!;
        public List<ProcessStartInfo> Started = new();

        public string ModDir => Workspace.Combine(Mod);
        public string ModFile(string rel) => Path.Combine(ModDir, rel.Replace('/', Path.DirectorySeparatorChar));
        public string TrashRoot => Workspace.Combine(".fmstudio", "trash");

        public static async Task<Env> Create(Action<Env>? seed = null)
        {
            var env = new Env();
            env.Workspace.Write($"{Mod}/info.json", InfoFor(Mod));
            env.Workspace.Write($"{Mod}/data.lua", "data:extend({})\n");
            env.Workspace.Write($"{Mod}/notes.txt", "hello");
            env.Workspace.Write($"{Mod}/dir/inner.txt", "inner");
            env.Workspace.Write($"{Mod}/dir/deep/leaf.lua", "return 1\n");
            env.Workspace.Write($"{Other}/info.json", InfoFor(Other));
            env.Workspace.Write($"{Other}/data.lua", "\n");
            env.Installed.Write($"{Ro}_1.0.0/info.json", InfoFor(Ro));
            env.Installed.Write($"{Ro}_1.0.0/data.lua", "\n");
            env.Installed.Write($"{Ro}_1.0.0/graphics/x.png", "png");
            Fixtures.WriteModZip(env.Installed.Path, Zipped, "1.0.0", "base");
            env.Outside.Write("secret.txt", "top-secret");
            seed?.Invoke(env);

            env.Ui = await UiServer.StartAsync(
                new ProjectContext(env.Project.Path, new ProjectConfig(), configFileExists: false),
                new UiOptions(Port: 0, ExtraModFolders: new[] { env.Installed.Path }, WorkspaceDirectory: env.Workspace.Path,
                    FileManager: (exe, args) =>
                    {
                        var psi = new ProcessStartInfo(exe);
                        foreach (var a in args) psi.ArgumentList.Add(a);
                        lock (env.Started) env.Started.Add(psi);
                    }));
            env.Http = Client(env.Ui);
            env.Http.Timeout = TimeSpan.FromSeconds(120);
            return env;
        }

        public async Task<string> Id(string name)
        {
            var text = await Http.GetStringAsync("/api/mods");
            return JsonDocument.Parse(text).RootElement.EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
        }

        public void Dispose()
        {
            try { Http?.Dispose(); } catch { }
            try { Ui?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            Project.Dispose(); Workspace.Dispose(); Installed.Dispose(); Outside.Dispose();
        }
    }

    private static HttpClient Client(RunningUi ui, bool token = true)
    {
        var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        if (token) http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);
        return http;
    }

    private static string Q(string path) => Uri.EscapeDataString(path);

    private static async Task<Resp> Send(HttpClient http, HttpMethod method, string url, HttpContent? content = null, Action<HttpRequestMessage>? tweak = null)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        tweak?.Invoke(req);
        using var res = await http.SendAsync(req);
        return new Resp(res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    private static HttpContent JsonBody(object o) => new StringContent(JsonSerializer.Serialize(o), new UTF8Encoding(false), "application/json");

    private static Task<Resp> Mkdir(HttpClient http, string id, string path, Action<HttpRequestMessage>? t = null) =>
        Send(http, HttpMethod.Post, $"/api/mods/{id}/fs/mkdir?path={Q(path)}", JsonBody(new { }), t);

    private static Task<Resp> Move(HttpClient http, string id, string from, string to, Action<HttpRequestMessage>? t = null) =>
        Send(http, HttpMethod.Post, $"/api/mods/{id}/fs/move", JsonBody(new { from, to }), t);

    private static Task<Resp> Copy(HttpClient http, string id, string from, string to, Action<HttpRequestMessage>? t = null) =>
        Send(http, HttpMethod.Post, $"/api/mods/{id}/fs/copy", JsonBody(new { from, to }), t);

    private static Task<Resp> Delete(HttpClient http, string id, string path, Action<HttpRequestMessage>? t = null) =>
        Send(http, HttpMethod.Delete, $"/api/mods/{id}/file?path={Q(path)}", null, t);

    private static Task<Resp> Trash(HttpClient http, string id) => Send(http, HttpMethod.Get, $"/api/mods/{id}/trash");

    private static Task<Resp> Restore(HttpClient http, string id, string trashId, Action<HttpRequestMessage>? t = null) =>
        Send(http, HttpMethod.Post, $"/api/mods/{id}/trash/{trashId}/restore", JsonBody(new { }), t);

    private static Task<Resp> Reveal(HttpClient http, string id, string? path, Action<HttpRequestMessage>? t = null) =>
        Send(http, HttpMethod.Post, path is null ? $"/api/mods/{id}/reveal" : $"/api/mods/{id}/reveal?path={Q(path)}", JsonBody(new { }), t);

    private static Task<Resp> PutAsset(HttpClient http, string id, string path, byte[] body, string? ifMatch = null, string? ifNoneMatch = null, Action<HttpRequestMessage>? t = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return Send(http, HttpMethod.Put, $"/api/mods/{id}/asset?path={Q(path)}", content, req =>
        {
            if (ifMatch != null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (ifNoneMatch != null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            t?.Invoke(req);
        });
    }

    private static byte[] Bytes(byte[] magic, int total = 64)
    {
        var b = new byte[Math.Max(total, magic.Length)];
        magic.CopyTo(b, 0);
        for (var i = magic.Length; i < b.Length; i++) b[i] = (byte)(i * 7);
        return b;
    }

    private static List<JsonElement> Entries(Resp r) => r.Json.EnumerateArray().Select(e => e.Clone()).ToList();

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p), p => File.Exists(p) ? Convert.ToBase64String(File.ReadAllBytes(p)) : "<dir>");

    private static Dictionary<string, string> ModSnapshot(Env e) => Snapshot(e.ModDir);

    private static void Is(HttpStatusCode expected, Resp r) =>
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {r}");

    private static void AssertBadPath(Resp r, string what) =>
        Assert.True(r.Status == HttpStatusCode.BadRequest, $"{what}: expected 400, got {r}");

    /// <summary>Attack paths that must be rejected (400) by every write endpoint before anything else happens.</summary>
    private static IEnumerable<string> AttackPaths(Env e)
    {
        yield return "../escaped.txt";
        yield return "a/../../escaped.txt";
        yield return "dir/../../escaped.txt";
        yield return "..\\escaped.txt";
        yield return e.Outside.Combine("abs.txt");                                     // absolute, host style
        yield return OperatingSystem.IsWindows() ? "C:\\Windows\\fms-abs.txt" : "/tmp/fms-abs.txt";
        yield return "/rooted.txt";
        yield return ".git/config.txt";
        yield return ".hidden.txt";
        yield return "dir/.secret/x.txt";
        yield return "dir/.x.txt";
    }

    // ================================================================== mkdir

    [Fact]
    public async Task Mkdir_CreatesFolderWithParents_AndReturnsPath()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Mkdir(e.Http, id, "new/nested/folder");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("new/nested/folder", r.Str("path"));
        Assert.True(Directory.Exists(e.ModFile("new/nested/folder")));
    }

    [Fact]
    public async Task Mkdir_ConflictsWithExistingFolderOrFile()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        Assert.Equal(HttpStatusCode.Conflict, (await Mkdir(e.Http, id, "dir")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Mkdir(e.Http, id, "notes.txt")).Status);
        Assert.Equal("hello", File.ReadAllText(e.ModFile("notes.txt")));
    }

    [Fact]
    public async Task Mkdir_RejectsBadPaths_AndCreatesNothing()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var before = ModSnapshot(e);
        var outsideBefore = Snapshot(e.Workspace.Path).Count;

        foreach (var attack in AttackPaths(e).Append(""))
        {
            var r = await Mkdir(e.Http, id, attack);
            AssertBadPath(r, "mkdir " + attack);
        }

        Assert.Equal(before, ModSnapshot(e));
        Assert.False(File.Exists(e.Workspace.Combine("escaped.txt")) || Directory.Exists(e.Workspace.Combine("escaped.txt")));
        Assert.False(Directory.Exists(e.Outside.Combine("abs.txt")));
        Assert.Equal(outsideBefore, Snapshot(e.Workspace.Path).Count);
    }

    // ================================================================== move

    [Fact]
    public async Task Move_File_CreatesTargetParents_AndReturnsFromTo()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Move(e.Http, id, "notes.txt", "docs/sub/renamed.txt");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("notes.txt", r.Str("from"));
        Assert.Equal("docs/sub/renamed.txt", r.Str("to"));
        Assert.False(File.Exists(e.ModFile("notes.txt")));
        Assert.Equal("hello", File.ReadAllText(e.ModFile("docs/sub/renamed.txt")));
    }

    [Fact]
    public async Task Move_Folder_MovesItsContents()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Move(e.Http, id, "dir", "moved/dir2");
        Is(HttpStatusCode.OK, r);
        Assert.False(Directory.Exists(e.ModFile("dir")));
        Assert.Equal("inner", File.ReadAllText(e.ModFile("moved/dir2/inner.txt")));
        Assert.Equal("return 1\n", File.ReadAllText(e.ModFile("moved/dir2/deep/leaf.lua")));
    }

    [Fact]
    public async Task Move_FolderToSiblingWithCommonPrefix_IsAllowed()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        // "dir2" starts with "dir" but is not inside it.
        Assert.Equal(HttpStatusCode.OK, (await Move(e.Http, id, "dir", "dir2")).Status);
        Assert.True(File.Exists(e.ModFile("dir2/inner.txt")));
    }

    [Fact]
    public async Task Move_ErrorCodes()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var before = ModSnapshot(e);

        Assert.Equal(HttpStatusCode.NotFound, (await Move(e.Http, id, "missing.txt", "x.txt")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Move(e.Http, id, "notes.txt", "data.lua")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Move(e.Http, id, "notes.txt", "dir")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(e.Http, id, "dir", "dir/inside")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(e.Http, id, "dir", "dir/deep/further")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(e.Http, id, "notes.txt", "notes.txt")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(e.Http, id, "dir", "dir")).Status);

        Assert.Equal(before, ModSnapshot(e));
    }

    [Fact]
    public async Task Move_IntoItself_IsRejectedEvenWithDifferentCaseOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        Assert.Equal(HttpStatusCode.BadRequest, (await Move(e.Http, id, "dir", "DIR/inside")).Status);
        Assert.True(File.Exists(e.ModFile("dir/inner.txt")));
    }

    [Fact]
    public async Task Move_CaseOnlyRename_IsAllowed()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Move(e.Http, id, "notes.txt", "Notes.txt");
        Is(HttpStatusCode.OK, r);
        Assert.Contains("Notes.txt", Directory.GetFiles(e.ModDir).Select(Path.GetFileName));
        Assert.DoesNotContain("notes.txt", Directory.GetFiles(e.ModDir).Select(Path.GetFileName));

        var folder = await Move(e.Http, id, "dir", "Dir");
        Is(HttpStatusCode.OK, folder);
        Assert.Contains("Dir", Directory.GetDirectories(e.ModDir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Move_RejectsBadPathsOnEitherSide()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var before = ModSnapshot(e);
        e.Workspace.Write("victim.txt", "victim");

        foreach (var attack in AttackPaths(e))
        {
            AssertBadPath(await Move(e.Http, id, "notes.txt", attack), "move to " + attack);
            AssertBadPath(await Move(e.Http, id, attack, "stolen.txt"), "move from " + attack);
        }
        AssertBadPath(await Move(e.Http, id, "../victim.txt", "stolen.txt"), "move victim");
        AssertBadPath(await Move(e.Http, id, "", "x"), "move root");

        Assert.Equal(before, ModSnapshot(e));
        Assert.Equal("victim", File.ReadAllText(e.Workspace.Combine("victim.txt")));
        Assert.Equal("top-secret", File.ReadAllText(e.Outside.Combine("secret.txt")));
    }

    [Fact]
    public async Task Move_RejectsMalformedBodies()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        foreach (var body in new[] { "", "not json", "[]", "{}", "{\"from\":\"notes.txt\"}", "{\"from\":1,\"to\":2}" })
        {
            var r = await Send(e.Http, HttpMethod.Post, $"/api/mods/{id}/fs/move", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.True(r.Status == HttpStatusCode.BadRequest, $"body {body}: {r}");
        }
        Assert.True(File.Exists(e.ModFile("notes.txt")));
    }

    // ================================================================== copy

    [Fact]
    public async Task Copy_File_CreatesParents_AndKeepsSource()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Copy(e.Http, id, "notes.txt", "copies/notes-copy.txt");
        Is(HttpStatusCode.OK, r);
        Assert.Equal("hello", File.ReadAllText(e.ModFile("notes.txt")));
        Assert.Equal("hello", File.ReadAllText(e.ModFile("copies/notes-copy.txt")));
    }

    [Fact]
    public async Task Copy_ErrorCodes()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var before = ModSnapshot(e);

        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(e.Http, id, "dir", "dir-copy")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Copy(e.Http, id, "notes.txt", "data.lua")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Copy(e.Http, id, "notes.txt", "dir")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Copy(e.Http, id, "missing.txt", "x.txt")).Status);

        Assert.Equal(before, ModSnapshot(e));
        Assert.False(Directory.Exists(e.ModFile("dir-copy")));
    }

    [Fact]
    public async Task Copy_RejectsBadPathsOnEitherSide()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var before = ModSnapshot(e);
        var secret = e.Outside.Combine("secret.txt");

        foreach (var attack in AttackPaths(e).Append(secret))
        {
            AssertBadPath(await Copy(e.Http, id, "notes.txt", attack), "copy to " + attack);
            AssertBadPath(await Copy(e.Http, id, attack, "stolen.txt"), "copy from " + attack);
        }

        Assert.Equal(before, ModSnapshot(e));
        Assert.False(File.Exists(e.ModFile("stolen.txt")));
    }

    // ================================================================== delete / trash / restore

    [Fact]
    public async Task Delete_File_MovesItToWorkspaceTrash_WithManifest()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Delete(e.Http, id, "notes.txt");
        Is(HttpStatusCode.OK, r);
        var trashId = r.Str("trashId");
        Assert.False(string.IsNullOrEmpty(trashId));
        Assert.False(File.Exists(e.ModFile("notes.txt")));

        // Stored under <workspace>/.fmstudio/trash/<mod>/<trashId>/ with a manifest; nothing left inside the mod.
        Assert.True(Directory.Exists(e.TrashRoot), "trash root missing");
        var entryDir = Directory.EnumerateDirectories(e.TrashRoot, trashId!, SearchOption.AllDirectories).SingleOrDefault();
        Assert.NotNull(entryDir);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(entryDir!, "manifest.json")))!;
        Assert.Equal(trashId, (string?)manifest["trashId"]);
        Assert.Equal("notes.txt", (string?)manifest["path"]);
        Assert.Equal("file", (string?)manifest["kind"]);
        Assert.NotNull(manifest["deletedUtc"]);
        Assert.Contains(Directory.EnumerateFiles(entryDir!, "*", SearchOption.AllDirectories), f => File.ReadAllText(f) == "hello");
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(e.ModDir, "*", SearchOption.AllDirectories), p => p.Contains("trash", StringComparison.OrdinalIgnoreCase));

        var list = Entries(await Trash(e.Http, id));
        var item = Assert.Single(list);
        Assert.Equal(trashId, item.GetProperty("trashId").GetString());
        Assert.Equal("notes.txt", item.GetProperty("path").GetString());
        Assert.Equal("file", item.GetProperty("kind").GetString());
        Assert.True(item.TryGetProperty("deletedUtc", out _));
    }

    [Fact]
    public async Task Delete_Folder_AndRestore_RoundTrips()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var del = await Delete(e.Http, id, "dir");
        Is(HttpStatusCode.OK, del);
        Assert.False(Directory.Exists(e.ModFile("dir")));
        var item = Assert.Single(Entries(await Trash(e.Http, id)));
        Assert.Equal("dir", item.GetProperty("path").GetString());
        Assert.NotEqual("file", item.GetProperty("kind").GetString());

        var res = await Restore(e.Http, id, del.Str("trashId")!);
        Is(HttpStatusCode.OK, res);
        Assert.Equal("dir", res.Str("path"));
        Assert.Equal("inner", File.ReadAllText(e.ModFile("dir/inner.txt")));
        Assert.Equal("return 1\n", File.ReadAllText(e.ModFile("dir/deep/leaf.lua")));
        Assert.Empty(Entries(await Trash(e.Http, id)));
    }

    [Fact]
    public async Task Restore_File_RecreatesMissingParents_AndRemovesEntry()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var del = await Delete(e.Http, id, "dir/deep/leaf.lua");
        Assert.Equal(HttpStatusCode.OK, del.Status);
        Directory.Delete(e.ModFile("dir"), recursive: true);

        var res = await Restore(e.Http, id, del.Str("trashId")!);
        Is(HttpStatusCode.OK, res);
        Assert.Equal("dir/deep/leaf.lua", res.Str("path"));
        Assert.Equal("return 1\n", File.ReadAllText(e.ModFile("dir/deep/leaf.lua")));
        Assert.Empty(Entries(await Trash(e.Http, id)));

        // A second restore of the same id is unknown now.
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(e.Http, id, del.Str("trashId")!)).Status);
    }

    [Fact]
    public async Task Restore_ConflictsWhenOriginalPathIsOccupied_AndKeepsEntry()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var del = await Delete(e.Http, id, "notes.txt");
        File.WriteAllText(e.ModFile("notes.txt"), "new content");

        Assert.Equal(HttpStatusCode.Conflict, (await Restore(e.Http, id, del.Str("trashId")!)).Status);
        Assert.Equal("new content", File.ReadAllText(e.ModFile("notes.txt")));
        Assert.Single(Entries(await Trash(e.Http, id)));
    }

    [Fact]
    public async Task Delete_ErrorCodes()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var before = ModSnapshot(e);

        Assert.Equal(HttpStatusCode.NotFound, (await Delete(e.Http, id, "missing.txt")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Delete(e.Http, id, "")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(e.Http, HttpMethod.Delete, $"/api/mods/{id}/file")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Delete(e.Http, id, ".")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Delete(e.Http, id, "./")).Status);

        Assert.Equal(before, ModSnapshot(e));
        Assert.True(Directory.Exists(e.ModDir));
    }

    [Fact]
    public async Task Delete_RejectsBadPaths_AndHiddenFiles()
    {
        using var e = await Env.Create(env =>
        {
            env.Workspace.Write($"{Mod}/.hidden.txt", "h");
            env.Workspace.Write($"{Mod}/.git/config.txt", "g");
            env.Workspace.Write("escaped.txt", "victim");
        });
        var id = await e.Id(Mod);
        var before = Snapshot(e.Workspace.Path);

        foreach (var attack in AttackPaths(e).Append(e.Outside.Combine("secret.txt")))
            AssertBadPath(await Delete(e.Http, id, attack), "delete " + attack);

        Assert.Equal(before, Snapshot(e.Workspace.Path));
        Assert.Equal("top-secret", File.ReadAllText(e.Outside.Combine("secret.txt")));
    }

    [Fact]
    public async Task Trash_IsPerMod_AndNewestFirst()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var other = await e.Id(Other);

        var first = (await Delete(e.Http, id, "notes.txt")).Str("trashId");
        await Task.Delay(20);
        var second = (await Delete(e.Http, id, "data.lua")).Str("trashId");

        var list = Entries(await Trash(e.Http, id));
        Assert.Equal(new[] { second, first }, list.Select(x => x.GetProperty("trashId").GetString()).ToArray());
        Assert.Empty(Entries(await Trash(e.Http, other)));

        // Another mod cannot restore this mod's entry.
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(e.Http, other, first!)).Status);
        Assert.False(File.Exists(e.Workspace.Combine(Other, "notes.txt")));
    }

    [Fact]
    public async Task Restore_UnknownOrMalformedIds_AreRejectedWithoutTouchingFiles()
    {
        using var e = await Env.Create(env =>
        {
            // A decoy that looks like a trash entry, outside any trash folder.
            env.Workspace.Write("evil/manifest.json", """{ "trashId": "evil", "path": "stolen.txt", "kind": "file", "deletedUtc": "2026-01-01T00:00:00Z" }""");
            env.Workspace.Write("evil/stolen.txt", "pwned");
            env.Workspace.Write("evil/content/stolen.txt", "pwned");
        });
        var id = await e.Id(Mod);
        await Delete(e.Http, id, "notes.txt"); // make sure the trash folder exists
        var before = ModSnapshot(e);

        Assert.Equal(HttpStatusCode.NotFound, (await Restore(e.Http, id, Guid.NewGuid().ToString("N"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(e.Http, id, "unknown")).Status);

        foreach (var bad in new[] { "..", "%2E%2E", "..%2F..%2F..%2Fevil", "..%5C..%5C..%5Cevil", "%2E%2E%2F%2E%2E%2F%2E%2E%2Fevil", "a.b", "evil", "%00" })
        {
            var r = await Restore(e.Http, id, bad);
            Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"restore {bad}: {r}");
        }

        Assert.Equal(before, ModSnapshot(e));
        Assert.False(File.Exists(e.ModFile("stolen.txt")));
    }

    [Fact]
    public async Task Trash_RetainsOnlyTheNewest20Entries()
    {
        using var e = await Env.Create(env =>
        {
            for (var i = 0; i < 22; i++) env.Workspace.Write($"{Mod}/bulk/f{i:00}.txt", "x" + i);
        });
        var id = await e.Id(Mod);

        var ids = new List<string>();
        for (var i = 0; i < 22; i++)
        {
            var r = await Delete(e.Http, id, $"bulk/f{i:00}.txt");
            Is(HttpStatusCode.OK, r);
            ids.Add(r.Str("trashId")!);
            await Task.Delay(5);
        }

        var list = Entries(await Trash(e.Http, id));
        Assert.Equal(20, list.Count);
        var listed = list.Select(x => x.GetProperty("trashId").GetString()).ToList();
        Assert.Equal(ids.Skip(2).Reverse().ToList(), listed);
        Assert.DoesNotContain(ids[0], listed);
        Assert.DoesNotContain(ids[1], listed);
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(e.Http, id, ids[0])).Status);

        // Purged entries are gone from disk too.
        Assert.Empty(Directory.EnumerateDirectories(e.TrashRoot, ids[0], SearchOption.AllDirectories));
        Assert.Equal(20, Directory.EnumerateFiles(e.TrashRoot, "manifest.json", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Trash_PurgesEntriesOlderThanSevenDays_OnTheNextDelete()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var old = (await Delete(e.Http, id, "notes.txt")).Str("trashId")!;
        var manifestPath = Directory.EnumerateFiles(e.TrashRoot, "manifest.json", SearchOption.AllDirectories)
            .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == old);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var aged = DateTime.UtcNow.AddDays(-8);
        manifest["deletedUtc"] = aged.ToString("o");
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Directory.SetCreationTimeUtc(Path.GetDirectoryName(manifestPath)!, aged);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(manifestPath)!, aged);

        var fresh = (await Delete(e.Http, id, "data.lua")).Str("trashId")!;

        var listed = Entries(await Trash(e.Http, id)).Select(x => x.GetProperty("trashId").GetString()).ToList();
        Assert.Equal(new[] { fresh }, listed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(manifestPath)));
    }

    // ================================================================== asset upload

    [Theory]
    [InlineData("graphics/new.png", "png")]
    [InlineData("graphics/new.PNG", "png")]
    [InlineData("graphics/photo.jpg", "jpeg")]
    [InlineData("graphics/photo.jpeg", "jpeg")]
    [InlineData("sound/beep.ogg", "ogg")]
    [InlineData("sound/beep.wav", "wav")]
    public async Task Asset_Create_WithIfNoneMatchStar(string path, string kind)
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var body = Bytes(kind switch { "png" => PngMagic, "jpeg" => JpegMagic, "ogg" => OggMagic, _ => WavMagic }, 200);

        var r = await PutAsset(e.Http, id, path, body, ifNoneMatch: "*");
        Is(HttpStatusCode.OK, r);
        Assert.False(string.IsNullOrEmpty(r.Str("etag")));
        Assert.Equal(body.Length, r.Json.GetProperty("size").GetInt64());
        Assert.Equal(body, File.ReadAllBytes(e.ModFile(path)));
    }

    [Fact]
    public async Task Asset_CreateWithoutIfNoneMatch_Is428()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        Assert.Equal((HttpStatusCode)428, (await PutAsset(e.Http, id, "graphics/a.png", Bytes(PngMagic))).Status);
        Assert.False(File.Exists(e.ModFile("graphics/a.png")));
    }

    [Fact]
    public async Task Asset_Overwrite_NeedsMatchingIfMatch()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var v1 = Bytes(PngMagic, 100);
        var v2 = Bytes(PngMagic, 150);
        var v3 = Bytes(PngMagic, 170);

        var created = await PutAsset(e.Http, id, "graphics/a.png", v1, ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var etag1 = created.Str("etag")!;

        // Creating again over an existing file is not a blind overwrite.
        var again = await PutAsset(e.Http, id, "graphics/a.png", v2, ifNoneMatch: "*");
        Assert.True(again.Code is 409 or 412 or 428, again.ToString());
        Assert.Equal(v1, File.ReadAllBytes(e.ModFile("graphics/a.png")));

        Assert.Equal((HttpStatusCode)428, (await PutAsset(e.Http, id, "graphics/a.png", v2)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsset(e.Http, id, "graphics/a.png", v2, ifMatch: "\"stale-etag\"")).Status);
        Assert.Equal(v1, File.ReadAllBytes(e.ModFile("graphics/a.png")));

        var updated = await PutAsset(e.Http, id, "graphics/a.png", v2, ifMatch: etag1);
        Is(HttpStatusCode.OK, updated);
        Assert.Equal(v2, File.ReadAllBytes(e.ModFile("graphics/a.png")));
        var etag2 = updated.Str("etag")!;
        Assert.NotEqual(etag1, etag2);

        // The old etag is stale now.
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsset(e.Http, id, "graphics/a.png", v3, ifMatch: etag1)).Status);
        Assert.Equal(v2, File.ReadAllBytes(e.ModFile("graphics/a.png")));
    }

    [Fact]
    public async Task Asset_OverwriteOfFileChangedOnDisk_Conflicts()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var created = await PutAsset(e.Http, id, "graphics/a.png", Bytes(PngMagic, 100), ifNoneMatch: "*");
        var onDisk = Bytes(PngMagic, 333);
        File.WriteAllBytes(e.ModFile("graphics/a.png"), onDisk);

        Assert.Equal(HttpStatusCode.Conflict, (await PutAsset(e.Http, id, "graphics/a.png", Bytes(PngMagic, 120), ifMatch: created.Str("etag"))).Status);
        Assert.Equal(onDisk, File.ReadAllBytes(e.ModFile("graphics/a.png")));
    }

    [Theory]
    [InlineData("graphics/a.gif")]
    [InlineData("graphics/a.bmp")]
    [InlineData("sound/a.mp3")]
    [InlineData("data.lua")]
    [InlineData("notes.txt")]
    [InlineData("graphics/noext")]
    [InlineData("graphics/a.png.exe")]
    public async Task Asset_UnsupportedExtension_Is415(string path)
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var before = ModSnapshot(e);

        var r = await PutAsset(e.Http, id, path, Bytes(PngMagic), ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.Status);
        Assert.Equal(before, ModSnapshot(e));
    }

    [Theory]
    [InlineData("graphics/a.png", "jpeg")]
    [InlineData("graphics/a.png", "text")]
    [InlineData("graphics/a.png", "empty")]
    [InlineData("graphics/a.jpg", "png")]
    [InlineData("sound/a.ogg", "wav")]
    [InlineData("sound/a.wav", "ogg")]
    [InlineData("sound/a.wav", "riff-not-wave")]
    [InlineData("sound/a.ogg", "short")]
    public async Task Asset_MagicBytesMismatch_Is400(string path, string kind)
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var body = kind switch
        {
            "jpeg" => Bytes(JpegMagic),
            "png" => Bytes(PngMagic),
            "wav" => Bytes(WavMagic),
            "ogg" => Bytes(OggMagic),
            "text" => Encoding.UTF8.GetBytes("<html>definitely not a png</html>"),
            "empty" => Array.Empty<byte>(),
            "riff-not-wave" => Bytes(Encoding.ASCII.GetBytes("RIFF\u0024\0\0\0AVI LIST")),
            "short" => Encoding.ASCII.GetBytes("Og"),
            _ => throw new ArgumentException(kind)
        };

        var r = await PutAsset(e.Http, id, path, body, ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.False(File.Exists(e.ModFile(path)));
    }

    [Fact]
    public async Task Asset_LargerThan32MB_Is413_AndExactly32MB_IsAccepted()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        const int limit = 32 * 1024 * 1024;

        var tooBig = await PutAsset(e.Http, id, "graphics/big.png", Bytes(PngMagic, limit + 1), ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.Status);
        Assert.False(File.Exists(e.ModFile("graphics/big.png")));

        var ok = await PutAsset(e.Http, id, "graphics/max.png", Bytes(PngMagic, limit), ifNoneMatch: "*");
        Is(HttpStatusCode.OK, ok);
        Assert.Equal(limit, new FileInfo(e.ModFile("graphics/max.png")).Length);
    }

    [Fact]
    public async Task Asset_RejectsBadPaths()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var before = Snapshot(e.Workspace.Path);

        foreach (var attack in new[]
                 {
                     "../escaped.png", "a/../../escaped.png", "..\\escaped.png", e.Outside.Combine("abs.png"), "/rooted.png",
                     ".git/x.png", ".hidden.png", "graphics/.x/a.png", "graphics/.a.png", ""
                 })
        {
            var r = await PutAsset(e.Http, id, attack, Bytes(PngMagic), ifNoneMatch: "*");
            AssertBadPath(r, "asset " + attack);
        }

        Assert.Equal(before, Snapshot(e.Workspace.Path));
        Assert.False(File.Exists(e.Outside.Combine("abs.png")));
    }

    // ================================================================== reveal

    private static void AssertRevealCall(ProcessStartInfo psi, string full, bool select)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("explorer.exe", Path.GetFileName(psi.FileName), ignoreCase: true);
            Assert.Equal(new[] { select ? "/select," + full : full }, psi.ArgumentList.ToArray());
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("open", psi.FileName);
            Assert.Equal(new[] { "-R", full }, psi.ArgumentList.ToArray());
        }
        else
        {
            Assert.Equal("xdg-open", psi.FileName);
            Assert.Equal(new[] { select ? Path.GetDirectoryName(full)! : full }, psi.ArgumentList.ToArray());
        }
        Assert.True(string.IsNullOrEmpty(psi.Arguments), "use ArgumentList, not a joined argument string");
    }

    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    private static void AssertSamePathIn(ProcessStartInfo psi, string expectedFull, bool select)
    {
        // Compare normalised so a trailing separator or 8.3/long-name differences in the temp path don't matter.
        var normalised = new ProcessStartInfo(psi.FileName);
        foreach (var arg in psi.ArgumentList)
        {
            if (arg.StartsWith("/select,", StringComparison.Ordinal)) normalised.ArgumentList.Add("/select," + Norm(arg["/select,".Length..]));
            else if (arg.StartsWith('-')) normalised.ArgumentList.Add(arg);
            else normalised.ArgumentList.Add(Norm(arg));
        }
        AssertRevealCall(normalised, Norm(expectedFull), select);
    }

    [Fact]
    public async Task Reveal_File_SelectsItInTheFileManager()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Reveal(e.Http, id, "dir/inner.txt");
        Assert.Equal(HttpStatusCode.NoContent, r.Status);
        var psi = Assert.Single(e.Started);
        AssertSamePathIn(psi, e.ModFile("dir/inner.txt"), select: true);
    }

    [Fact]
    public async Task Reveal_Folder_OpensTheFolder()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        Assert.Equal(HttpStatusCode.NoContent, (await Reveal(e.Http, id, "dir")).Status);
        var psi = Assert.Single(e.Started);
        if (OperatingSystem.IsMacOS()) AssertSamePathIn(psi, e.ModFile("dir"), select: true);
        else AssertSamePathIn(psi, e.ModFile("dir"), select: false);
    }

    [Fact]
    public async Task Reveal_WithoutPath_OpensModRoot()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        Assert.Equal(HttpStatusCode.NoContent, (await Reveal(e.Http, id, null)).Status);
        var psi = Assert.Single(e.Started);
        if (OperatingSystem.IsMacOS()) AssertSamePathIn(psi, e.ModDir, select: true);
        else AssertSamePathIn(psi, e.ModDir, select: false);
    }

    [Fact]
    public async Task Reveal_IsAllowedForReadOnlyMods_AndRevealsTheZipOfAZipMod()
    {
        using var e = await Env.Create();

        var ro = await e.Id(Ro);
        Assert.Equal(HttpStatusCode.NoContent, (await Reveal(e.Http, ro, "graphics/x.png")).Status);
        AssertSamePathIn(Assert.Single(e.Started), e.Installed.Combine($"{Ro}_1.0.0", "graphics", "x.png"), select: true);

        e.Started.Clear();
        var zip = await e.Id(Zipped);
        Assert.Equal(HttpStatusCode.NoContent, (await Reveal(e.Http, zip, null)).Status);
        AssertSamePathIn(Assert.Single(e.Started), e.Installed.Combine($"{Zipped}_1.0.0.zip"), select: true);
    }

    [Fact]
    public async Task Reveal_RejectsBadPathsAndUnknownTargets_WithoutStartingAnything()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        foreach (var attack in new[] { "../", "..", "a/../../x", "..\\x", e.Outside.Path, e.Outside.Combine("secret.txt"), "/rooted" })
        {
            var r = await Reveal(e.Http, id, attack);
            Assert.True(r.Status == HttpStatusCode.BadRequest, $"reveal {attack}: {r}");
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Reveal(e.Http, id, "missing.txt")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Reveal(e.Http, "no-such-mod", null)).Status);
        Assert.Empty(e.Started);
    }

    [Fact]
    public async Task Reveal_IgnoresAnyClientSuppliedArguments()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Send(e.Http, HttpMethod.Post, $"/api/mods/{id}/reveal?path=notes.txt&exe=calc.exe&args=%2Fc%20calc",
            JsonBody(new { executable = "calc.exe", arguments = new[] { "/c", "calc" }, fileName = "cmd.exe" }));
        Assert.Equal(HttpStatusCode.NoContent, r.Status);
        var psi = Assert.Single(e.Started);
        AssertSamePathIn(psi, e.ModFile("notes.txt"), select: true);
        Assert.DoesNotContain(psi.ArgumentList, a => a.Contains("calc", StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================== read-only mods

    [Fact]
    public async Task WriteEndpoints_OnReadOnlyMods_Are403_AndChangeNothing()
    {
        using var e = await Env.Create();
        var before = Snapshot(e.Installed.Path);

        foreach (var name in new[] { Ro, Zipped })
        {
            var id = await e.Id(name);
            Assert.Equal(HttpStatusCode.Forbidden, (await Mkdir(e.Http, id, "newdir")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Move(e.Http, id, "data.lua", "moved.lua")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Copy(e.Http, id, "data.lua", "copy.lua")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Delete(e.Http, id, "data.lua")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await PutAsset(e.Http, id, "graphics/new.png", Bytes(PngMagic), ifNoneMatch: "*")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await PutAsset(e.Http, id, "graphics/x.png", Bytes(PngMagic), ifMatch: "\"x\"")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Restore(e.Http, id, Guid.NewGuid().ToString("N"))).Status);
        }

        Assert.Equal(before, Snapshot(e.Installed.Path));
        Assert.False(Directory.Exists(e.TrashRoot) && Directory.EnumerateFiles(e.TrashRoot, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task UnknownMod_Is404_ForEveryEndpoint()
    {
        using var e = await Env.Create();
        const string id = "no-such-mod";

        Assert.Equal(HttpStatusCode.NotFound, (await Mkdir(e.Http, id, "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Move(e.Http, id, "a", "b")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Copy(e.Http, id, "a", "b")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(e.Http, id, "a")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Trash(e.Http, id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(e.Http, id, Guid.NewGuid().ToString("N"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsset(e.Http, id, "a.png", Bytes(PngMagic), ifNoneMatch: "*")).Status);
    }

    // ================================================================== token / origin

    private delegate Task<Resp> Op(HttpClient http, string id, Action<HttpRequestMessage>? tweak);

    private static IEnumerable<(string Name, Op Op)> WriteOps(string trashId) => new (string, Op)[]
    {
        ("mkdir", (h, id, t) => Mkdir(h, id, "made", t)),
        ("move", (h, id, t) => Move(h, id, "notes.txt", "moved.txt", t)),
        ("copy", (h, id, t) => Copy(h, id, "notes.txt", "copied.txt", t)),
        ("delete", (h, id, t) => Delete(h, id, "data.lua", t)),
        ("restore", (h, id, t) => Restore(h, id, trashId, t)),
        ("asset", (h, id, t) => PutAsset(h, id, "graphics/up.png", Bytes(PngMagic), ifNoneMatch: "*", t: t)),
        ("reveal", (h, id, t) => Reveal(h, id, "notes.txt", t)),
    };

    [Fact]
    public async Task EveryWriteEndpoint_NeedsTheSessionToken()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var trashId = (await Delete(e.Http, id, "dir/inner.txt")).Str("trashId")!;
        var before = ModSnapshot(e);
        using var anonymous = Client(e.Ui, token: false);

        foreach (var (name, op) in WriteOps(trashId))
        {
            Assert.True((await op(anonymous, id, null)).Status == HttpStatusCode.Forbidden, name + " without token");
            Assert.True((await op(anonymous, id, r => r.Headers.TryAddWithoutValidation("X-FMS-Token", "wrong"))).Status == HttpStatusCode.Forbidden, name + " wrong token");
        }

        Assert.Equal(before, ModSnapshot(e));
        Assert.Empty(e.Started);
        Assert.Single(Entries(await Trash(e.Http, id)));
    }

    [Fact]
    public async Task EveryWriteEndpoint_RejectsAForeignOrigin_ButAcceptsTheSameOrigin()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        var trashId = (await Delete(e.Http, id, "dir/inner.txt")).Str("trashId")!;
        var before = ModSnapshot(e);

        foreach (var (name, op) in WriteOps(trashId))
        {
            var r = await op(e.Http, id, req => req.Headers.TryAddWithoutValidation("Origin", "http://evil.example"));
            Assert.True(r.Status == HttpStatusCode.Forbidden, $"{name} foreign origin: {r}");
            var n = await op(e.Http, id, req => req.Headers.TryAddWithoutValidation("Origin", "null"));
            Assert.True(n.Status == HttpStatusCode.Forbidden, $"{name} null origin: {n}");
        }
        Assert.Equal(before, ModSnapshot(e));
        Assert.Empty(e.Started);

        var same = await Mkdir(e.Http, id, "same-origin", req => req.Headers.TryAddWithoutValidation("Origin", e.Ui.Url.TrimEnd('/')));
        Assert.Equal(HttpStatusCode.OK, same.Status);
    }

    [Fact]
    public async Task WriteEndpoints_RejectForeignHost()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);

        var r = await Mkdir(e.Http, id, "via-rebinding", req => req.Headers.Host = "attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.False(Directory.Exists(e.ModFile("via-rebinding")));
    }

    // ================================================================== symlinks / junctions

    /// <summary>Creates <paramref name="link"/> pointing at <paramref name="target"/> (symlink, else a Windows junction). False if not permitted.</summary>
    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }

        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception) { return false; }
    }

    [Fact]
    public async Task LinksOnTheWay_AreRejectedByEveryWriteEndpoint()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        if (!TryLinkDirectory(e.ModFile("link"), e.Outside.Path))
        {
            Console.WriteLine("SKIPPED: cannot create a symbolic link or junction on this machine.");
            return;
        }
        var outsideBefore = Snapshot(e.Outside.Path);

        AssertBadPath(await Mkdir(e.Http, id, "link/newdir"), "mkdir via link");
        AssertBadPath(await Move(e.Http, id, "notes.txt", "link/notes.txt"), "move into link");
        AssertBadPath(await Move(e.Http, id, "link/secret.txt", "stolen.txt"), "move out of link");
        AssertBadPath(await Move(e.Http, id, "link", "renamed-link"), "move the link itself");
        AssertBadPath(await Copy(e.Http, id, "notes.txt", "link/notes.txt"), "copy into link");
        AssertBadPath(await Copy(e.Http, id, "link/secret.txt", "stolen.txt"), "copy out of link");
        AssertBadPath(await Delete(e.Http, id, "link/secret.txt"), "delete via link");
        AssertBadPath(await Delete(e.Http, id, "link"), "delete the link itself");
        AssertBadPath(await PutAsset(e.Http, id, "link/x.png", Bytes(PngMagic), ifNoneMatch: "*"), "asset via link");

        Assert.Equal(outsideBefore, Snapshot(e.Outside.Path));
        Assert.Equal("top-secret", File.ReadAllText(e.Outside.Combine("secret.txt")));
        Assert.False(File.Exists(e.ModFile("stolen.txt")));
        Assert.True(File.Exists(e.ModFile("notes.txt")));

        try { Directory.Delete(e.ModFile("link")); } catch { }
    }

    [Fact]
    public async Task LinkedTrashFolder_IsNotFollowed()
    {
        using var e = await Env.Create();
        var id = await e.Id(Mod);
        Directory.CreateDirectory(e.Workspace.Combine(".fmstudio"));
        if (!TryLinkDirectory(e.TrashRoot, e.Outside.Path))
        {
            Console.WriteLine("SKIPPED: cannot create a symbolic link or junction on this machine.");
            return;
        }
        var outsideBefore = Snapshot(e.Outside.Path);

        var r = await Delete(e.Http, id, "notes.txt");
        // Either refuse (file stays) or succeed without writing through the link.
        Assert.True(r.Code is 200 or 400 or 409 or 500, r.ToString());
        Assert.Equal(outsideBefore, Snapshot(e.Outside.Path));
        if (r.Code != 200) Assert.True(File.Exists(e.ModFile("notes.txt")));

        try { Directory.Delete(e.TrashRoot); } catch { }
    }

    // ================================================================== overview integration

    [Fact]
    public async Task ModOverview_ExposesUnusedFlag_AndReferenceIssues()
    {
        using var e = await Env.Create(env =>
        {
            env.Workspace.Write($"{Mod}/data.lua", "local icon = \"__wmod__/graphics/missing.png\"\nrequire(\"nope.nothing\")\n");
            Directory.CreateDirectory(env.Workspace.Combine(Mod, "graphics"));
            Fixtures.WritePngHeader(env.Workspace.Combine(Mod, "graphics", "orphan.png"), 32, 32);
        });
        var id = await e.Id(Mod);

        var overview = JsonDocument.Parse(await e.Http.GetStringAsync($"/api/mods/{id}")).RootElement;
        var files = overview.GetProperty("files").EnumerateArray().ToList();
        var orphan = files.Single(f => f.GetProperty("path").GetString() == "graphics/orphan.png");
        Assert.True(orphan.GetProperty("unused").GetBoolean());
        var data = files.Single(f => f.GetProperty("path").GetString() == "data.lua");
        Assert.False(data.GetProperty("unused").GetBoolean());

        var codes = overview.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToList();
        Assert.Contains("FMS030", codes);
        Assert.Contains("FMS032", codes);
    }
}
