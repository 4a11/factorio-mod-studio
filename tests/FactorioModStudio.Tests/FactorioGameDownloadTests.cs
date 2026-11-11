using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using FactorioModStudio.Core;

namespace FactorioModStudio.Tests;

/// <summary>The factorio.com account (<see cref="FactorioAccount"/>) and installing the full game with it.</summary>
public class FactorioAccountTests
{
    private static Func<string, string?> NoEnvironment => _ => null;

    [Fact]
    public void Save_ThenResolve_GivesTheAccountBack_FromTheStudio()
    {
        using var dir = new TempDir();
        var store = dir.Combine("account.json");
        FactorioAccount.Save("engineer", "abc123def456", store);
        var credentials = FactorioAccount.Resolve(store, dir.Combine("none.json"), NoEnvironment);
        Assert.Equal(new FactorioCredentials("engineer", "abc123def456", "studio"), credentials);
    }

    [Fact]
    public void Save_OnWindows_DoesNotWriteTheTokenAsPlainText()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var store = dir.Combine("account.json");
        FactorioAccount.Save("engineer", "abc123def456", store);
        var text = File.ReadAllText(store);
        Assert.DoesNotContain("abc123def456", text);
        Assert.Contains("dpapi", text);
    }

    [Fact]
    public void Resolve_PrefersTheEnvironment_ThenTheStudio()
    {
        using var dir = new TempDir();
        var store = dir.Combine("account.json");
        FactorioAccount.Save("saved", "abc123def456", store);
        var env = new Dictionary<string, string?> { [FactorioAccount.UsernameVariable] = "ci-user", [FactorioAccount.TokenVariable] = "0123456789abcdef" };
        var credentials = FactorioAccount.Resolve(store, null, name => env.GetValueOrDefault(name));
        Assert.Equal("ci-user", credentials!.Username);
        Assert.Equal("env", credentials.Source);
    }

    [Fact]
    public void UseGame_ReadsPlayerData_EachTime_AndCopiesNothing()
    {
        using var dir = new TempDir();
        var store = dir.Combine("account.json");
        var playerData = dir.Write("player-data.json", """{ "service-username": "gamer", "service-token": "feedbeef1234", "other": 1 }""");
        FactorioAccount.SaveUseGame(store);
        Assert.DoesNotContain("feedbeef1234", File.ReadAllText(store));

        var credentials = FactorioAccount.Resolve(store, playerData, NoEnvironment);
        Assert.Equal(new FactorioCredentials("gamer", "feedbeef1234", "game"), credentials);

        File.Delete(playerData); // signed out of the game: no account any more
        Assert.Null(FactorioAccount.Resolve(store, playerData, NoEnvironment));
    }

    [Fact]
    public void Status_NamesTheAccount_AndTheGamesOwn_ButNeverTheToken()
    {
        using var dir = new TempDir();
        var store = dir.Combine("account.json");
        var playerData = dir.Write("player-data.json", """{ "service-username": "gamer", "service-token": "feedbeef1234" }""");
        var empty = FactorioAccount.Status(store, playerData, NoEnvironment);
        Assert.Null(empty.Source);
        Assert.Equal("gamer", empty.GameUsername);

        FactorioAccount.Save("engineer", "abc123def456", store);
        var saved = FactorioAccount.Status(store, playerData, NoEnvironment);
        Assert.Equal("studio", saved.Source);
        Assert.Equal("engineer", saved.Username);
        Assert.DoesNotContain("abc123def456", saved.ToString());
    }

    [Fact]
    public void Forget_RemovesTheSavedAccount()
    {
        using var dir = new TempDir();
        var store = dir.Combine("account.json");
        FactorioAccount.Save("engineer", "abc123def456", store);
        Assert.True(FactorioAccount.Forget(store));
        Assert.False(FactorioAccount.Forget(store));
        Assert.Null(FactorioAccount.Resolve(store, null, NoEnvironment));
    }

    [Theory]
    [InlineData("", "abc123def456")]
    [InlineData("bad name", "abc123def456")]
    [InlineData("engineer", "short")]
    [InlineData("engineer", "abc123def456&x=1")]
    public void Save_RejectsWhatIsNotAUsernameOrToken(string username, string token)
    {
        using var dir = new TempDir();
        Assert.Throws<FmsException>(() => FactorioAccount.Save(username, token, dir.Combine("account.json")));
        Assert.False(File.Exists(dir.Combine("account.json")));
    }

    [Fact]
    public void Credentials_ToString_LeavesTheTokenOut() =>
        Assert.DoesNotContain("abc123def456", new FactorioCredentials("engineer", "abc123def456", "studio").ToString());
}

public class FactorioGameDownloadTests
{
    private static readonly FactorioCredentials Account = new("engineer", "abc123def456", "studio");

