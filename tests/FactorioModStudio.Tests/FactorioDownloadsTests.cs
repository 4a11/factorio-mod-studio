using System.Net;
using FactorioModStudio.Cli;
using FactorioModStudio.Core;

namespace FactorioModStudio.Tests;

public class FactorioDownloadsTests
{
    private const string UpdaterJson = """
        {
          "core-linux_headless64": [
            {"from":"1.1.109","to":"1.1.110"},
            {"from":"2.0.71","to":"2.0.72"},
            {"from":"2.0.72","to":"2.0.73"},
            {"from":"2.1.23","to":"2.1.24"},
            {"experimental":"2.1.24","stable":"2.0.73"}
          ],
          "core-linux_headless-arm64": [ {"from":"2.1.23","to":"2.1.24"} ]
        }
        """;

    /// <summary>Answers by URL; records every request.</summary>
    internal sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var response = _answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class SyncProgress(Action<(long, long?)> report) : IProgress<(long Received, long? Total)>
    {
        public void Report((long Received, long? Total) value) => report(value);
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    internal static FakeHttp Server(byte[]? archive = null, HttpStatusCode download = HttpStatusCode.OK) => new(request =>
        request.RequestUri!.Host == "updater.factorio.com"
            ? Ok(UpdaterJson)
            : download == HttpStatusCode.OK ? Ok(archive ?? new byte[] { 1, 2, 3 }) : new HttpResponseMessage(download));

    /// <summary>A "tar" that unpacks a headless layout reporting <paramref name="version"/> (or fails with exit code 2).</summary>
    internal static FakeRunner Tar(string? version, bool fail = false) => new(args =>
    {
        if (fail) return new ProcessResult(2, false, new[] { "tar: broken archive" });
        var into = args[3];
        Directory.CreateDirectory(Path.Combine(into, "factorio", "bin", "x64"));
        File.WriteAllText(Path.Combine(into, "factorio", "bin", "x64", "factorio"), "");
        Directory.CreateDirectory(Path.Combine(into, "factorio", "data", "base"));
        File.WriteAllText(Path.Combine(into, "factorio", "data", "base", "info.json"), $$"""{ "name": "base", "version": "{{version}}" }""");
        return new ProcessResult(0, false, Array.Empty<string>());
    });

    private static FactorioDownloads Downloads(TempDir dir, FakeHttp http, FakeRunner runner, string? platformProblem = null) =>
        new(new HttpClient(http), runner, dir.Combine("factorio"), () => platformProblem);

    private static void AssertNoStaging(TempDir dir) =>
        Assert.Empty(Directory.Exists(dir.Combine("factorio")) ? Directory.GetDirectories(dir.Combine("factorio"), ".staging-*") : Array.Empty<string>());

    // ------------------------------------------------------------------ versions

    [Fact]
    public void ParseAvailable_CollectsEveryHeadlessVersion_NewestFirst_WithStableAndExperimental()
    {
        var releases = FactorioDownloads.ParseAvailable(UpdaterJson);
        Assert.Equal(new[] { "2.1.24", "2.1.23", "2.0.73", "2.0.72", "2.0.71", "1.1.110", "1.1.109" }, releases.Versions);
        Assert.Equal("2.0.73", releases.Stable);
        Assert.Equal("2.1.24", releases.Experimental);
    }

    [Fact]
    public void ParseAvailable_SortsNumerically_NotAlphabetically()
    {
        var releases = FactorioDownloads.ParseAvailable("""{ "core-linux_headless64": [ {"from":"2.0.9","to":"2.0.10"}, {"from":"2.0.99","to":"2.0.100"} ] }""");
        Assert.Equal(new[] { "2.0.100", "2.0.99", "2.0.10", "2.0.9" }, releases.Versions);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "core-win64": [] }""")]
    public void ParseAvailable_UnexpectedAnswer_IsAnFmsException(string json) =>
        Assert.ThrowsAny<FmsException>(() => FactorioDownloads.ParseAvailable(json));

    [Theory]
    [InlineData("2.0", "2.0.73")]
    [InlineData(" 2.1 ", "2.1.24")]
    [InlineData("1.1", "1.1.110")]
    [InlineData("2.0.72", "2.0.72")]
    [InlineData("stable", "2.0.73")]
    [InlineData("STABLE", "2.0.73")]
    [InlineData("latest", "2.1.24")]
    [InlineData("3.0", null)]
    [InlineData("2.0.999", null)]
    public void Resolve_BranchExactStableLatest(string spec, string? expected) =>
        Assert.Equal(expected, FactorioDownloads.Resolve(FactorioDownloads.ParseAvailable(UpdaterJson), spec));

    [Theory]
    [InlineData("two")]
    [InlineData("2")]
    [InlineData("2.0.x")]
    [InlineData("2.0.72/../../evil")]
    [InlineData("")]
    public void Resolve_SomethingElse_IsAnFmsException(string spec) =>
        Assert.ThrowsAny<FmsException>(() => FactorioDownloads.Resolve(FactorioDownloads.ParseAvailable(UpdaterJson), spec));

    [Fact]
    public void HeadlessUrl_IsTheDocumentedAddress_AndAcceptsOnlyVersions()
    {
        Assert.Equal("https://www.factorio.com/get-download/2.0.72/headless/linux64", FactorioDownloads.HeadlessUrl("2.0.72"));
        Assert.Throws<ArgumentException>(() => FactorioDownloads.HeadlessUrl("latest"));
        Assert.Throws<ArgumentException>(() => FactorioDownloads.HeadlessUrl("2.0.72?x=1"));
    }

    [Fact]
    public async Task Available_ReadsTheUpdater()
    {
        using var dir = new TempDir();
        var http = Server();
        var releases = await Downloads(dir, http, Tar("2.0.72")).AvailableAsync();
        Assert.Equal("2.1.24", releases.Versions[0]);
        Assert.Equal(new[] { FactorioDownloads.UpdaterUrl }, http.Requests);
    }

    [Fact]
    public async Task Available_ServerError_IsAnFmsException()
    {
        using var dir = new TempDir();
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, http, Tar("2.0.72")).AvailableAsync());
    }

    // ------------------------------------------------------------------ install

    [Fact]
    public async Task Install_DownloadsUnpacksVerifiesAndMovesIntoPlace()
    {
        using var dir = new TempDir();
        var http = Server();
        var tar = Tar("2.0.72");
        var reports = 0;

        var result = await Downloads(dir, http, tar).InstallHeadlessAsync("2.0.72", new SyncProgress(_ => reports++));

        Assert.False(result.AlreadyInstalled);
        Assert.True(reports > 0);
        Assert.Equal(dir.Combine("factorio", "2.0.72-headless"), result.Path);
        Assert.True(File.Exists(result.Executable));
        Assert.Equal("2.0.72", FactorioInstallations.DetectVersion(result.Executable));
        Assert.Equal(new[] { "https://www.factorio.com/get-download/2.0.72/headless/linux64" }, http.Requests);
        var call = Assert.Single(tar.Calls);
        Assert.Equal("-xf", call[0]);
        Assert.Equal("-C", call[2]);
        AssertNoStaging(dir);
    }

    [Fact]
    public async Task Install_TheInstalledGame_IsWhatTheDiscoveryResolves()
    {
        using var dir = new TempDir();
        var result = await Downloads(dir, Server(), Tar("2.0.72")).InstallHeadlessAsync("2.0.72");
        // The discovery scans the children of the managed folder and resolves each to bin/x64/factorio.
        Assert.Equal(result.Executable, FactorioLocator.ResolveExecutable(result.Path));
    }

    [Fact]
    public async Task Install_SameVersionAgain_DownloadsNothing()
    {
        using var dir = new TempDir();
        await Downloads(dir, Server(), Tar("2.0.72")).InstallHeadlessAsync("2.0.72");

        var http = Server();
        var tar = Tar("2.0.72");
        var again = await Downloads(dir, http, tar).InstallHeadlessAsync("2.0.72");

        Assert.True(again.AlreadyInstalled);
        Assert.Empty(http.Requests);
        Assert.Empty(tar.Calls);
    }

    [Fact]
    public async Task Install_ArchiveWithAnotherVersion_InstallsNothing()
    {
        using var dir = new TempDir();
        var error = await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, Server(), Tar("2.0.71")).InstallHeadlessAsync("2.0.72"));
        Assert.Contains("2.0.71", error.Message);
        Assert.False(Directory.Exists(dir.Combine("factorio", "2.0.72-headless")));
        AssertNoStaging(dir);
    }

    [Fact]
    public async Task Install_TarFails_InstallsNothing()
    {
        using var dir = new TempDir();
        var error = await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, Server(), Tar(null, fail: true)).InstallHeadlessAsync("2.0.72"));
        Assert.Contains("tar", error.Message);
        Assert.False(Directory.Exists(dir.Combine("factorio", "2.0.72-headless")));
        AssertNoStaging(dir);
    }

    [Fact]
    public async Task Install_NoSuchDownload_SaysSo_AndInstallsNothing()
    {
        using var dir = new TempDir();
        var tar = Tar("2.0.72");
        var error = await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, Server(download: HttpStatusCode.NotFound), tar).InstallHeadlessAsync("2.0.72"));
        Assert.Contains("no headless download", error.Message);
        Assert.Empty(tar.Calls);
        AssertNoStaging(dir);
    }

    [Fact]
    public async Task Install_EmptyDownload_IsRefused()
    {
        using var dir = new TempDir();
        await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, Server(archive: Array.Empty<byte>()), Tar("2.0.72")).InstallHeadlessAsync("2.0.72"));
        AssertNoStaging(dir);
    }

    [Fact]
    public async Task Install_TooLargeDownload_IsRefusedBeforeReading()
    {
        using var dir = new TempDir();
        var http = new FakeHttp(_ =>
        {
            var content = new ByteArrayContent(new byte[] { 1 });
            content.Headers.ContentLength = FactorioDownloads.MaxDownloadBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var error = await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, http, Tar("2.0.72")).InstallHeadlessAsync("2.0.72"));
        Assert.Contains("larger than", error.Message);
        AssertNoStaging(dir);
    }

    /// <summary>A body that sends one byte and then nothing, until the read is cancelled.</summary>
    private sealed class SilentStream : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent) { _sent = true; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task Install_StalledDownload_IsGivenUp_AndInstallsNothing()
    {
        using var dir = new TempDir();
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) });
        var tar = Tar("2.0.72");
        var downloads = new FactorioDownloads(new HttpClient(http), tar, dir.Combine("factorio"), () => null, stallTimeout: TimeSpan.FromMilliseconds(300));

        var error = await Assert.ThrowsAnyAsync<FmsException>(() => downloads.InstallHeadlessAsync("2.0.72"));
        Assert.Contains("stalled", error.Message);
        Assert.Empty(tar.Calls);
        AssertNoStaging(dir);
    }

    [Fact]
    public async Task Install_CancelledByTheCaller_IsACancellation_NotAStall()
    {
        using var dir = new TempDir();
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var downloads = new FactorioDownloads(new HttpClient(http), Tar("2.0.72"), dir.Combine("factorio"), () => null, stallTimeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloads.InstallHeadlessAsync("2.0.72", cancellationToken: cancel.Token));
        AssertNoStaging(dir);
    }

    [Fact]
    public async Task Install_RedirectedToPlainHttp_IsRefused()
    {
        using var dir = new TempDir();
        var http = new FakeHttp(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1 }),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cdn.example/factorio.tar.xz")
        });
        var tar = Tar("2.0.72");
        await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, http, tar).InstallHeadlessAsync("2.0.72"));
        Assert.Empty(tar.Calls);
    }

    [Fact]
    public async Task Install_OnAnUnsupportedPlatform_RefusesWithoutTouchingTheNetwork()
    {
        using var dir = new TempDir();
        var http = Server();
        var error = await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, http, Tar("2.0.72"), platformProblem: "Linux only, run it in WSL").InstallHeadlessAsync("2.0.72"));
        Assert.Contains("WSL", error.Message);
        Assert.Empty(http.Requests);
        Assert.False(Directory.Exists(dir.Combine("factorio")));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("2.0")]
    [InlineData("2.0.72/../../x")]
    [InlineData("..")]
    public async Task Install_OnlyTakesAnExactVersion(string version)
    {
        using var dir = new TempDir();
        var http = Server();
        await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, http, Tar("2.0.72")).InstallHeadlessAsync(version));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Install_TargetFolderWithoutGame_IsNotOverwritten()
    {
        using var dir = new TempDir();
        dir.Write("factorio/2.0.72-headless/keep.txt", "mine");
        var http = Server();
        await Assert.ThrowsAnyAsync<FmsException>(() => Downloads(dir, http, Tar("2.0.72")).InstallHeadlessAsync("2.0.72"));
        Assert.Equal("mine", File.ReadAllText(dir.Combine("factorio", "2.0.72-headless", "keep.txt")));
        Assert.Empty(http.Requests);
    }

    // ------------------------------------------------------------------ installed / remove

    [Fact]
    public async Task Installed_ListsTheStudiosInstallations_NewestFirst_AndNothingElse()
    {
        using var dir = new TempDir();
        await Downloads(dir, Server(), Tar("2.0.72")).InstallHeadlessAsync("2.0.72");
        await Downloads(dir, Server(), Tar("2.0.100")).InstallHeadlessAsync("2.0.100");
        dir.Write("factorio/my-own-factorio/readme.txt", "not ours");
        dir.Write("factorio/2.0.5-headless/empty.txt", "no game in here");

        var installed = Downloads(dir, Server(), Tar(null)).Installed();
        Assert.Equal(new[] { "2.0.100", "2.0.72" }, installed.Select(i => i.Version));
    }

    [Fact]
    public async Task Remove_DeletesOnlyThatVersion()
    {
        using var dir = new TempDir();
        await Downloads(dir, Server(), Tar("2.0.72")).InstallHeadlessAsync("2.0.72");
        await Downloads(dir, Server(), Tar("2.0.73")).InstallHeadlessAsync("2.0.73");
        var downloads = Downloads(dir, Server(), Tar(null));

        Assert.True(downloads.Remove("2.0.72"));
        Assert.False(Directory.Exists(dir.Combine("factorio", "2.0.72-headless")));
        Assert.True(Directory.Exists(dir.Combine("factorio", "2.0.73-headless")));
        Assert.False(downloads.Remove("2.0.72"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("2.0")]
    [InlineData("2.0.72/..")]
    public void Remove_TakesOnlyAnExactVersion(string version)
    {
        using var dir = new TempDir();
        Assert.ThrowsAny<FmsException>(() => Downloads(dir, Server(), Tar(null)).Remove(version));
    }

    // ------------------------------------------------------------------ telling headless servers apart

    [Fact]
    public async Task IsHeadlessInstall_TheStudiosHeadlessFolder_Only()
    {
        using var dir = new TempDir();
        var managed = dir.Combine("factorio");
        var installed = await Downloads(dir, Server(), Tar("2.0.72")).InstallHeadlessAsync("2.0.72");
        Assert.True(FactorioDownloads.IsHeadlessInstall(installed.Executable, managed));

        // the same layout under another name, or the same name elsewhere, is somebody's own Factorio
        Tar("2.0.72").Run("tar", new[] { "-xf", "x", "-C", dir.Combine("own") }, TimeSpan.Zero);
        Assert.False(FactorioDownloads.IsHeadlessInstall(dir.Combine("own", "factorio", "bin", "x64", "factorio"), managed));
        Directory.Move(dir.Combine("own", "factorio"), dir.Combine("own", "2.0.72-headless"));
        Assert.False(FactorioDownloads.IsHeadlessInstall(dir.Combine("own", "2.0.72-headless", "bin", "x64", "factorio"), managed));
        Assert.False(FactorioDownloads.IsHeadlessInstall(dir.Combine("nothing", "factorio"), managed));
    }

    private static FactorioInstallation Install(string path, string version) => new(FactorioInstallations.IdOf(path), path, version, null, new[] { "auto" });

    [Fact]
    public void PickToPlay_LeavesTheHeadlessServersOut()
    {
        var headless = Install("/data/factorio/2.0.76-headless/bin/x64/factorio", "2.0.76");
        var game = Install("/games/factorio/bin/x64/factorio", "2.0.60");
        bool IsHeadless(string path) => path == headless.Path;

        Assert.Same(headless, FactorioInstallations.Pick(new[] { headless, game }, "2.0").Chosen); // smoke: the newest is fine
        Assert.Same(game, FactorioInstallations.PickToPlay(new[] { headless, game }, "2.0", isHeadless: IsHeadless).Chosen);

        var none = FactorioInstallations.PickToPlay(new[] { headless }, "2.0", isHeadless: IsHeadless);
        Assert.Null(none.Chosen);
        Assert.Contains("headless", none.Reason);
    }

    // ------------------------------------------------------------------ CLI

    private static (int Code, string Out, string Err) Cli(HttpClient? http, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Program.Run(args, output, error, new FakeRunner(_ => new ProcessResult(0, false, Array.Empty<string>())), http);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Cli_UnknownFactorioSubcommand_IsAUsageError()
    {
        var (code, _, error) = Cli(null, "factorio", "frobnicate");
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("factorio install", error);
    }

    [Fact]
    public void Cli_RemoveWithoutVersion_IsAUsageError()
    {
        var (code, _, error) = Cli(null, "factorio", "remove");
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("Usage", error);
    }

    [Fact]
    public void Cli_Available_PrintsTheNewestOfEachBranch()
    {
        var (code, output, _) = Cli(new HttpClient(Server()), "factorio", "available");
        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("stable 2.0.73, experimental 2.1.24", output);
        Assert.Contains("2.0   2.0.73", output);
        Assert.DoesNotContain("2.0.72", output);
    }

    [Fact]
    public void Cli_AvailableAll_PrintsEveryRelease()
    {
        var (_, output, _) = Cli(new HttpClient(Server()), "factorio", "available", "--all");
        Assert.Contains("2.0.73, 2.0.72, 2.0.71", output);
    }

    [Fact]
    public void Cli_RunWithInstall_IsRefused_BecauseHeadlessCannotPlay()
    {
        using var mod = Fixtures.ValidMod();
        var (code, _, error) = Cli(null, "run", "--project", mod.Path, "--install");
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("smoke", error);
    }

    [Fact]
    public void Cli_Install_OffLinux_ExplainsAndUsesNoNetwork()
    {
        if (FactorioDownloads.HeadlessPlatformProblem() is null) return; // on Linux x64 this would really install
        var http = Server();
        var (code, _, error) = Cli(new HttpClient(http), "factorio", "install", "2.0");
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("Linux", error);
        Assert.Empty(http.Requests);
    }
}
