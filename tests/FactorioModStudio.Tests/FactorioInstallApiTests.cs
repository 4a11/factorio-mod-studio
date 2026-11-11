using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;

namespace FactorioModStudio.Tests;

/// <summary>The Build tab's "install a missing Factorio" API, with Factorio's servers and tar replaced by fakes.</summary>
public class FactorioInstallApiTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TempDir Project = new();
        public TempDir Workspace = new();
        public TempDir Managed = new();
        public FactorioDownloadsTests.FakeHttp Factorio = null!;
        public RunningUi Ui = null!;
        public HttpClient Http = null!;

        /// <summary>The game's player-data.json and the studio's saved account live here, never in the real profile.</summary>
        public TempDir Accounts = new();
        public string PlayerData => Accounts.Combine("player-data.json");
        public string AccountFile => Accounts.Combine("factorio-account.json");

        public const string SameProblem = "<same as the headless server>";

        /// <param name="gameProblem">Why the full game cannot be installed (null: it can, as on Linux); by default the same as
        /// <paramref name="platformProblem"/>, so "off Linux" still asks nobody.</param>
        public static async Task<Env> Create(string? platformProblem = null, string tarVersion = "2.0.73", HttpStatusCode download = HttpStatusCode.OK,
            string? gameProblem = SameProblem)
        {
            var env = new Env { Factorio = FactorioDownloadsTests.Server(download: download) };
            ApiEnv.AddMod(env.Workspace, "alpha", "2.0");
            var tar = FactorioDownloadsTests.Tar(tarVersion);
            if (gameProblem == SameProblem) gameProblem = platformProblem;
            env.Ui = await UiServer.StartAsync(
                new ProjectContext(env.Project.Path, new ProjectConfig(), configFileExists: false),
                new UiOptions(Port: 0, ExtraModFolders: Array.Empty<string>(), WorkspaceDirectory: env.Workspace.Path,
                    FactorioDownloads: () => new FactorioDownloads(new HttpClient(env.Factorio), tar, env.Managed.Path, () => platformProblem,
                        gamePlatform: () => gameProblem is null ? ("linux64", null) : (null, gameProblem)),
                    PlayerDataFile: env.PlayerData, FactorioAccountFile: env.AccountFile));
            env.Http = ApiEnv.Client(env.Ui);
            return env;
        }

        public Task<ApiResp> Get(string url) => ApiEnv.Send(Http, HttpMethod.Get, url);
        public Task<ApiResp> Install(string body, HttpClient? http = null) => ApiEnv.Send(http ?? Http, HttpMethod.Post, "/api/factorio/install", body);

        public async Task<ApiResp> WaitDone(int seconds = 20)
        {
            var clock = Stopwatch.StartNew();
            ApiResp last;
            do
            {
                last = await Get("/api/factorio/install");
                var state = last.Json.GetProperty("install").S("state");
                if (state is "succeeded" or "failed" or "cancelled") return last;
                await Task.Delay(50);
            } while (clock.Elapsed < TimeSpan.FromSeconds(seconds));
            throw new InvalidOperationException("The install did not finish: " + last.Raw);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Ui.DisposeAsync();
            Project.Dispose();
            Workspace.Dispose();
            Managed.Dispose();
            Accounts.Dispose();
        }
    }

    [Fact]
    public async Task Available_OffLinux_SaysWhy_AndAsksNobody()
    {
        await using var env = await Env.Create(platformProblem: "Linux only; run it in WSL.");
        var r = await env.Get("/api/factorio/available");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Contains("WSL", r.Str("platformProblem"));
        Assert.Empty(r.Json.GetProperty("branches").EnumerateArray());
        Assert.Empty(env.Factorio.Requests);
    }

    [Fact]
    public async Task Available_ListsTheNewestReleaseOfEachBranch()
    {
        await using var env = await Env.Create();
        var r = await env.Get("/api/factorio/available");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Null(r.Str("platformProblem"));
        Assert.Equal("2.0.73", r.Str("stable"));
        var branches = r.Json.GetProperty("branches").EnumerateArray().ToList();
        Assert.Equal(new[] { "2.1", "2.0", "1.1" }, branches.Select(b => b.S("branch")));
        Assert.Equal("2.0.73", branches[1].S("newest"));
    }

    [Fact]
    public async Task Install_ABranch_InstallsItsNewestRelease_InTheBackground()
    {
        await using var env = await Env.Create();
        var started = await env.Install("""{ "version": "2.0" }""");
        Assert.Equal(HttpStatusCode.Accepted, started.Status);
        Assert.Equal("2.0.73", started.Json.GetProperty("install").S("version"));

        var done = await env.WaitDone();
        var install = done.Json.GetProperty("install");
        Assert.Equal("succeeded", install.S("state"));
        Assert.True(File.Exists(Path.Combine(install.S("path")!, "bin", "x64", "factorio")));
        Assert.Contains("https://www.factorio.com/get-download/2.0.73/headless/linux64", env.Factorio.Requests);
    }

    [Fact]
    public async Task Install_ArchiveWithTheWrongVersion_Fails_AndSaysWhy()
    {
        await using var env = await Env.Create(tarVersion: "2.0.1");
        Assert.Equal(HttpStatusCode.Accepted, (await env.Install("""{ "version": "2.0.73" }""")).Status);
        var install = (await env.WaitDone()).Json.GetProperty("install");
        Assert.Equal("failed", install.S("state"));
        Assert.Contains("2.0.1", install.S("error"));
    }

    [Theory]
    [InlineData("""{ "version": "two" }""")]
    [InlineData("""{ "version": "2.0.73/../../x" }""")]
    [InlineData("""{ "version": 2 }""")]
    [InlineData("""{ }""")]
    [InlineData("""not json""")]
    public async Task Install_BadRequest_Is400_WithoutAskingFactorio(string body)
    {
        await using var env = await Env.Create();
        var r = await env.Install(body);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Empty(env.Factorio.Requests);
    }

    [Fact]
    public async Task Install_UnknownRelease_Is404()
    {
        await using var env = await Env.Create();
        Assert.Equal(HttpStatusCode.NotFound, (await env.Install("""{ "version": "3.0" }""")).Status);
    }

    [Fact]
    public async Task Install_OffLinux_Is400_WithTheReason()
    {
        await using var env = await Env.Create(platformProblem: "Linux only; run it in WSL.");
        var r = await env.Install("""{ "version": "2.0" }""");
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Contains("WSL", r.Error);
        Assert.Empty(env.Factorio.Requests);
    }

    [Fact]
    public async Task Install_WithoutTheSessionToken_IsRefused()
    {
        await using var env = await Env.Create();
        using var anonymous = ApiEnv.Client(env.Ui, token: false);
        var r = await env.Install("""{ "version": "2.0" }""", anonymous);
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.Empty(env.Factorio.Requests);
    }

    [Fact]
    public async Task Status_BeforeAnyInstall_IsNull()
    {
        await using var env = await Env.Create();
        var r = await env.Get("/api/factorio/install");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, r.Json.GetProperty("install").ValueKind);
    }

    [Fact]
    public async Task Cancel_WithNothingRunning_Is409()
    {
        await using var env = await Env.Create();
        Assert.Equal(HttpStatusCode.Conflict, (await ApiEnv.Send(env.Http, HttpMethod.Post, "/api/factorio/install/cancel", "{}")).Status);
    }

    [Fact]
    public async Task Remove_DeletesAStudioInstall_AndNothingElse()
    {
        await using var env = await Env.Create();
        await env.Install("""{ "version": "2.0" }""");
        await env.WaitDone();

        Assert.Equal(HttpStatusCode.BadRequest, (await ApiEnv.Send(env.Http, HttpMethod.Delete, "/api/factorio/install/2.0")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiEnv.Send(env.Http, HttpMethod.Delete, "/api/factorio/install/2.0.72")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiEnv.Send(env.Http, HttpMethod.Delete, "/api/factorio/install/2.0.73")).Status);
        Assert.False(Directory.Exists(Path.Combine(env.Managed.Path, "2.0.73-headless")));
    }

    // ------------------------------------------------------------------ the full game and the factorio.com account

    [Fact]
    public async Task Account_Save_IsReported_WithoutTheToken_AndCanBeForgotten()
    {
        await using var env = await Env.Create();
        var none = await env.Get("/api/factorio/account");
        Assert.Equal(JsonValueKind.Null, none.Json.GetProperty("account").GetProperty("source").ValueKind);

        var saved = await ApiEnv.Send(env.Http, HttpMethod.Put, "/api/factorio/account", """{ "username": "engineer", "token": "abc123def456" }""");
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal("studio", saved.Json.GetProperty("account").S("source"));
        Assert.Equal("engineer", saved.Json.GetProperty("account").S("username"));
        Assert.DoesNotContain("abc123def456", saved.Raw);
        Assert.DoesNotContain("abc123def456", (await env.Get("/api/factorio/account")).Raw);
        Assert.DoesNotContain("abc123def456", (await env.Get("/api/factorio/available")).Raw);

        var forgotten = await ApiEnv.Send(env.Http, HttpMethod.Delete, "/api/factorio/account");
        Assert.Equal(JsonValueKind.Null, forgotten.Json.GetProperty("account").GetProperty("source").ValueKind);
        Assert.False(File.Exists(env.AccountFile));
    }

    [Theory]
    [InlineData("""{ "username": "engineer", "token": "x" }""")]
    [InlineData("""{ "username": "two words", "token": "abc123def456" }""")]
    [InlineData("""{ "token": "abc123def456" }""")]
    [InlineData("""[]""")]
    public async Task Account_BadInput_Is400_AndSavesNothing(string body)
    {
        await using var env = await Env.Create();
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiEnv.Send(env.Http, HttpMethod.Put, "/api/factorio/account", body)).Status);
        Assert.False(File.Exists(env.AccountFile));
    }

    [Fact]
    public async Task Account_UseGame_NeedsTheGameSignedIn()
    {
        await using var env = await Env.Create();
        Assert.Equal(HttpStatusCode.Conflict, (await ApiEnv.Send(env.Http, HttpMethod.Put, "/api/factorio/account", """{ "useGame": true }""")).Status);

        File.WriteAllText(env.PlayerData, """{ "service-username": "gamer", "service-token": "feedbeef1234" }""");
        var r = await ApiEnv.Send(env.Http, HttpMethod.Put, "/api/factorio/account", """{ "useGame": true }""");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("game", r.Json.GetProperty("account").S("source"));
        Assert.Equal("gamer", r.Json.GetProperty("account").S("gameUsername"));
        Assert.DoesNotContain("feedbeef1234", File.ReadAllText(env.AccountFile));
    }

    [Fact]
    public async Task Account_WithoutTheSessionToken_IsRefused()
    {
        await using var env = await Env.Create();
        using var anonymous = new HttpClient { BaseAddress = env.Http.BaseAddress };
        var r = await ApiEnv.Send(anonymous, HttpMethod.Put, "/api/factorio/account", """{ "username": "engineer", "token": "abc123def456" }""");
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.False(File.Exists(env.AccountFile));
    }

    [Fact]
    public async Task InstallGame_WithoutAnAccount_Is400_AndAsksNobody()
    {
        await using var env = await Env.Create();
        var r = await env.Install("""{ "version": "2.0", "game": true }""");
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Contains("account", r.Error);
        Assert.Empty(env.Factorio.Requests);
    }

    [Fact]
    public async Task InstallGame_WithASavedAccount_InstallsTheFullGame_NextToTheHeadlessServers()
    {
        await using var env = await Env.Create();
        await ApiEnv.Send(env.Http, HttpMethod.Put, "/api/factorio/account", """{ "username": "engineer", "token": "abc123def456" }""");
        Assert.Equal(HttpStatusCode.Accepted, (await env.Install("""{ "version": "2.0", "game": true }""")).Status);
        var install = (await env.WaitDone()).Json.GetProperty("install");
        Assert.Equal("succeeded", install.S("state"));
        Assert.True(install.GetProperty("game").GetBoolean());
        Assert.Equal(Path.Combine(env.Managed.Path, "2.0.73-game"), install.S("path"));
        Assert.DoesNotContain("abc123def456", (await env.Get("/api/factorio/install")).Raw);
        Assert.Contains(env.Factorio.Requests, u => u.StartsWith("https://www.factorio.com/get-download/2.0.73/alpha/linux64?", StringComparison.Ordinal));

        var available = await env.Get("/api/factorio/available");
        Assert.Equal(new[] { "2.0.73" }, available.Json.GetProperty("game").GetProperty("installed").EnumerateArray().Select(v => v.GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await ApiEnv.Send(env.Http, HttpMethod.Delete, "/api/factorio/install/2.0.73")).Status); // the headless one
        Assert.Equal(HttpStatusCode.OK, (await ApiEnv.Send(env.Http, HttpMethod.Delete, "/api/factorio/install/2.0.73?game=true")).Status);
        Assert.False(Directory.Exists(Path.Combine(env.Managed.Path, "2.0.73-game")));
    }

    [Fact]
    public async Task Available_OffLinux_StillListsReleases_WhenTheFullGameCanBeInstalled()
    {
        await using var env = await Env.Create(platformProblem: "Linux only; run it in WSL.", gameProblem: null);
        var r = await env.Get("/api/factorio/available");
        Assert.Contains("WSL", r.Str("platformProblem"));
        Assert.NotEmpty(r.Json.GetProperty("branches").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, r.Json.GetProperty("game").GetProperty("platformProblem").ValueKind);
    }
}
