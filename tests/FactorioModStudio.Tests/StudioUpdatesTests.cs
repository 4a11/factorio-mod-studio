using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FactorioModStudio.Core;

namespace FactorioModStudio.Tests;

public class StudioUpdatesTests
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";

    private static string ReleasesJson(params (string Tag, bool Draft, string? Sha)[] releases) =>
        "[" + string.Join(",", releases.Select(r => $$"""
            {
              "tag_name": "{{r.Tag}}", "draft": {{(r.Draft ? "true" : "false")}}, "body": "notes of {{r.Tag}}",
              "html_url": "https://github.com/4a11/factorio-mod-studio/releases/tag/{{r.Tag}}",
              "assets": [
                { "name": "fmstudio-{{r.Tag}}-{{StudioUpdates.RuntimeId()}}{{Extension()}}", "size": 3,
                  "browser_download_url": "https://github.com/download/{{r.Tag}}"{{(r.Sha is null ? "" : $", \"digest\": \"sha256:{r.Sha}\"")}} }
              ]
            }
            """)) + "]";

    private static string Extension() => StudioUpdates.RuntimeId()?.StartsWith("win-", StringComparison.Ordinal) == true ? ".zip" : ".tar.gz";

    private static HttpClient Http(string json, byte[]? archive = null) => new(new FactorioDownloadsTests.FakeHttp(request =>
        request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive ?? new byte[] { 1, 2, 3 }) }));

    [Theory]
    [InlineData("0.2.0", false)]
    [InlineData("v0.3.0", false)]
    [InlineData("0.2.1", true)]
    [InlineData("v1.4.12", true)]
    public void Patch_above_zero_is_a_preview(string version, bool preview)
    {
        Assert.Equal(preview, StudioUpdates.IsPreview(version));
        Assert.Equal(preview ? UpdateChannel.Preview : UpdateChannel.Stable, StudioUpdates.DefaultChannel(version));
    }

    [Theory]
    [InlineData("0.2")]
    [InlineData("0.3.0-rc1")]
    [InlineData("latest")]
    [InlineData("")]
    public void Only_plain_versions_parse(string text) => Assert.Null(StudioUpdates.Parse(text));

    [Fact]
    public void Releases_skip_drafts_and_other_tags_and_keep_the_digest()
    {
        var json = ReleasesJson(("v0.3.0", false, "ABCDEF"), ("v0.3.1", true, null), ("nightly", false, null));
        var releases = StudioUpdates.ParseReleases(json);

        var release = Assert.Single(releases);
        Assert.Equal("0.3.0", release.Version);
        Assert.Equal("notes of v0.3.0", release.Notes);
        Assert.Equal("abcdef", Assert.Single(release.Assets).Sha256);
    }

    [Fact]
    public void Stable_sees_releases_only_and_preview_sees_both()
    {
        var releases = StudioUpdates.ParseReleases(ReleasesJson(("v0.2.0", false, null), ("v0.2.1", false, null), ("v0.2.2", false, null)));

        Assert.Null(StudioUpdates.Newest(releases, UpdateChannel.Stable, "0.2.0"));
        Assert.Equal("0.2.2", StudioUpdates.Newest(releases, UpdateChannel.Preview, "0.2.0")?.Version);
        Assert.Null(StudioUpdates.Newest(releases, UpdateChannel.Preview, "0.2.2"));

        var withRelease = StudioUpdates.ParseReleases(ReleasesJson(("v0.2.1", false, null), ("v0.3.0", false, null)));
        Assert.Equal("0.3.0", StudioUpdates.Newest(withRelease, UpdateChannel.Stable, "0.2.0")?.Version);
        Assert.Equal("0.3.0", StudioUpdates.Newest(withRelease, UpdateChannel.Preview, "0.2.1")?.Version);
    }

    [Fact]
    public void A_folder_with_both_programs_and_the_license_is_a_release_archive()
    {
        using var dir = new TempDir();
        Assert.Equal(InstallKind.Other, StudioUpdates.DetectKind(dir.Path));

        File.WriteAllText(dir.Combine("fmstudio" + Exe), "");
        File.WriteAllText(dir.Combine("FactorioModStudio" + Exe), "");
        File.WriteAllText(dir.Combine("LICENSE"), "");
        Assert.Equal(InstallKind.ReleaseArchive, StudioUpdates.DetectKind(dir.Path));

        Assert.Equal(InstallKind.DotnetTool, StudioUpdates.DetectKind("/home/me/.dotnet/tools/.store/factoriomodstudio.cli/0.2.0/factoriomodstudio.cli/0.2.0/tools/net10.0/any"));
        Assert.Equal(InstallKind.Other, StudioUpdates.DetectKind("/nix/store/abc-fmstudio/lib"));
    }

    [Fact]
    public async Task Check_reports_the_newer_release_and_the_tool_command()
    {
        using var dir = new TempDir();
        var updates = new StudioUpdates(Http(ReleasesJson(("v0.3.0", false, null))), new FakeRunner(_ => new ProcessResult(0, false, [])), dir.Path, "0.2.0");

        var check = await updates.CheckAsync(UpdateChannel.Stable);

        Assert.Equal("0.3.0", check.Newer?.Version);
        Assert.Equal(InstallKind.Other, check.Kind);
        Assert.Null(check.Command);
    }

    /// <summary>A release folder holding version 0.2.0, and the archive of 0.3.0 for this system (a zip on Windows, a "tar" elsewhere).</summary>
    private static (TempDir Dir, byte[] Archive, FakeRunner Tar) ReleaseFolder()
    {
        var dir = new TempDir();
        foreach (var name in new[] { "fmstudio" + Exe, "FactorioModStudio" + Exe, "LICENSE" }) File.WriteAllText(dir.Combine(name), "old " + name);
        Directory.CreateDirectory(dir.Combine("mods", "my-mod"));

        string[] files = ["fmstudio" + Exe, "FactorioModStudio" + Exe, "LICENSE", "README.md"];
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in files)
                using (var writer = new StreamWriter(zip.CreateEntry("fmstudio/" + name).Open()))
                    writer.Write("new " + name);

        var tar = new FakeRunner(args =>
        {
            foreach (var name in files)
            {
                Directory.CreateDirectory(System.IO.Path.Combine(args[3], "fmstudio"));
                File.WriteAllText(System.IO.Path.Combine(args[3], "fmstudio", name), "new " + name);
            }
            return new ProcessResult(0, false, []);
        });
        return (dir, buffer.ToArray(), tar);
    }

    [Fact]
    public async Task Apply_swaps_the_files_keeps_the_mods_and_leaves_old_ones_for_clean_up()
    {
        if (StudioUpdates.RuntimeId() is null) return; // no archive is published for this machine
        var (dir, archive, tar) = ReleaseFolder();
        using var _ = dir;
        var sha = Convert.ToHexStringLower(SHA256.HashData(archive));
        var updates = new StudioUpdates(Http(ReleasesJson(("v0.3.0", false, sha)), archive), tar, dir.Path, "0.2.0");
        Assert.Equal(InstallKind.ReleaseArchive, updates.Kind);

        var check = await updates.CheckAsync(UpdateChannel.Stable);
        await updates.ApplyAsync(check.Newer!);

        Assert.Equal("new fmstudio" + Exe, File.ReadAllText(dir.Combine("fmstudio" + Exe)));
        Assert.Equal("new README.md", File.ReadAllText(dir.Combine("README.md")));
        Assert.Equal("old LICENSE", File.ReadAllText(dir.Combine("LICENSE.old")));
        Assert.True(Directory.Exists(dir.Combine("mods", "my-mod")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(dir.Path), d => System.IO.Path.GetFileName(d).StartsWith(".update-", StringComparison.Ordinal));

        StudioUpdates.CleanUp(dir.Path);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path, "*.old"));
        Assert.True(File.Exists(dir.Combine("fmstudio" + Exe)));
    }

    [Fact]
    public async Task Apply_refuses_an_archive_with_the_wrong_checksum_and_changes_nothing()
    {
        if (StudioUpdates.RuntimeId() is null) return;
        var (dir, archive, tar) = ReleaseFolder();
        using var _ = dir;
        var updates = new StudioUpdates(Http(ReleasesJson(("v0.3.0", false, new string('0', 64))), archive), tar, dir.Path, "0.2.0");

        var check = await updates.CheckAsync(UpdateChannel.Stable);
        var error = await Assert.ThrowsAsync<FmsException>(() => updates.ApplyAsync(check.Newer!));

        Assert.Contains("checksum", error.Message);
        Assert.Equal("old fmstudio" + Exe, File.ReadAllText(dir.Combine("fmstudio" + Exe)));
        Assert.False(File.Exists(dir.Combine("README.md")));
    }

    [Fact]
    public async Task Apply_is_refused_for_a_copy_that_is_not_a_release_archive()
    {
        using var dir = new TempDir();
        var updates = new StudioUpdates(Http(ReleasesJson(("v0.3.0", false, null))), new FakeRunner(_ => new ProcessResult(0, false, [])), dir.Path, "0.2.0");
        var check = await updates.CheckAsync(UpdateChannel.Stable);

        await Assert.ThrowsAsync<FmsException>(() => updates.ApplyAsync(check.Newer!));
    }

    [Fact]
    public void The_channel_is_remembered_in_the_data_folder()
    {
        using var dir = new TempDir();
        Assert.Null(UpdateSettings.ReadChannel(dir.Path));
        Assert.Equal(UpdateChannel.Preview, UpdateSettings.Channel("0.2.1", dir.Path));

        UpdateSettings.WriteChannel(UpdateChannel.Stable, dir.Path);

        Assert.Equal(UpdateChannel.Stable, UpdateSettings.Channel("0.2.1", dir.Path));
    }

    // ------------------------------------------------------------------ the dialog: release date, cancelling a download

    [Fact]
    public void Releases_carry_their_publish_date_when_GitHub_gives_one()
    {
        var releases = StudioUpdates.ParseReleases("""
            [ { "tag_name": "v0.3.0", "published_at": "2026-11-11T18:20:00Z", "assets": [] },
              { "tag_name": "v0.2.0", "assets": [] } ]
            """);

        Assert.Equal(new DateTimeOffset(2026, 11, 11, 18, 20, 0, TimeSpan.Zero), releases[0].Published);
        Assert.Null(releases[1].Published);
    }

    [Fact]
    public async Task A_download_can_be_cancelled_and_changes_nothing()
    {
        if (StudioUpdates.RuntimeId() is null) return;
        var (dir, _, tar) = ReleaseFolder();
        using var _dir = dir;
        var json = ReleasesJson(("v0.3.0", false, null));
        var http = new HttpClient(new FactorioDownloadsTests.FakeHttp(request => request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }));
        var updater = new FactorioModStudio.Server.StudioUpdater(new StudioUpdates(http, tar, dir.Path, "0.2.0"), dir.Path);
        var (check, _) = await updater.CheckAsync(force: true, CancellationToken.None);

        Assert.NotNull(updater.Start(check!.Newer!));
        Assert.Null(updater.Start(check.Newer!)); // one download at a time
        await WaitFor(() => updater.Install is { Received: > 0 });
        Assert.True(updater.Cancel());
        await WaitFor(() => updater.Install is not { State: "downloading" });

        Assert.Equal("cancelled", updater.Install!.State);
        Assert.Equal("old fmstudio" + Exe, File.ReadAllText(dir.Combine("fmstudio" + Exe)));
        Assert.DoesNotContain(Directory.EnumerateDirectories(dir.Path), d => System.IO.Path.GetFileName(d).StartsWith(".update-", StringComparison.Ordinal));
        Assert.False(updater.Cancel()); // nothing left to cancel
        Assert.NotNull(updater.LastChecked);
    }

    [Fact]
    public async Task Prepare_puts_the_update_next_to_the_program_and_changes_nothing_that_runs()
    {
        if (StudioUpdates.RuntimeId() is null) return;
        var (dir, archive, tar) = ReleaseFolder();
        using var _dir = dir;
        var updates = new StudioUpdates(Http(ReleasesJson(("v0.3.0", false, null)), archive), tar, dir.Path, "0.2.0");
        var check = await updates.CheckAsync(UpdateChannel.Stable);

        Assert.Null(StudioUpdates.PendingVersion(dir.Path));
        await updates.PrepareAsync(check.Newer!);

        // The running program's files are untouched (a single-file program reads its own file on demand) ...
        Assert.Equal("old fmstudio" + Exe, File.ReadAllText(dir.Combine("fmstudio" + Exe)));
        Assert.False(File.Exists(dir.Combine("README.md")));
        // ... the new ones wait for the next start, and CleanUp leaves them alone.
        Assert.Equal("0.3.0", StudioUpdates.PendingVersion(dir.Path));
        Assert.Equal("new fmstudio" + Exe, File.ReadAllText(dir.Combine(".pending-update", "fmstudio", "fmstudio" + Exe)));
        StudioUpdates.CleanUp(dir.Path);
        Assert.Equal("0.3.0", StudioUpdates.PendingVersion(dir.Path));
        Assert.DoesNotContain(Directory.EnumerateDirectories(dir.Path), d => System.IO.Path.GetFileName(d).StartsWith(".update-", StringComparison.Ordinal));

        // A second download replaces the waiting one.
        await updates.PrepareAsync(check.Newer!);
        Assert.Equal("0.3.0", StudioUpdates.PendingVersion(dir.Path));

        // The server reports it as ready even without a download in this session.
        var updater = new FactorioModStudio.Server.StudioUpdater(updates, dir.Path);
        Assert.Equal(("0.3.0", "succeeded"), (updater.Install!.Version, updater.Install.State));
    }

    [Fact]
    public void Cancel_without_a_download_does_nothing()
    {
        using var dir = new TempDir();
        var updater = new FactorioModStudio.Server.StudioUpdater(
            new StudioUpdates(Http("[]"), new FakeRunner(_ => new ProcessResult(0, false, [])), dir.Path, "0.2.0"), dir.Path);
        Assert.False(updater.Cancel());
        Assert.Null(updater.Install);
        Assert.Null(updater.LastChecked);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "timed out");
    }

    /// <summary>A download that sends one byte and then hangs until it is cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent) { _sent = true; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
