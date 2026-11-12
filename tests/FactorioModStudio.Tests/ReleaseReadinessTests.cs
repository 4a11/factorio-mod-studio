using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;
using Xunit;

namespace FactorioModStudio.Tests;

public class ReleaseReadinessTests
{
    private static IReadOnlyList<ReadinessCheck> Eval(TempDir mod, string? dist = null) =>
        ReleaseReadiness.Evaluate(mod.Path, ModInspector.Inspect(mod.Path), dist);

    private static ReadinessCheck Check(IReadOnlyList<ReadinessCheck> checks, string id) => checks.Single(c => c.Id == id);

    [Fact]
    public void AlwaysSixChecks_InFixedOrder()
    {
        using var mod = Fixtures.ValidMod();
        Assert.Equal(new[] { "thumbnail", "changelog", "locale-name", "author", "errors", "packed" }, Eval(mod).Select(c => c.Id).ToArray());

        using var empty = new TempDir();
        Assert.Equal(6, ReleaseReadiness.Evaluate(empty.Path, ModInspector.Inspect(empty.Path), null).Count);
    }

    [Theory]
    [InlineData("Your Name", "warn")]
    [InlineData("  your name ", "warn")]
    [InlineData("", "warn")]
    [InlineData("Bobingabout", "ok")]
    public void Author_WarnsWhileItIsTheNewModPlaceholderOrEmpty(string author, string status)
    {
        using var mod = Fixtures.ValidMod();
        var info = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(mod.Combine("info.json")))!.AsObject();
        info["author"] = author;
        File.WriteAllText(mod.Combine("info.json"), info.ToJsonString());