    /// <summary>A zip shaped like Factorio's win64-manual package: "Factorio_&lt;v&gt;/bin/x64/factorio.exe" and its base info.json.</summary>
    internal static byte[] GameZip(string version, string? reported = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry($"Factorio_{version}/bin/x64/factorio.exe").Open())) writer.Write("exe");
            using (var writer = new StreamWriter(zip.CreateEntry($"Factorio_{version}/data/base/info.json").Open()))
                writer.Write($$"""{ "name": "base", "version": "{{reported ?? version}}" }""");
        }
        return memory.ToArray();
    }

    private static FactorioDownloadsTests.FakeHttp Server(Func<HttpRequestMessage, HttpResponseMessage> download) => new(request =>
        request.RequestUri!.Host == "updater.factorio.com" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") } : download(request));

    private static HttpResponseMessage Zip(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static FactorioDownloads Downloads(TempDir dir, FactorioDownloadsTests.FakeHttp http, string platform = "win64-manual") =>
        new(new HttpClient(http), new FakeRunner(_ => throw new InvalidOperationException("tar is not used for the zip")), dir.Combine("factorio"),
            () => "headless: Linux only", gamePlatform: () => (platform, null));

    [Fact]
    public async Task InstallGame_DownloadsTheAlphaPackage_WithTheAccount_AndUnpacksIt()
    {
        using var dir = new TempDir();
        var http = Server(_ => Zip(GameZip("2.0.73")));
        var downloads = Downloads(dir, http);

        var result = await downloads.InstallGameAsync("2.0.73", Account);

        Assert.True(result.Game);
        Assert.Equal(dir.Combine("factorio", "2.0.73-game"), result.Path);
        Assert.True(File.Exists(Path.Combine(result.Path, "bin", "x64", "factorio.exe")));
        var request = Assert.Single(http.Requests);
        Assert.StartsWith("https://www.factorio.com/get-download/2.0.73/alpha/win64-manual?", request);
        Assert.Contains("username=engineer", request);
        Assert.Contains("token=abc123def456", request);
        Assert.Equal("2.0.73", Assert.Single(downloads.InstalledGames()).Version);
        Assert.Empty(downloads.Installed()); // the headless list is separate
        Assert.True(FactorioDownloads.IsGameInstall(result.Executable, dir.Combine("factorio")));
        Assert.False(FactorioDownloads.IsHeadlessInstall(result.Executable, dir.Combine("factorio")));
        Assert.Empty(Directory.GetDirectories(dir.Combine("factorio"), ".staging-*"));
    }

    [Fact]
    public async Task InstallGame_AgainDownloadsNothing_AndRemoveGameDeletesIt()
    {
        using var dir = new TempDir();
        var http = Server(_ => Zip(GameZip("2.0.73")));
        var downloads = Downloads(dir, http);
        await downloads.InstallGameAsync("2.0.73", Account);
        Assert.True((await downloads.InstallGameAsync("2.0.73", Account)).AlreadyInstalled);
        Assert.Single(http.Requests);

        Assert.False(downloads.Remove("2.0.73")); // not a headless server
        Assert.True(downloads.RemoveGame("2.0.73"));
        Assert.Empty(downloads.InstalledGames());
    }

    [Fact]
    public async Task InstallGame_LoginPageInsteadOfTheArchive_SaysTheAccountWasRefused_WithoutTheToken()
    {
        using var dir = new TempDir();
        var http = Server(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", System.Text.Encoding.UTF8, "text/html") });
        var error = await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, http).InstallGameAsync("2.0.73", Account));
        Assert.Contains("did not accept", error.Message);
        Assert.DoesNotContain("abc123def456", error.Message);
        Assert.Empty(Directory.GetDirectories(dir.Combine("factorio")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task InstallGame_Refused_SaysSo(HttpStatusCode status)
    {
        using var dir = new TempDir();
        var error = await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, Server(_ => new HttpResponseMessage(status))).InstallGameAsync("2.0.73", Account));
        Assert.Contains("did not accept", error.Message);
    }

    [Fact]
    public async Task InstallGame_ServerError_NamesTheAddress_WithoutTheAccount()
    {
        using var dir = new TempDir();
        var error = await Assert.ThrowsAnyAsync<FmsException>(() =>
            Downloads(dir, Server(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).InstallGameAsync("2.0.73", Account));
        Assert.Contains("https://www.factorio.com/get-download/2.0.73/alpha/win64-manual", error.Message);
        Assert.DoesNotContain("abc123def456", error.Message);
        Assert.DoesNotContain("engineer", error.Message);
    }

    [Fact]
    public async Task InstallGame_ArchiveWithAnotherVersion_InstallsNothing()
    {
        using var dir = new TempDir();
        var error = await Assert.ThrowsAnyAsync<FmsException>(() =>
            Downloads(dir, Server(_ => Zip(GameZip("2.0.73", reported: "2.0.1")))).InstallGameAsync("2.0.73", Account));
        Assert.Contains("2.0.1", error.Message);
        Assert.False(Directory.Exists(dir.Combine("factorio", "2.0.73-game")));
    }

    [Fact]
    public async Task InstallGame_WhereThereIsNoPackage_SaysWhy_AndAsksNobody()
    {
        using var dir = new TempDir();
        var http = Server(_ => Zip(GameZip("2.0.73")));
        var downloads = new FactorioDownloads(new HttpClient(http), new FakeRunner(_ => throw new InvalidOperationException()), dir.Combine("factorio"),
            gamePlatform: () => (null, "On macOS the full game comes as a disk image"));
        var error = await Assert.ThrowsAnyAsync<FmsException>(() => downloads.InstallGameAsync("2.0.73", Account));
        Assert.Contains("macOS", error.Message);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("2.0.73/../x")]
    [InlineData("2.0")]
    public async Task InstallGame_OnlyAcceptsAnExactVersion(string version)
    {
        using var dir = new TempDir();
        await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, Server(_ => Zip(GameZip("2.0.73")))).InstallGameAsync(version, Account));
    }
}