        var check = Check(Eval(mod), "author");
        Assert.Equal(status, check.Status);
        if (status == "warn") Assert.Equal("info.json", check.File);
    }

    [Fact]
    public void Thumbnail_OkWhenPresent_WarnWhenMissing()
    {
        using var mod = Fixtures.ValidMod();
        Assert.Equal("ok", Check(Eval(mod), "thumbnail").Status);
        File.Delete(mod.Combine("thumbnail.png"));
        Assert.Equal("warn", Check(Eval(mod), "thumbnail").Status);
    }

    [Fact]
    public void Changelog_OkPointsAtEntry_WarnWhenMissingOrNoEntry()
    {
        using var mod = Fixtures.ValidMod();
        var dashes = new string('-', 99);
        mod.Write("changelog.txt", $"{dashes}\nVersion: 1.2.4\n  Bugfixes:\n    - x\n{dashes}\nVersion: 1.2.3\n  Features:\n    - y\n");
        var ok = Check(Eval(mod), "changelog");
        Assert.Equal(("ok", "1.2.3", "changelog.txt", 6), (ok.Status, ok.Version, ok.File, ok.Line));

        mod.Write("changelog.txt", $"{dashes}\nVersion: 1.0.0\n");
        var noEntry = Check(Eval(mod), "changelog");
        Assert.Equal(("warn", "1.2.3"), (noEntry.Status, noEntry.Version));
        Assert.Null(noEntry.File);

        File.Delete(mod.Combine("changelog.txt"));
        Assert.Equal(("warn", "1.2.3"), (Check(Eval(mod), "changelog").Status, Check(Eval(mod), "changelog").Version));
    }

    [Fact]
    public void LocaleName_OkOnlyWhenNameAndDescriptionInSameLanguage()
    {
        using var mod = Fixtures.ValidMod();
        Assert.Equal("warn", Check(Eval(mod), "locale-name").Status);

        mod.Write("locale/en/a.cfg", "[mod-name]\nmy-mod=My Mod\n");
        Assert.Equal("warn", Check(Eval(mod), "locale-name").Status);

        mod.Write("locale/de/a.cfg", "[mod-description]\nmy-mod=Beschreibung\n");
        Assert.Equal("warn", Check(Eval(mod), "locale-name").Status);

        mod.Write("locale/en/b.cfg", "[mod-description]\nmy-mod=Description\n");
        Assert.Equal("ok", Check(Eval(mod), "locale-name").Status);
    }

    [Fact]
    public void LocaleName_WrongKeyIsWarn()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("locale/en/a.cfg", "[mod-name]\nother=X\n[mod-description]\nother=Y\n");
        Assert.Equal("warn", Check(Eval(mod), "locale-name").Status);
    }

    [Fact]
    public void Errors_OkWhenNone_FailWithCount()
    {
        using var mod = Fixtures.ValidMod();
        var ok = Check(Eval(mod), "errors");
        Assert.Equal(("ok", 0), (ok.Status, ok.Count));

        var overview = ModInspector.Inspect(mod.Path) with
        {
            Issues = new[]
            {
                new ValidationIssue(Severity.Error, "X1", "a"),
                new ValidationIssue(Severity.Error, "X2", "b"),
                new ValidationIssue(Severity.Warning, "X3", "c")
            }
        };
        var fail = Check(ReleaseReadiness.Evaluate(mod.Path, overview, null), "errors");
        Assert.Equal(("fail", 2), (fail.Status, fail.Count));
    }

    [Fact]
    public void Packed_InfoWithoutDistOrZip()
    {
        using var mod = Fixtures.ValidMod();
        using var dist = new TempDir();
        var noDist = Check(Eval(mod), "packed");
        Assert.Equal(("info", "1.2.3"), (noDist.Status, noDist.Version));
        Assert.Equal("info", Check(Eval(mod, dist.Path), "packed").Status);

        dist.Write("my-mod_1.2.2.zip", "old");
        Assert.Equal("info", Check(Eval(mod, dist.Path), "packed").Status);
    }

    [Fact]
    public void Packed_OkWhenZipNewer_WarnWhenFileChangedAfterPacking()
    {
        using var mod = Fixtures.ValidMod();
        using var dist = new TempDir();
        var zip = dist.Write("my-mod_1.2.3.zip", "zip");
        var t0 = DateTime.UtcNow.AddHours(-1);
        foreach (var f in ModFiles.Enumerate(mod.Path)) File.SetLastWriteTimeUtc(mod.Combine(f.Split('/')), t0);
        File.SetLastWriteTimeUtc(zip, t0.AddMinutes(10));
        Assert.Equal("ok", Check(Eval(mod, dist.Path), "packed").Status);

        File.SetLastWriteTimeUtc(mod.Combine("data.lua"), t0.AddMinutes(20));
        var warn = Check(Eval(mod, dist.Path), "packed");
        Assert.Equal(("warn", "1.2.3"), (warn.Status, warn.Version));
    }

    [Fact]
    public void Packed_IgnoresDistInsideModAndToolingFiles()
    {
        using var mod = Fixtures.ValidMod();
        var dist = mod.Combine("dist");
        var zip = mod.Write("dist/my-mod_1.2.3.zip", "zip");
        var t0 = DateTime.UtcNow.AddHours(-1);
        foreach (var f in ModFiles.Enumerate(mod.Path, dist)) File.SetLastWriteTimeUtc(mod.Combine(f.Split('/')), t0);
        File.SetLastWriteTimeUtc(zip, t0.AddMinutes(10));
        mod.Write("dist/notes.txt", "later"); // inside dist: not a mod file
        mod.Write(".git/HEAD", "later");      // tooling: not a mod file
        Assert.Equal("ok", Check(Eval(mod, dist), "packed").Status);
    }

    [Fact]
    public async Task Api_ModOverviewIncludesSixReadinessItems()
    {
        using var mod = Fixtures.ValidMod();
        await using var ui = await UiServer.StartAsync(
            new ProjectContext(mod.Path, new ProjectConfig(), configFileExists: false),
            new UiOptions(Port: 0));
        using var http = new HttpClient { BaseAddress = new Uri(ui.Url) };
        http.DefaultRequestHeaders.Add("X-FMS-Token", ui.Token);

        var mods = JsonDocument.Parse(await http.GetStringAsync("/api/mods")).RootElement;
        var id = mods.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "my-mod").GetProperty("id").GetString();
        var overview = JsonDocument.Parse(await http.GetStringAsync($"/api/mods/{id}")).RootElement;

        var items = overview.GetProperty("readiness").EnumerateArray().ToList();
        Assert.Equal(new[] { "thumbnail", "changelog", "locale-name", "author", "errors", "packed" }, items.Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(new[] { "ok", "ok", "warn", "ok", "ok", "info" }, items.Select(i => i.GetProperty("status").GetString()!).ToArray());
        Assert.Equal("changelog.txt", items[1].GetProperty("file").GetString());
        Assert.Equal(0, items[4].GetProperty("count").GetInt32());

        // Packing creates dist/<name>_<version>.zip; the next request must see it (not cached).
        var dist = System.IO.Path.Combine(mod.Path, "dist");
        Directory.CreateDirectory(dist);
        File.WriteAllText(System.IO.Path.Combine(dist, "my-mod_1.2.3.zip"), "zip");
        File.SetLastWriteTimeUtc(System.IO.Path.Combine(dist, "my-mod_1.2.3.zip"), DateTime.UtcNow.AddHours(1));
        overview = JsonDocument.Parse(await http.GetStringAsync($"/api/mods/{id}")).RootElement;
        Assert.Equal("ok", overview.GetProperty("readiness")[5].GetProperty("status").GetString());
    }
}
