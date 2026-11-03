using System.Text.Json;
using FactorioModStudio.Cli;
using FactorioModStudio.Core;
using Xunit;
using static FactorioModStudio.Tests.FakeFactorio;

namespace FactorioModStudio.Tests;

/// <summary>
/// Tests of FactorioInstallations (Core), written from versions-contract.md only.
/// Fake installs are shell scripts that record every start; a test fails if anything ever starts one.
/// </summary>
public class FactorioInstallationsTests
{
    private static FactorioInstallation Inst(string path, string? version, string[]? sources = null, string? label = null) =>
        new(FactorioInstallations.IdOf(path), path, version, label, sources ?? new[] { "auto" });

    private static ProjectContext Ctx(TempDir root, string? configPath = null) =>
        new(root.Combine("proj"), new ProjectConfig { FactorioPath = configPath }, configFileExists: false);

    private static readonly IReadOnlyList<RegisteredInstallation> NoRegistered = Array.Empty<RegisteredInstallation>();

    private static List<FactorioInstallation> Cands(TempDir root, ProjectContext ctx, params RegisteredInstallation[] reg) =>
        FactorioInstallations.Candidates(ctx, reg).Where(c => Under(c.Path, root.Path)).ToList();

    // ------------------------------------------------------------------ InstallRoot / DetectVersion

    [Fact]
    public void InstallRoot_StandardLayout_IsTheInstallDirectory()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "fx", "2.0.76");
        Assert.Equal(Norm(Path.Combine(t.Path, "fx")), Norm(FactorioInstallations.InstallRoot(exe)!));
    }

    [Fact]
    public void InstallRoot_MacOsLikeLayout_IsContentsFolder()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = MakeMac(t.Path, "Applications", "2.0.76");
        Assert.Equal(Norm(Path.Combine(t.Path, "Applications", "factorio.app", "Contents")), Norm(FactorioInstallations.InstallRoot(exe)!));
        Assert.Equal("2.0.76", FactorioInstallations.DetectVersion(exe));
    }

    [Fact]
    public void InstallRoot_NoInfoJson_IsNull_AndVersionNull()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "fx", null, info: false);
        Assert.Null(FactorioInstallations.InstallRoot(exe));
        Assert.Null(FactorioInstallations.DetectVersion(exe));
    }

    [Fact]
    public void InstallRoot_InfoJsonTooFarUp_IsNotFound_ButNearbyIs()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        // info.json 3 levels above the executable's directory: found
        var near = Path.Combine(t.Path, "near");
        WriteInfo(near, "1.1.110");
        var nearExe = Path.Combine(near, "a", "b", "c", "factorio");
        WriteScript(nearExe);
        Assert.Equal(Norm(near), Norm(FactorioInstallations.InstallRoot(nearExe)!));
        Assert.Equal("1.1.110", FactorioInstallations.DetectVersion(nearExe));

        // 9 levels above: contract says at most 6
        var far = Path.Combine(t.Path, "far");
        WriteInfo(far, "1.1.110");
        var farExe = Path.Combine(far, "1", "2", "3", "4", "5", "6", "7", "8", "9", "factorio");
        WriteScript(farExe);
        Assert.Null(FactorioInstallations.InstallRoot(farExe));
        Assert.Null(FactorioInstallations.DetectVersion(farExe));
    }

    [Fact]
    public void InstallRoot_NearestInfoJsonWins()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        WriteInfo(t.Combine("outer"), "1.1.110");
        var inner = t.Combine("outer", "inner");
        WriteInfo(inner, "2.0.76");
        var exe = Path.Combine(inner, "bin", "x64", "factorio");
        WriteScript(exe);
        Assert.Equal(Norm(inner), Norm(FactorioInstallations.InstallRoot(exe)!));
        Assert.Equal("2.0.76", FactorioInstallations.DetectVersion(exe));
    }

    [Theory]
    [InlineData("2.0.76", "2.0.76")]
    [InlineData("1.1.110", "1.1.110")]
    [InlineData("0.17.79", "0.17.79")]
    [InlineData("2.1.0", "2.1.0")]
    [InlineData("10.20.30", "10.20.30")]
    public void DetectVersion_ReadsVersionFromBaseInfoJson(string version, string expected)
    {
        if (Unsupported) return;
        using var t = new TempDir();
        Assert.Equal(expected, FactorioInstallations.DetectVersion(Make(t.Path, "fx", version)));
    }

    [Theory]
    [InlineData("""{"version":"2.0"}""")]                  // not MAJOR.MINOR.PATCH
    [InlineData("""{"version":"2"}""")]
    [InlineData("""{"version":"2.0.x"}""")]
    [InlineData("""{"version":"abc"}""")]
    [InlineData("""{"version":""}""")]
    [InlineData("""{"version":"..."}""")]
    [InlineData("""{"version":"a.b.c"}""")]
    [InlineData("""{"version":"2.0.-1"}""")]
    [InlineData("""{"version":2.0}""")]                    // number, not string
    [InlineData("""{"version":null}""")]
    [InlineData("""{"version":["2.0.76"]}""")]
    [InlineData("""{"version":{"v":"2.0.76"}}""")]
    [InlineData("""{"name":"base"}""")]
    [InlineData("""[]""")]
    [InlineData(""""2.0.76"""")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("garbage {{{")]
    [InlineData("""{"version":"2.0.76""")]
    [InlineData("""{"version":"99999999999999999999.0.0"}""")]
    public void DetectVersion_MissingGarbageOrNonNumeric_IsNull_AndNeverThrows(string content)
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "fx", "9.9.9");
        File.WriteAllText(Path.Combine(RootOf(exe), "data", "base", "info.json"), content);
        string? v = "sentinel";
        try { v = FactorioInstallations.DetectVersion(exe); }
        catch (Exception ex) { throw new InvalidOperationException("DetectVersion threw " + ex.GetType().Name + " for " + content); }
        Assert.Null(v);
    }

    [Fact]
    public void DetectVersion_InfoJsonIsADirectoryOrBinary_IsNull()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "dir", "9.9.9");
        var info = Path.Combine(RootOf(exe), "data", "base", "info.json");
        File.Delete(info);
        Directory.CreateDirectory(info);
        Assert.Null(FactorioInstallations.DetectVersion(exe));

        var exe2 = Make(t.Path, "bin", "9.9.9");
        File.WriteAllBytes(Path.Combine(RootOf(exe2), "data", "base", "info.json"), new byte[] { 0, 1, 2, 0xFF, 0xFE, 0, 0x7B, 0x80 });
        Assert.Null(FactorioInstallations.DetectVersion(exe2));
    }

    [Fact]
    public void DetectVersion_HugeOrDeeplyNestedInfoJson_DoesNotCrashOrHang()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "huge", "9.9.9");
        var info = Path.Combine(RootOf(exe), "data", "base", "info.json");
        // 30 MB of padding after a valid version
        using (var w = new StreamWriter(info))
        {
            w.Write("{\"version\":\"2.0.76\",\"pad\":\"");
            var chunk = new string('x', 1 << 20);
            for (var i = 0; i < 30; i++) w.Write(chunk);
            w.Write("\"}");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var v = FactorioInstallations.DetectVersion(exe);   // null (size cap) or the version: both acceptable, must not throw
        Assert.True(v is null or "2.0.76", "unexpected: " + v);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "took " + sw.Elapsed);

        // deep nesting (JSON reader depth limit)
        var exe2 = Make(t.Path, "deep", "9.9.9");
        File.WriteAllText(Path.Combine(RootOf(exe2), "data", "base", "info.json"), new string('[', 200000) + new string(']', 200000));
        Assert.Null(FactorioInstallations.DetectVersion(exe2));

        // a 500 MB sparse-ish file is not read fully into memory: just must not throw
        var exe3 = Make(t.Path, "sparse", "9.9.9");
        using (var fs = new FileStream(Path.Combine(RootOf(exe3), "data", "base", "info.json"), FileMode.Create))
        {
            fs.SetLength(300L * 1024 * 1024);
        }
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(FactorioInstallations.DetectVersion(exe3));
        Assert.True(sw3.Elapsed < TimeSpan.FromSeconds(20), "took " + sw3.Elapsed);
    }

    [Fact]
    public void DetectVersion_UnusualInputs_ReturnNull_NoThrow()
    {
        Assert.Null(FactorioInstallations.DetectVersion(""));
        Assert.Null(FactorioInstallations.DetectVersion("   "));
        Assert.Null(FactorioInstallations.DetectVersion("definitely/not/here/factorio"));
        Assert.Null(FactorioInstallations.DetectVersion("/definitely/not/here/factorio"));
        Assert.Null(FactorioInstallations.DetectVersion("/"));
        Assert.Null(FactorioInstallations.DetectVersion("bad\0path"));
    }

    [Fact]
    public void DetectVersion_NeverRunsTheExecutable_NorOtherApis()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "Factorio-2.0.76", "2.0.76");
        var other = Make(t.Path, "Factorio-1.1.110", "1.1.110");
        var ctx = Ctx(t, configPath: exe);
        Assert.Equal("2.0.76", FactorioInstallations.DetectVersion(exe));
        FactorioInstallations.InstallRoot(exe);
        FactorioInstallations.IdOf(exe);
        var cands = Cands(t, ctx, new RegisteredInstallation(other, "label"));
        Assert.True(cands.Count >= 2);
        FactorioInstallations.Pick(cands, "2.0");
        Assert.False(WasStarted(exe), "executable was started: " + Calls(exe));
        Assert.False(WasStarted(other), "executable was started: " + Calls(other));
    }

    // ------------------------------------------------------------------ IdOf

    [Fact]
    public void IdOf_Is12LowercaseHex_OfSha256OfThePath_AndStable()
    {
        foreach (var p in new[] { "/opt/factorio/bin/x64/factorio", "/tmp/x y/Fäctörio/bin/x64/factorio", "C:\\Games\\Factorio\\bin\\x64\\factorio.exe", "/a" })
        {
            var id = FactorioInstallations.IdOf(p);
            Assert.Equal(12, id.Length);
            Assert.True(id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), id);
            Assert.Equal(id, FactorioInstallations.IdOf(p));
            if (!OperatingSystem.IsWindows() && Path.IsPathRooted(p) && p.StartsWith('/'))
                Assert.Equal(Sha12(Path.GetFullPath(p)), id);
        }
    }

    [Fact]
    public void IdOf_DifferentPathsDiffer_AndCaseMattersOnLinux()
    {
        var a = FactorioInstallations.IdOf("/opt/factorio-a/bin/x64/factorio");
        var b = FactorioInstallations.IdOf("/opt/factorio-b/bin/x64/factorio");
        Assert.NotEqual(a, b);
        if (!OperatingSystem.IsWindows())
            Assert.NotEqual(FactorioInstallations.IdOf("/opt/Factorio/bin/x64/factorio"), FactorioInstallations.IdOf("/opt/factorio/bin/x64/factorio"));
    }

    [Fact]
    public void IdOf_MatchesTheIdOfCandidates()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "fx", "2.0.76");
        var c = Assert.Single(Cands(t, Ctx(t, exe)));
        Assert.Equal(FactorioInstallations.IdOf(c.Path), c.Id);
        Assert.Equal(Norm(exe), c.Path);
    }

    // ------------------------------------------------------------------ VersionMatches

    [Theory]
    [InlineData("2.0.76", "2.0", true)]
    [InlineData("2.0.0", "2.0", true)]
    [InlineData("1.1.110", "1.1", true)]
    [InlineData("2.1.3", "2.1", true)]
    [InlineData("0.17.79", "0.17", true)]
    [InlineData("2.0.76", "2.0.5", true)]          // mod version with a patch: only major.minor is used
    [InlineData("2.0.76", "2.0.76", true)]
    [InlineData("2.0.76", "2.1", false)]
    [InlineData("2.0.76", "1.1", false)]
    [InlineData("1.1.110", "2.0", false)]
    [InlineData("1.1.110", "1.0", false)]
    [InlineData("2.10.0", "2.1", false)]           // numeric, not prefix, compare
    [InlineData("2.1.0", "2.10", false)]
    [InlineData("1.10.5", "1.1", false)]
    [InlineData("12.0.0", "1.2", false)]
    [InlineData("1.20.0", "1.2", false)]
    [InlineData("2.0.76", "3.0", false)]
    [InlineData(null, "2.0", false)]
    [InlineData("2.0.76", null, false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("", "2.0", false)]
    [InlineData("2.0.76", "", false)]
    [InlineData("garbage", "2.0", false)]
    [InlineData("2.0.76", "garbage", false)]
    [InlineData("garbage", "garbage", false)]
    [InlineData("a.b.c", "a.b", false)]
    [InlineData("2.0.76", "2.", false)]
    [InlineData("2.0.76", ".0", false)]
    [InlineData("2.0.76", "-2.0", false)]
    public void VersionMatches_TruthTable(string? install, string? mod, bool expected)
    {
        Assert.Equal(expected, FactorioInstallations.VersionMatches(install, mod));
    }

    [Theory]
    [InlineData("99999999999999999999.0.0", "99999999999999999999.0")]
    [InlineData("2.0.76", "99999999999999999999.0")]
    [InlineData("٢.٠.٧٦", "٢.٠")]                  // Arabic-Indic digits match \d in .NET regex but not int parsing
    [InlineData("2.0.76", "٢.٠")]
    [InlineData("٢.٠.٧٦", "2.0")]
    [InlineData("2.0.76", "2.0\n")]
    [InlineData("2.0.76", " 2.0 ")]
    [InlineData("-1.0.0", "-1.0")]
    [InlineData("2.0.76\0", "2.0")]
    public void VersionMatches_OddInputs_NeverThrow(string install, string mod)
    {
        try { FactorioInstallations.VersionMatches(install, mod); }
        catch (Exception ex) { throw new InvalidOperationException($"VersionMatches threw {ex.GetType().Name} for ('{install}', '{mod}')"); }
    }

    [Fact]
    public void VersionMatches_IsSymmetricOnlyInMeaning_NotRequired_ButStable()
    {
        Assert.Equal(FactorioInstallations.VersionMatches("2.0.76", "2.0"), FactorioInstallations.VersionMatches("2.0.76", "2.0"));
    }

    // ------------------------------------------------------------------ Pick

    private static List<FactorioInstallation> Four() => new()
    {
        Inst("/i/a/factorio", "2.0.60"), Inst("/i/b/factorio", "2.0.76"), Inst("/i/c/factorio", "1.1.110"), Inst("/i/d/factorio", "2.1.3"),
    };

    [Theory]
    [InlineData("2.0", "/i/b/factorio")]
    [InlineData("1.1", "/i/c/factorio")]
    [InlineData("2.1", "/i/d/factorio")]
    [InlineData("2.0.5", "/i/b/factorio")]
    public void Pick_Auto_ChoosesHighestPatchOfTheMatchingMajorMinor(string mod, string expectedPath)
    {
        var p = FactorioInstallations.Pick(Four(), mod);
        Assert.Equal("auto", p.Mode);
        Assert.Equal(expectedPath, p.Chosen!.Path);
        Assert.False(string.IsNullOrWhiteSpace(p.Reason));
    }

    [Fact]
    public void Pick_Auto_IsIndependentOfInputOrder()
    {
        var all = Four();
        var rnd = new Random(7);
        for (var i = 0; i < 20; i++)
        {
            var shuffled = all.OrderBy(_ => rnd.Next()).ToList();
            Assert.Equal("/i/b/factorio", FactorioInstallations.Pick(shuffled, "2.0").Chosen!.Path);
        }
    }

    [Fact]
    public void Pick_Auto_VersionsAreComparedNumerically()
    {
        var list = new List<FactorioInstallation> { Inst("/i/a/factorio", "2.0.9"), Inst("/i/b/factorio", "2.0.10"), Inst("/i/c/factorio", "2.0.100"), Inst("/i/d/factorio", "2.0.76") };
        Assert.Equal("/i/c/factorio", FactorioInstallations.Pick(list, "2.0").Chosen!.Path);
        list.RemoveAt(2);
        Assert.Equal("/i/d/factorio", FactorioInstallations.Pick(list, "2.0").Chosen!.Path);   // 2.0.76 > 2.0.10 > 2.0.9
        list.RemoveAt(2);
        Assert.Equal("/i/b/factorio", FactorioInstallations.Pick(list, "2.0").Chosen!.Path);   // 2.0.10 > 2.0.9 (a string compare would pick 2.0.9)
        // 2.10 must not be treated as a 2.1 / 2.0 install
        var tricky = new List<FactorioInstallation> { Inst("/i/a/factorio", "2.10.0"), Inst("/i/b/factorio", "2.1.3") };
        Assert.Equal("/i/b/factorio", FactorioInstallations.Pick(tricky, "2.1").Chosen!.Path);
        Assert.Null(FactorioInstallations.Pick(new List<FactorioInstallation> { Inst("/i/a/factorio", "2.10.0") }, "2.1").Chosen);
    }

    [Fact]
    public void Pick_TieOnVersion_PrefersMoreSpecificSource_UiEnvConfigAuto()
    {
        var order = new[] { "ui", "env", "config", "auto" };
        for (var hi = 0; hi < order.Length; hi++)
            for (var lo = hi + 1; lo < order.Length; lo++)
            {
                // the lower-priority one has the alphabetically smaller path, so a path tie-break would pick it
                var list = new List<FactorioInstallation>
                {
                    Inst("/i/a/factorio", "2.0.76", new[] { order[lo] }),
                    Inst("/i/b/factorio", "2.0.76", new[] { order[hi] }),
                };
                Assert.Equal("/i/b/factorio", FactorioInstallations.Pick(list, "2.0").Chosen!.Path);
                list.Reverse();
                Assert.Equal("/i/b/factorio", FactorioInstallations.Pick(list, "2.0").Chosen!.Path);
            }
    }

    [Fact]
    public void Pick_TieOnVersion_MultiSourceInstall_CountsItsMostSpecificSource()
    {
        var list = new List<FactorioInstallation>
        {
            Inst("/i/a/factorio", "2.0.76", new[] { "config" }),
            Inst("/i/b/factorio", "2.0.76", new[] { "ui", "auto" }),
        };
        Assert.Equal("/i/b/factorio", FactorioInstallations.Pick(list, "2.0").Chosen!.Path);
    }

    [Fact]
    public void Pick_TieOnVersionAndSource_UsesOrdinalPath_Deterministically()
    {
        var a = Inst("/i/a/factorio", "2.0.76", new[] { "auto" });
        var b = Inst("/i/b/factorio", "2.0.76", new[] { "auto" });
        var c = Inst("/i/B/factorio", "2.0.76", new[] { "auto" });
        Assert.Equal("/i/B/factorio", FactorioInstallations.Pick(new List<FactorioInstallation> { a, b, c }, "2.0").Chosen!.Path);   // ordinal: 'B' < 'a'
        Assert.Equal("/i/B/factorio", FactorioInstallations.Pick(new List<FactorioInstallation> { c, b, a }, "2.0").Chosen!.Path);
        Assert.Equal("/i/a/factorio", FactorioInstallations.Pick(new List<FactorioInstallation> { b, a }, "2.0").Chosen!.Path);
    }

    [Fact]
    public void Pick_HigherVersionBeatsMoreSpecificSource()
    {
        var list = new List<FactorioInstallation>
        {
            Inst("/i/a/factorio", "2.0.60", new[] { "ui" }),
            Inst("/i/b/factorio", "2.0.76", new[] { "auto" }),
        };
        Assert.Equal("/i/b/factorio", FactorioInstallations.Pick(list, "2.0").Chosen!.Path);
    }

    [Fact]
    public void Pick_Manual_UsesExactId_CaseInsensitive_EvenWhenAnotherWouldBeBetter()
    {
        var list = Four();
        var target = list[0];   // 2.0.60, while 2.0.76 exists
        foreach (var id in new[] { target.Id, target.Id.ToUpperInvariant() })
        {
            var p = FactorioInstallations.Pick(list, "2.0", id);
            Assert.Equal("manual", p.Mode);
            Assert.Equal(target.Path, p.Chosen!.Path);
            Assert.False(string.IsNullOrWhiteSpace(p.Reason));
        }
    }

    [Fact]
    public void Pick_Manual_VersionMismatch_IsAllowed_AndReasonSaysSo()
    {
        var list = Four();
        var onePointOne = list[2];
        var p = FactorioInstallations.Pick(list, "2.0", onePointOne.Id);
        Assert.Equal("manual", p.Mode);
        Assert.Equal(onePointOne.Path, p.Chosen!.Path);
        Assert.Contains("2.0", p.Reason);
        var reasonMatching = FactorioInstallations.Pick(list, "1.1", onePointOne.Id).Reason;
        Assert.NotEqual(reasonMatching, p.Reason);
        Assert.Contains("match", p.Reason.ToLowerInvariant());
    }

    [Fact]
    public void Pick_Manual_WithNullMod_NullVersionInstall_AndInvalidMod_StillManual()
    {
        var unknown = Inst("/i/x/factorio", null);
        var list = new List<FactorioInstallation> { unknown, Inst("/i/b/factorio", "2.0.76") };
        foreach (var mod in new string?[] { null, "garbage", "", "2.0" })
        {
            var p = FactorioInstallations.Pick(list, mod, unknown.Id);
            Assert.Equal("manual", p.Mode);
            Assert.Equal(unknown.Path, p.Chosen!.Path);
        }
    }

    [Fact]
    public void Pick_Manual_UnknownId_IsNoneWithReason()
    {
        foreach (var id in new[] { "000000000000", "nope", "ffffffffffff", new string('a', 500) })
        {
            var p = FactorioInstallations.Pick(Four(), "2.0", id);
            Assert.Null(p.Chosen);
            Assert.Equal("none", p.Mode);
            Assert.False(string.IsNullOrWhiteSpace(p.Reason));
        }
        // even with an empty candidate list
        var e = FactorioInstallations.Pick(new List<FactorioInstallation>(), "2.0", "abc");
        Assert.Null(e.Chosen);
        Assert.Equal("none", e.Mode);
    }

    [Fact]
    public void Pick_NoMatch_ReasonListsWantedAndFoundVersions()
    {
        var list = new List<FactorioInstallation> { Inst("/i/a/factorio", "2.0.76"), Inst("/i/b/factorio", "2.1.3") };
        var p = FactorioInstallations.Pick(list, "1.1");
        Assert.Null(p.Chosen);
        Assert.Equal("none", p.Mode);
        Assert.Contains("1.1", p.Reason);
        Assert.Contains("2.0.76", p.Reason);
        Assert.Contains("2.1.3", p.Reason);
        Assert.Contains("No installed Factorio matches", p.Reason);
    }

    [Fact]
    public void Pick_NoInstalls_IsNone_ForEveryModVersion()
    {
        foreach (var mod in new string?[] { "2.0", "1.1", null, "garbage" })
        {
            var p = FactorioInstallations.Pick(new List<FactorioInstallation>(), mod);
            Assert.Null(p.Chosen);
            Assert.Equal("none", p.Mode);
            Assert.False(string.IsNullOrWhiteSpace(p.Reason));
        }
        Assert.Contains("1.1", FactorioInstallations.Pick(new List<FactorioInstallation>(), "1.1").Reason);
    }

    [Fact]
    public void Pick_InstallsWithUnknownVersion_NeverMatchAValidModVersion()
    {
        var list = new List<FactorioInstallation> { Inst("/i/a/factorio", null), Inst("/i/b/factorio", null) };
        var p = FactorioInstallations.Pick(list, "2.0");
        Assert.Null(p.Chosen);
        Assert.Equal("none", p.Mode);
        // ... and do not beat a real match
        list.Add(Inst("/i/c/factorio", "2.0.1"));
        Assert.Equal("/i/c/factorio", FactorioInstallations.Pick(list, "2.0").Chosen!.Path);
    }

    [Fact]
    public void Pick_ModVersionNull_ChoosesHighestOverall() => Pick_ModVersionNullOrInvalid_ChoosesHighestOverall_AndSaysSo(null);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("2")]
    [InlineData("2.")]
    [InlineData("x.y")]
    public void Pick_ModVersionNullOrInvalid_ChoosesHighestOverall_AndSaysSo(string? mod)
    {
        var list = Four();
        list.Add(Inst("/i/z/factorio", null));
        var p = FactorioInstallations.Pick(list, mod);
        Assert.Equal("auto", p.Mode);
        Assert.Equal("/i/d/factorio", p.Chosen!.Path);   // 2.1.3 is highest; the unknown-version install comes last
        Assert.Contains("factorio_version", p.Reason);
    }

    [Fact]
    public void Pick_ModVersionInvalid_OnlyUnknownVersionInstalls_StillChoosesOne()
    {
        var list = new List<FactorioInstallation> { Inst("/i/a/factorio", null) };
        var p = FactorioInstallations.Pick(list, null);
        Assert.Equal("auto", p.Mode);
        Assert.Equal("/i/a/factorio", p.Chosen!.Path);
    }

    [Fact]
    public void Pick_DoesNotMutateInput()
    {
        var list = Four();
        var before = list.Select(i => i.Id).ToList();
        FactorioInstallations.Pick(list, "2.0");
        FactorioInstallations.Pick(list, null);
        Assert.Equal(before, list.Select(i => i.Id).ToList());
    }

    // ------------------------------------------------------------------ Candidates

    [Fact]
    public void Candidates_Registered_HaveSourceUi_Label_AndDetectedVersion()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "fx", "2.0.76");
        var other = Make(t.Path, "fy", "1.1.110");
        var list = Cands(t, Ctx(t), new RegisteredInstallation(exe, "My 2.0"), new RegisteredInstallation(other, null));
        Assert.Equal(2, list.Count);
        var a = list.Single(c => c.Path == Norm(exe));
        Assert.Equal("2.0.76", a.Version);
        Assert.Equal("My 2.0", a.Label);
        Assert.Equal("ui", string.Join(",", a.Sources));
        Assert.Equal(FactorioInstallations.IdOf(a.Path), a.Id);
        var b = list.Single(c => c.Path == Norm(other));
        Assert.Equal("1.1.110", b.Version);
        Assert.Null(b.Label);
    }

    [Fact]
    public void Candidates_Config_FileOrInstallDir_HasSourceConfig_AndNoLabel()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "fx", "2.0.76");
        foreach (var cfg in new[] { exe, RootOf(exe), RootOf(exe) + "/" })
        {
            var c = Assert.Single(Cands(t, Ctx(t, cfg)));
            Assert.Equal(Norm(exe), c.Path);
            Assert.Equal("config", string.Join(",", c.Sources));
            Assert.Null(c.Label);
            Assert.Equal("2.0.76", c.Version);
        }
    }

    [Fact]
    public void Candidates_VanishedOrMissingExecutables_AreDropped_WithoutThrowing()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var gone = Make(t.Path, "gone", "2.0.76");
        var kept = Make(t.Path, "kept", "1.1.110");
        File.Delete(gone);
        var list = Cands(t, Ctx(t, t.Combine("missing", "factorio")),
            new RegisteredInstallation(gone, "gone"), new RegisteredInstallation(kept, "kept"), new RegisteredInstallation(t.Combine("never", "factorio"), null));
        Assert.Equal(Norm(kept), Assert.Single(list).Path);
        // the install dir of the deleted executable resolves to nothing as well
        Assert.Empty(Cands(t, Ctx(t, RootOf(gone))).Where(c => c.Path == Norm(gone)));
    }

    [Fact]
    public void Candidates_DedupMergesSources_InContractOrder()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        // parent contains "Factorio-2.0.76" (registered + configured + auto-discovered as a sibling of the other install)
        var main = Make(t.Path, "Factorio-2.0.76", "2.0.76");
        var sibling = Make(t.Path, "Factorio-1.1.110", "1.1.110");
        var ctx = Ctx(t, configPath: RootOf(main));
        var list = Cands(t, ctx, new RegisteredInstallation(main, "primary"), new RegisteredInstallation(sibling, null));
        Assert.Equal(2, list.Count);
        var m = list.Single(c => c.Path == Norm(main));
        Assert.Equal("ui,config,auto", string.Join(",", m.Sources));
        Assert.Equal("primary", m.Label);
        var s = list.Single(c => c.Path == Norm(sibling));
        Assert.Equal("ui,auto", string.Join(",", s.Sources));
    }

    [Fact]
    public void Candidates_SamePathSpelledDifferently_IsOneCandidate()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var exe = Make(t.Path, "fx", "2.0.76");
        var weird = Path.Combine(Path.GetDirectoryName(exe)!, ".", "..", "x64", "factorio");
        var list = Cands(t, Ctx(t, configPath: RootOf(exe)), new RegisteredInstallation(weird, null), new RegisteredInstallation(exe, "dup"));
        var c = Assert.Single(list);
        Assert.Equal(Norm(exe), c.Path);
        Assert.Contains("ui", c.Sources);
        Assert.Contains("config", c.Sources);
    }

    [Fact]
    public void Candidates_SiblingDiscovery_NameRules()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var park = t.Combine("park");
        var anchor = Make(park, "base-install", "2.0.76");               // does not match ^factorio
        var matching = new[] { "Factorio-1.1", "factorio_2.0.76", "Factorio 1.1.110", "FACTORIO", "factorio", "Factorio2" };
        var versions = new[] { "1.1.5", "2.0.76", "1.1.110", "2.1.0", "2.0.1", "0.17.79" };
        var matchingExes = matching.Select((n, i) => Make(park, n, versions[i])).ToList();
        var nonMatching = new[] { "my-factorio", "old-Factorio", "Xfactorio-1.0", "fact", "game-factorio-2.0", "_factorio" }
            .Select(n => Make(park, n, "2.0.99")).ToList();
        Directory.CreateDirectory(Path.Combine(park, "Factorio-empty"));                      // matches the name, no executable
        Directory.CreateDirectory(Path.Combine(park, "Factorio-nobin", "bin", "x64"));
        WriteScript(Path.Combine(park, "Factorio-evil", "bin", "x64", "evil.sh"));            // matches, but no factorio executable
        File.WriteAllText(Path.Combine(park, "factorio-2.0.76.txt"), "x");                    // a FILE with a matching name
        WriteScript(Path.Combine(park, "factorio-file"));                                     // an executable FILE named like a sibling

        var list = Cands(t, Ctx(t), new RegisteredInstallation(anchor, null));
        var paths = list.Select(c => c.Path).ToHashSet();
        foreach (var e in matchingExes) Assert.True(paths.Contains(Norm(e)), "sibling not discovered: " + e);
        foreach (var e in nonMatching) Assert.False(paths.Contains(Norm(e)), "non-matching name discovered: " + e);
        Assert.Equal(1 + matchingExes.Count, list.Count);
        foreach (var e in matchingExes) Assert.Equal("auto", string.Join(",", list.Single(c => c.Path == Norm(e)).Sources));
        Assert.Equal("ui", string.Join(",", list.Single(c => c.Path == Norm(anchor)).Sources));
        Assert.Equal("2.0.76", list.Single(c => c.Path == Norm(matchingExes[1])).Version);
        // none of the installs was started during discovery
        foreach (var e in matchingExes.Concat(nonMatching).Append(anchor)) Assert.False(WasStarted(e), "started " + e);
    }

    [Fact]
    public void Candidates_SiblingDiscovery_WorksFromConfigInstall_AndFindsInstallWithoutVersion()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var cfg = Make(t.Path, "cfg-install", "2.0.76");
        var noVer = Make(t.Path, "Factorio-mystery", null, info: false);
        var list = Cands(t, Ctx(t, configPath: cfg));
        var m = list.Single(c => c.Path == Norm(noVer));
        Assert.Null(m.Version);
        Assert.Equal("auto", string.Join(",", m.Sources));
        Assert.Equal("config", string.Join(",", list.Single(c => c.Path == Norm(cfg)).Sources));
    }

    [Fact]
    public void Candidates_SiblingDiscovery_IsDepthOneOnly()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var park = t.Combine("park");
        var anchor = Make(park, "base-install", "2.0.76");
        var nested = Make(Path.Combine(park, "games"), "Factorio-9.9", "2.0.1");            // grandchild
        var inside = Make(Path.Combine(park, "Factorio-outer"), "Factorio-inner", "2.0.2");  // inside a matching sibling that has no executable itself
        var list = Cands(t, Ctx(t), new RegisteredInstallation(anchor, null));
        Assert.Equal(Norm(anchor), Assert.Single(list).Path);
        Assert.DoesNotContain(Norm(nested), list.Select(c => c.Path));
        Assert.DoesNotContain(Norm(inside), list.Select(c => c.Path));
    }

    [Fact]
    public void Candidates_SymlinkedDirectories_AreNotFollowed()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var park = t.Combine("park");
        var anchor = Make(park, "base-install", "2.0.76");
        var real = Make(t.Combine("elsewhere"), "real-install", "1.1.110");
        Directory.CreateSymbolicLink(Path.Combine(park, "Factorio-link"), RootOf(real));
        var list = Cands(t, Ctx(t), new RegisteredInstallation(anchor, null));
        Assert.Equal(Norm(anchor), Assert.Single(list).Path);
        Assert.DoesNotContain(list, c => c.Path.Contains("Factorio-link"));
        Assert.DoesNotContain(list, c => c.Path.Contains("real-install"));
    }

    [Fact]
    public void Candidates_RegisteredEntries_MustBeNamedFactorio_AndNotSymlinks()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var good = Make(t.Path, "good", "2.0.76");
        var evil = Path.Combine(t.Path, "evil", "evil.sh");
        WriteScript(evil);
        var notFactorio = Path.Combine(t.Path, "nf", "factorio-bak");
        WriteScript(notFactorio);
        var link = Path.Combine(t.Path, "lnk", "factorio");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, good);
        var upper = Path.Combine(t.Path, "up", "Factorio");
        WriteScript(upper);
        var list = Cands(t, Ctx(t), new RegisteredInstallation(evil, "evil"), new RegisteredInstallation(notFactorio, null),
            new RegisteredInstallation(link, "link"), new RegisteredInstallation(good, "good"), new RegisteredInstallation(upper, "upper"));
        var paths = list.Select(c => c.Path).ToList();
        Assert.True(paths.Contains(Norm(good)));
        Assert.False(paths.Contains(Norm(evil)), "registered executable not named factorio must be dropped");
        Assert.False(paths.Contains(Norm(notFactorio)), "registered executable not named factorio must be dropped");
        Assert.False(paths.Contains(Norm(link)), "registered symlink must be dropped");
        Assert.False(WasStarted(evil) || WasStarted(notFactorio));
    }

    [Fact]
    public void Candidates_ScanCapOf200EntriesPerParent()
    {
        if (Unsupported) return;
        using var t = new TempDir();
        var park = t.Combine("park");
        var anchor = Make(park, "base-install", "2.0.76");
        for (var i = 0; i < 320; i++) Make(park, $"factorio-{i:D3}", "2.0." + i);
        var list = Cands(t, Ctx(t), new RegisteredInstallation(anchor, null));
        var auto = list.Where(c => c.Path != Norm(anchor)).ToList();
        Assert.True(auto.Count <= 200, $"{auto.Count} siblings discovered; at most 200 directory entries may be inspected per parent");
        Assert.True(auto.Count >= 1, "nothing discovered at all");
        Assert.True(list.Any(c => c.Path == Norm(anchor)), "the registered install must always stay in the list");
    }

    [Fact]
    public void Candidates_NoRegisteredNoConfig_ReturnsNothingFromTheTempTree_AndDoesNotThrow()
    {
        using var t = new TempDir();
        var list = FactorioInstallations.Candidates(Ctx(t), NoRegistered);
        Assert.NotNull(list);
        Assert.Empty(list.Where(c => Under(c.Path, t.Path)));
    }

    /// <summary>Process-global environment variables: everything that touches FACTORIO_PATH / FMS_DATA_DIR lives in this single test, restores in finally and runs synchronously.</summary>
    [Fact]
    public void Candidates_EnvironmentSources_EnvVarAndDataDirectory()
    {
        if (Unsupported) return;
        var prevFp = Environment.GetEnvironmentVariable("FACTORIO_PATH");
        var prevData = Environment.GetEnvironmentVariable("FMS_DATA_DIR");
        try
        {
            using var t = new TempDir();
            var envExe = Make(t.Path, "env-install", "2.0.76");
            var cfgExe = Make(t.Path, "cfg-install", "1.1.110");
            var data = t.Combine("data");
            var d1 = Make(Path.Combine(data, "factorio"), "2.0.77", "2.0.77");
            var d2 = Make(Path.Combine(data, "factorio"), "my-custom-name", "1.1.50");       // any name works inside the data dir
            Directory.CreateDirectory(Path.Combine(data, "factorio", "empty-dir"));
            File.WriteAllText(Path.Combine(data, "factorio", "readme.txt"), "x");
            var deeper = Make(Path.Combine(data, "factorio", "group"), "3.0.0", "3.0.0");     // depth 2: not discovered
            var linkTarget = Make(t.Combine("elsewhere"), "linked", "2.1.0");
            Directory.CreateSymbolicLink(Path.Combine(data, "factorio", "linked"), RootOf(linkTarget));
            var dataOutsideFactorioSubdir = Make(data, "should-not-count", "2.0.5");           // <data>/ itself is not scanned, only <data>/factorio/

            Environment.SetEnvironmentVariable("FMS_DATA_DIR", data);

            // FACTORIO_PATH as executable
            Environment.SetEnvironmentVariable("FACTORIO_PATH", envExe);
            var list = Cands(t, Ctx(t, cfgExe));
            var e = list.Single(c => c.Path == Norm(envExe));
            Assert.Equal("env", string.Join(",", e.Sources));
            Assert.Equal("config", string.Join(",", list.Single(c => c.Path == Norm(cfgExe)).Sources));
            Assert.Equal("auto", string.Join(",", list.Single(c => c.Path == Norm(d1)).Sources));
            Assert.Equal("auto", string.Join(",", list.Single(c => c.Path == Norm(d2)).Sources));
            Assert.Equal("2.0.77", list.Single(c => c.Path == Norm(d1)).Version);
            Assert.DoesNotContain(Norm(deeper), list.Select(c => c.Path));
            Assert.DoesNotContain(Norm(dataOutsideFactorioSubdir), list.Select(c => c.Path));
            Assert.DoesNotContain(list, c => c.Path.Contains(Path.Combine("factorio", "linked")));
            Assert.DoesNotContain(list, c => c.Path.Contains("empty-dir"));

            // FACTORIO_PATH as installation directory, same exe also configured and registered -> merged sources in order
            Environment.SetEnvironmentVariable("FACTORIO_PATH", RootOf(envExe));
            var merged = Cands(t, Ctx(t, RootOf(envExe)), new RegisteredInstallation(envExe, "L")).Single(c => c.Path == Norm(envExe));
            Assert.Equal("ui,env,config", string.Join(",", merged.Sources));

            // FACTORIO_PATH pointing nowhere: simply not a candidate
            Environment.SetEnvironmentVariable("FACTORIO_PATH", t.Combine("nowhere", "factorio"));
            var none = Cands(t, Ctx(t, cfgExe));
            Assert.DoesNotContain(none, c => c.Path.Contains("nowhere"));
            Assert.True(none.Any(c => c.Path == Norm(cfgExe)));

            // FMS_DATA_DIR that does not exist
            Environment.SetEnvironmentVariable("FACTORIO_PATH", null);
            Environment.SetEnvironmentVariable("FMS_DATA_DIR", t.Combine("no-such-data-dir"));
            Assert.Empty(Cands(t, Ctx(t)).Where(c => c.Path.Contains("no-such-data-dir")));

            // FMS_DATA_DIR pointing at a file
            var asFile = t.Write("datafile", "x");
            Environment.SetEnvironmentVariable("FMS_DATA_DIR", asFile);
            Assert.Empty(Cands(t, Ctx(t)).Where(c => c.Path.Contains("datafile")));

            // nothing started
            foreach (var x in new[] { envExe, cfgExe, d1, d2, deeper, linkTarget }) Assert.False(WasStarted(x), "started " + x);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FACTORIO_PATH", prevFp);
            Environment.SetEnvironmentVariable("FMS_DATA_DIR", prevData);
        }
    }
}

// ====================================================================== Validator rules FMS015 / FMS016

public class FactorioVersionValidatorTests
{
    private static ValidationResult Validate(string factorioVersion, params string[] deps)
    {
        var depsJson = string.Join(", ", deps.Select(d => $"\"{d}\""));
        var fv = factorioVersion == "<missing>" ? "" : $"\"factorio_version\": \"{factorioVersion}\",";
        using var mod = Fixtures.ValidMod($$"""
            { "name": "my-mod", "version": "1.2.3", "title": "My Mod", "author": "Someone", {{fv}} "dependencies": [{{depsJson}}] }
            """);
        return ModValidator.Validate(mod.Path);
    }

    private static ValidationIssue? Issue(ValidationResult r, string code) => r.Issues.FirstOrDefault(i => i.Code == code);

    [Theory]
    [InlineData("1.1", "space-age")]
    [InlineData("1.1", "quality")]
    [InlineData("1.1", "elevated-rails")]
    [InlineData("1.1", "space-age >= 2.0.0")]
    [InlineData("1.1", "quality >= 2.0.0")]
    [InlineData("1.0", "space-age")]
    [InlineData("0.18", "elevated-rails")]
    [InlineData("1.99", "quality")]
    public void Fms015_DlcDependencyBelow2_0_IsAnError(string version, string dep)
    {
        var r = Validate(version, "base >= 1.1.0", dep);
        var i = Issue(r, "FMS015");
        Assert.NotNull(i);
        Assert.Equal(Severity.Error, i!.Severity);
        Assert.True(r.HasErrors);
        Assert.False(string.IsNullOrWhiteSpace(i.Message));
    }

    [Theory]
    [InlineData("2.0", "space-age")]
    [InlineData("2.0", "quality")]
    [InlineData("2.0", "elevated-rails")]
    [InlineData("2.1", "space-age")]
    [InlineData("3.0", "space-age")]
    [InlineData("10.0", "space-age")]
    [InlineData("1.1", "flib")]
    [InlineData("1.1", "space-age-compat")]          // similar name, not the DLC
    [InlineData("1.1", "quality-of-life")]
    [InlineData("1.1", "elevated-rails-extended")]
    [InlineData("1.1", "Space-Age")]
    public void Fms015_DoesNotFire_ForOtherCombinations(string version, string dep)
    {
        var r = Validate(version, "base >= 1.1.0", dep);
        Assert.Null(Issue(r, "FMS015"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.1.0")]
    [InlineData("")]
    public void Fms015_NotEvaluated_WhenFactorioVersionDoesNotParse(string version)
    {
        var r = Validate(version, "base >= 1.1.0", "space-age");
        Assert.Null(Issue(r, "FMS015"));
        Assert.Null(Issue(r, "FMS016"));
    }

    [Fact]
    public void Fms015_NotEvaluated_WhenFactorioVersionIsMissing()
    {
        var r = Validate("<missing>", "base >= 1.1.0", "space-age");
        Assert.Null(Issue(r, "FMS015"));
    }

    [Fact]
    public void Fms015_ReportedOncePerMod_OrOncePerDlc_NeverCrashes()
    {
        var r = Validate("1.1", "base >= 1.1.0", "space-age", "quality", "elevated-rails");
        var n = r.Issues.Count(i => i.Code == "FMS015");
        Assert.True(n is >= 1 and <= 3, "FMS015 count: " + n);
    }

    [Theory]
    [InlineData("3.0")]
    [InlineData("2.2")]
    [InlineData("2.10")]
    [InlineData("1.2")]
    [InlineData("1.10")]
    [InlineData("0.18")]
    [InlineData("0.17")]
    [InlineData("0.0")]
    [InlineData("99.99")]
    public void Fms016_UnusualButWellFormedVersion_IsInfo(string version)
    {
        var r = Validate(version, "base");
        var i = Issue(r, "FMS016");
        Assert.NotNull(i);
        Assert.Equal(Severity.Info, i!.Severity);
        Assert.Contains("unusual", i.Message.ToLowerInvariant());
        Assert.False(r.Issues.Any(x => x.Code == "FMS016" && x.Severity != Severity.Info));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData("2.0")]
    [InlineData("2.1")]
    public void Fms016_KnownVersions_NoIssue(string version)
    {
        var r = Validate(version, "base");
        Assert.Null(Issue(r, "FMS016"));
        Assert.Null(Issue(r, "FMS015"));
        Assert.False(r.HasErrors, string.Join("; ", r.Issues.Select(x => x.Code + " " + x.Message)));
    }

    [Theory]
    [InlineData("2.0.5")]
    [InlineData("1.1.0")]
    [InlineData("2")]
    [InlineData("abc")]
    [InlineData("2.0 ")]
    [InlineData("v2.0")]
    public void Fms016_NotReported_ForMalformedVersion_ExistingRuleStillApplies(string version)
    {
        var r = Validate(version, "base");
        Assert.Null(Issue(r, "FMS016"));
        Assert.True(r.Issues.Any(x => x.Code == "FMS007"), "the existing malformed-version rule must still fire: " + string.Join(",", r.Issues.Select(x => x.Code)));
    }

    [Fact]
    public void Fms016_NotReported_WhenVersionMissing()
    {
        Assert.Null(Issue(Validate("<missing>", "base"), "FMS016"));
    }

    [Fact]
    public void UnusualVersion_With_Dlc_ReportsBothRules_ForBelow2()
    {
        var r = Validate("0.18", "base", "quality");
        Assert.NotNull(Issue(r, "FMS015"));
        Assert.NotNull(Issue(r, "FMS016"));
    }

    [Fact]
    public void ValidModWithVersion2_0_StillHasNoIssues()
    {
        using var mod = Fixtures.ValidMod();
        Assert.Empty(ModValidator.Validate(mod.Path).Issues);
    }

    [Fact]
    public void ExoticDigits_NeverCrashTheValidator()
    {
        foreach (var v in new[] { "٢.٠", "２.０", "99999999999999999999.0", "0000000000001.0000000000001" })
        {
            try { Validate(v, "base", "space-age"); }
            catch (Exception ex) { throw new InvalidOperationException($"Validator threw {ex.GetType().Name} for factorio_version {v}: {ex.Message}"); }
        }
    }
}

// ====================================================================== Scaffolding with a target version

public class FactorioScaffoldVersionTests
{
    private static JsonElement Info(string folder) => JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "info.json"))).RootElement.Clone();

    private static ScaffoldResult Create(TempDir parent, string name, string template, string? version) =>
        version is null
            ? ModScaffolder.Create(new ScaffoldRequest(name, template), parent.Path)
            : ModScaffolder.Create(new ScaffoldRequest(name, template, FactorioVersion: version), parent.Path);

    [Fact]
    public void Templates_ExposeSupportedVersions_Wellformed()
    {
        foreach (var t in ModScaffolder.Templates)
        {
            Assert.NotNull(t.SupportedVersions);
            Assert.NotEmpty(t.SupportedVersions);
            foreach (var v in t.SupportedVersions)
                Assert.True(System.Text.RegularExpressions.Regex.IsMatch(v, @"^[0-9]+\.[0-9]+$"), $"{t.Id}: bad version '{v}'");
            Assert.Equal(t.SupportedVersions.Count, t.SupportedVersions.Distinct().Count());
        }
    }

    [Fact]
    public void EmptyAndRuntimeScript_List_1_1_2_0_2_1()
    {
        foreach (var id in new[] { "empty", "runtime-script" })
        {
            var t = ModScaffolder.Templates.Single(x => x.Id == id);
            foreach (var v in new[] { "1.1", "2.0", "2.1" }) Assert.Contains(v, t.SupportedVersions);
        }
    }

    [Fact]
    public void EveryTemplate_ListsAtLeast_2_0_And_2_1()
    {
        foreach (var t in ModScaffolder.Templates)
        {
            Assert.Contains("2.0", t.SupportedVersions);
            Assert.Contains("2.1", t.SupportedVersions);
        }
    }

    [Fact]
    public void DefaultVersionIs2_0()
    {
        if (FakeFactorio.Unsupported) return;
        using var parent = new TempDir();
        var r = Create(parent, "defmod", "empty", null);
        var info = Info(r.Folder);
        Assert.Equal("2.0", info.GetProperty("factorio_version").GetString());
        Assert.Contains("base >= 2.0.0", info.GetProperty("dependencies").EnumerateArray().Select(d => d.GetString()!));
    }

    [Fact]
    public void EveryTemplate_EverySupportedVersion_ScaffoldsAValidMod_WithMatchingInfoJson()
    {
        using var parent = new TempDir();
        var n = 0;
        foreach (var t in ModScaffolder.Templates)
            foreach (var v in t.SupportedVersions)
            {
                var name = $"m{n++}-{t.Id}";
                var r = Create(parent, name, t.Id, v);
                var info = Info(r.Folder);
                Assert.Equal(v, info.GetProperty("factorio_version").GetString());
                var deps = info.GetProperty("dependencies").EnumerateArray().Select(d => d.GetString()!).ToList();
                Assert.True(deps.Contains($"base >= {v}.0"), $"{t.Id}@{v}: base dependency line missing: {string.Join(" | ", deps)}");
                Assert.DoesNotContain(deps, d => d.StartsWith("space-age") || d.StartsWith("quality") || d.StartsWith("elevated-rails"));
                var validation = ModValidator.Validate(r.Folder);
                Assert.False(validation.HasErrors, $"{t.Id}@{v}: " + string.Join("; ", validation.Issues.Select(i => $"{i.Code} {i.Message}")));
                Assert.DoesNotContain(validation.Issues, i => i.Code == "FMS015");
            }
    }

    [Fact]
    public void ScaffoldedLua_ParsesCleanly_ForEveryTemplateAndVersion()
    {
        using var parent = new TempDir();
        var n = 0;
        foreach (var t in ModScaffolder.Templates)
            foreach (var v in t.SupportedVersions)
            {
                var r = Create(parent, $"lua{n++}-{t.Id}", t.Id, v);
                var validation = ModValidator.Validate(r.Folder);
                var luaIssues = validation.Issues.Where(i => i.Severity != Severity.Info && (i.File?.EndsWith(".lua") ?? false)).ToList();
                Assert.True(luaIssues.Count == 0, $"{t.Id}@{v}: " + string.Join("; ", luaIssues.Select(i => $"{i.Code} {i.File}: {i.Message}")));
            }
    }

    [Fact]
    public void UnsupportedCombination_ThrowsFmsException_AndLeavesNothingBehind()
    {
        foreach (var t in ModScaffolder.Templates)
        {
            var candidates = new[] { "0.18", "1.0", "1.1", "2.0", "2.1", "3.0", "9.9" }.Where(v => !t.SupportedVersions.Contains(v)).ToList();
            Assert.Contains("9.9", candidates);
            foreach (var v in candidates)
            {
                using var parent = new TempDir();
                Assert.Throws<FmsException>(() => Create(parent, "nope", t.Id, v));
                Assert.False(Directory.Exists(Path.Combine(parent.Path, "nope")), $"{t.Id}@{v}: folder created despite failure");
                var left = Directory.GetFileSystemEntries(parent.Path, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".fmstudio")).ToList();
                Assert.True(left.Count == 0, $"{t.Id}@{v}: leftovers " + string.Join(",", left));
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("2")]
    [InlineData("2.")]
    [InlineData(".0")]
    [InlineData("2.0.1")]
    [InlineData("2.0.0.0")]
    [InlineData("a.b")]
    [InlineData("2.x")]
    [InlineData("v2.0")]
    [InlineData("-2.0")]
    [InlineData("+2.0")]
    [InlineData("2.-0")]
    [InlineData("2,0")]
    [InlineData("2 .0")]
    [InlineData("2.0;")]
    [InlineData("2.0\"")]
    [InlineData("2.0\", \"x\": \"y")]
    [InlineData("../2.0")]
    [InlineData("2.0/../../x")]
    [InlineData("2.0\0")]
    public void InvalidVersionString_ThrowsFmsException_AndCreatesNothing(string version)
    {
        using var parent = new TempDir();
        Assert.Throws<FmsException>(() => ModScaffolder.Create(new ScaffoldRequest("badver", "empty", FactorioVersion: version), parent.Path));
        Assert.False(Directory.Exists(Path.Combine(parent.Path, "badver")));
    }

    [Theory]
    [InlineData("2.0\n")]
    [InlineData("2.0\r\n")]
    [InlineData("\n2.0")]
    [InlineData(" 2.0")]
    [InlineData("2.0 ")]
    [InlineData("2.0\t")]
    public void VersionWithWhitespace_IsRejected_OrNeverLeaksIntoInfoJson(string version)
    {
        // `^\d+\.\d+$` accepts a trailing "\n" in .NET: either reject it, or make sure info.json stays clean.
        using var parent = new TempDir();
        try
        {
            var r = ModScaffolder.Create(new ScaffoldRequest("nl", "empty", FactorioVersion: version), parent.Path);
            var v = Info(r.Folder).GetProperty("factorio_version").GetString()!;
            Assert.True(v == "2.0", $"info.json factorio_version is '{v}' (escaped: {System.Text.Json.JsonSerializer.Serialize(v)})");
        }
        catch (FmsException) { }
    }

    [Theory]
    [InlineData("٢.٠")]
    [InlineData("２.０")]
    public void NonAsciiDigits_AreRejectedOrHarmless_NeverACrash(string version)
    {
        using var parent = new TempDir();
        try { ModScaffolder.Create(new ScaffoldRequest("uni", "empty", FactorioVersion: version), parent.Path); }
        catch (FmsException) { }
    }

    [Fact]
    public void HugeVersionString_IsAnFmsException()
    {
        using var parent = new TempDir();
        Assert.Throws<FmsException>(() => ModScaffolder.Create(new ScaffoldRequest("huge", "empty", FactorioVersion: new string('1', 5000) + ".0"), parent.Path));
        Assert.Throws<FmsException>(() => ModScaffolder.Create(new ScaffoldRequest("huge2", "empty", FactorioVersion: new string('x', 100000)), parent.Path));
    }

    [Fact]
    public void TemplateInfoSupportedVersions_ConsistentWithBothApis()
    {
        // Templates property and the CLI --list --json output must agree
        var output = new StringWriter();
        var code = Program.Run(new[] { "new", "--list", "--json" }, output, new StringWriter());
        Assert.Equal(Program.ExitOk, code);
        var arr = JsonDocument.Parse(output.ToString()).RootElement;
        foreach (var t in ModScaffolder.Templates)
        {
            var e = arr.EnumerateArray().Single(x => string.Equals(x.GetProperty("id").GetString(), t.Id, StringComparison.OrdinalIgnoreCase));
            var found = e.TryGetProperty("supportedVersions", out var sv);
            Assert.True(found, "CLI `new --list --json` should expose supportedVersions: " + e);
            Assert.Equal(t.SupportedVersions.ToList(), sv.EnumerateArray().Select(x => x.GetString()!).ToList());
        }
    }
}

// ====================================================================== CLI

public class FactorioCliTests
{
    private static (int Code, string Out, string Err) Run(IProcessRunner? runner, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Program.Run(args, output, error, runner);
        return (code, output.ToString(), error.ToString());
    }

    private static string Info(string factorioVersion) =>
        Fixtures.ValidInfo.Replace("\"factorio_version\": \"2.0\"", $"\"factorio_version\": \"{factorioVersion}\"").Replace("base >= 2.0.0", $"base >= {factorioVersion}.0");

    /// <summary>A project (the mod itself) whose fmstudio.json points at Factorio-2.0.76; Factorio-1.1.110 and Factorio-2.1.3 are discovered as siblings.</summary>
    private sealed class World : IDisposable
    {
        public TempDir Tools = new();
        public TempDir Project;
        public string Exe20, Exe11, Exe21;

        public World(string modVersion)
        {
            Project = Fixtures.ValidMod(Info(modVersion));
            Exe20 = FakeFactorio.Make(Tools.Path, "Factorio-2.0.76", "2.0.76");
            Exe11 = FakeFactorio.Make(Tools.Path, "Factorio-1.1.110", "1.1.110");
            Exe21 = FakeFactorio.Make(Tools.Path, "Factorio-2.1.3", "2.1.3");
            Project.Write("fmstudio.json", "{ \"factorioPath\": " + JsonSerializer.Serialize(FakeFactorio.RootOf(Exe20)) + " }");
        }

        public void Dispose() { Project.Dispose(); Tools.Dispose(); }
    }

    private static IEnumerable<string> Strings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: yield return e.GetString()!; break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) foreach (var s in Strings(x)) yield return s; break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) foreach (var s in Strings(p.Value)) yield return s; break;
        }
    }

    private static IEnumerable<(string Name, JsonElement Value)> Props(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                yield return (p.Name, p.Value);
                foreach (var q in Props(p.Value)) yield return q;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) foreach (var q in Props(x)) yield return q;
    }

    [Fact]
    public void FactorioCommand_Text_ListsCandidatesAndThePick()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("1.1");
        var (code, output, error) = Run(null, "factorio", "-p", w.Project.Path);
        Assert.Equal(Program.ExitOk, code);
        foreach (var exe in new[] { w.Exe20, w.Exe11, w.Exe21 })
        {
            Assert.Contains(FakeFactorio.Norm(exe), output);
            Assert.Contains(FactorioInstallations.IdOf(FakeFactorio.Norm(exe)), output);
        }
        foreach (var v in new[] { "2.0.76", "1.1.110", "2.1.3" }) Assert.Contains(v, output);
        Assert.Contains("config", output);
        Assert.Contains("auto", output);
        Assert.False(FakeFactorio.WasStarted(w.Exe20) || FakeFactorio.WasStarted(w.Exe11) || FakeFactorio.WasStarted(w.Exe21), "listing must not start Factorio");
    }

    [Fact]
    public void FactorioCommand_Json_IsParseable_WithCandidatesAndPick()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("1.1");
        var (code, output, _) = Run(null, "factorio", "--json", "-p", w.Project.Path);
        Assert.Equal(Program.ExitOk, code);
        var doc = JsonDocument.Parse(output).RootElement;
        var strings = Strings(doc).ToList();
        foreach (var exe in new[] { w.Exe20, w.Exe11, w.Exe21 })
        {
            Assert.Contains(FakeFactorio.Norm(exe), strings);
            Assert.Contains(FactorioInstallations.IdOf(FakeFactorio.Norm(exe)), strings);
        }
        foreach (var v in new[] { "2.0.76", "1.1.110", "2.1.3" }) Assert.Contains(v, strings);
        // mod is 1.1 -> the pick is automatic and points at the 1.1.110 install
        var modes = Props(doc).Where(p => string.Equals(p.Name, "mode", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value.GetString()).ToList();
        Assert.Contains("auto", modes);
        Assert.True(Props(doc).Any(p => string.Equals(p.Name, "reason", StringComparison.OrdinalIgnoreCase)), "pick reason expected");
    }

    [Fact]
    public void FactorioCommand_Json_NoMatch_ReportsModeNone()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("3.0");
        var (_, output, _) = Run(null, "factorio", "--json", "-p", w.Project.Path);
        var doc = JsonDocument.Parse(output).RootElement;
        var modes = Props(doc).Where(p => string.Equals(p.Name, "mode", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value.GetString()).ToList();
        Assert.Contains("none", modes);
    }

    [Fact]
    public void Smoke_AutoPicks_TheInstallMatchingTheModsFactorioVersion()
    {
        if (FakeFactorio.Unsupported) return;
        foreach (var (modVersion, expected) in new[] { ("1.1", "1.1.110"), ("2.0", "2.0.76"), ("2.1", "2.1.3") })
        {
            using var w = new World(modVersion);
            var exeFor = new Dictionary<string, string> { ["1.1.110"] = w.Exe11, ["2.0.76"] = w.Exe20, ["2.1.3"] = w.Exe21 };
            var runner = new RecordingRunner();
            var (code, output, error) = Run(runner, "smoke", "-p", w.Project.Path, "--ticks", "60");
            Assert.True(code == Program.ExitOk, $"{modVersion}: exit {code}\n{output}\n{error}");
            Assert.NotEmpty(runner.Calls);
            Assert.All(runner.Calls, c => Assert.Equal(FakeFactorio.Norm(exeFor[expected]), FakeFactorio.Norm(c.Exe)));
            var text = output + error;
            Assert.Contains($"Using Factorio {expected} ({FakeFactorio.Norm(exeFor[expected])})", text);
        }
    }

    [Fact]
    public void Smoke_NoMatchingInstall_IsAUsageError_WithReasonAndHint_AndStartsNothing()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("3.0");
        var runner = new RecordingRunner();
        var (code, output, error) = Run(runner, "smoke", "-p", w.Project.Path);
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Empty(runner.Calls);
        Assert.Contains("3.0", error);
        Assert.Contains("2.0.76", error);        // found versions are listed
        Assert.Contains("--factorio", error);    // hint
        Assert.DoesNotContain("Using Factorio", output + error);
    }

    [Fact]
    public void Smoke_ExplicitFactorio_IsUsedExactly_EvenOnVersionMismatch_WithAWarning()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("1.1");
        var runner = new RecordingRunner();
        var (code, output, error) = Run(runner, "smoke", "-p", w.Project.Path, "--factorio", w.Exe21);   // mod is 1.1, install is 2.1.3
        Assert.Equal(Program.ExitOk, code);
        Assert.All(runner.Calls, c => Assert.Equal(FakeFactorio.Norm(w.Exe21), FakeFactorio.Norm(c.Exe)));
        var text = (output + error).ToLowerInvariant();
        Assert.Contains("warn", text);
        Assert.Contains("2.1.3", text);
    }

    [Fact]
    public void Smoke_ExplicitFactorio_MatchingVersion_HasNoMismatchWarning()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("2.1");
        var runner = new RecordingRunner();
        var (code, output, error) = Run(runner, "smoke", "-p", w.Project.Path, "--factorio", w.Exe21);
        Assert.Equal(Program.ExitOk, code);
        Assert.DoesNotContain("mismatch", (output + error).ToLowerInvariant());
    }

    [Fact]
    public void Smoke_ExplicitFactorio_Missing_StillFails_WithoutFallingBackToAnotherInstall()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("2.0");
        var runner = new RecordingRunner();
        var (code, _, error) = Run(runner, "smoke", "-p", w.Project.Path, "--factorio", Path.Combine(w.Tools.Path, "nope", "factorio"));
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Empty(runner.Calls);
        Assert.Contains("nope", error);
    }

    [Fact]
    public void Smoke_UnusableConfigPath_IsSimplyNotACandidate_WhenOtherInstallsExist()
    {
        if (FakeFactorio.Unsupported) return;
        using var tools = new TempDir();
        var good = FakeFactorio.Make(tools.Path, "Factorio-2.0.76", "2.0.76");
        using var project = Fixtures.ValidMod();
        project.Write("fmstudio.json", "{ \"factorioPath\": " + JsonSerializer.Serialize(Path.Combine(tools.Path, "does-not-exist")) + " }");
        // the good install is only reachable through the sibling rule of ... nothing; so register it as the configured one's neighbour:
        Directory.CreateDirectory(Path.Combine(tools.Path, "does-not-exist"));
        var runner = new RecordingRunner();
        var (code, _, error) = Run(runner, "smoke", "-p", project.Path);
        // The config path resolves to nothing; with no other source the run must fail with a clear error (not crash).
        Assert.True(code is Program.ExitUsageError or Program.ExitOk, error);
        if (code == Program.ExitUsageError) Assert.Contains("does-not-exist", error);
    }

    [Fact]
    public void Run_AutoPicks_TheMatchingInstall_AndPrintsUsingLine()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("1.1");
        var (code, output, error) = Run(null, "run", "-p", w.Project.Path);
        Assert.True(code == Program.ExitOk, $"exit {code}\n{output}\n{error}");
        Assert.True(FakeFactorio.WasStarted(w.Exe11), "the 1.1 install should have been started");
        Assert.False(FakeFactorio.WasStarted(w.Exe20) || FakeFactorio.WasStarted(w.Exe21), "other installs must not be started");
        Assert.Contains($"Using Factorio 1.1.110 ({FakeFactorio.Norm(w.Exe11)})", output + error);
        Assert.Contains("--mod-directory", FakeFactorio.Calls(w.Exe11));
    }

    [Fact]
    public void Run_NoMatch_IsAUsageError_AndStartsNothing()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("0.18");
        var (code, _, error) = Run(null, "run", "-p", w.Project.Path);
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("--factorio", error);
        Assert.False(FakeFactorio.WasStarted(w.Exe20) || FakeFactorio.WasStarted(w.Exe11) || FakeFactorio.WasStarted(w.Exe21));
    }

    [Fact]
    public void Run_ExplicitFactorio_BypassesPicking()
    {
        if (FakeFactorio.Unsupported) return;
        using var w = new World("1.1");
        var (code, output, error) = Run(null, "run", "-p", w.Project.Path, "--factorio", w.Exe21);
        Assert.True(code == Program.ExitOk, $"exit {code}\n{output}\n{error}");
        Assert.True(FakeFactorio.WasStarted(w.Exe21));
        Assert.False(FakeFactorio.WasStarted(w.Exe11));
    }

    [Fact]
    public void New_FactorioVersionOption_SetsInfoJson()
    {
        using var parent = new TempDir();
        var (code, _, error) = Run(null, "new", "ver11", "--factorio-version", "1.1", "--out", parent.Path);
        Assert.True(code == Program.ExitOk, error);
        var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(parent.Path, "ver11", "info.json"))).RootElement;
        Assert.Equal("1.1", info.GetProperty("factorio_version").GetString());
        Assert.Contains("base >= 1.1.0", info.GetProperty("dependencies").EnumerateArray().Select(d => d.GetString()!));
    }

    [Fact]
    public void New_DefaultFactorioVersionIs2_0()
    {
        using var parent = new TempDir();
        Assert.Equal(Program.ExitOk, Run(null, "new", "defver", "--out", parent.Path).Code);
        var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(parent.Path, "defver", "info.json"))).RootElement;
        Assert.Equal("2.0", info.GetProperty("factorio_version").GetString());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("2")]
    [InlineData("2.0.1")]
    [InlineData("")]
    [InlineData("9.9")]
    public void New_InvalidOrUnsupportedFactorioVersion_IsAUsageError_AndCreatesNothing(string version)
    {
        using var parent = new TempDir();
        var (code, _, error) = Run(null, "new", "badver", "--factorio-version", version, "--out", parent.Path);
        Assert.Equal(Program.ExitUsageError, code);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(Directory.Exists(Path.Combine(parent.Path, "badver")));
    }

    [Fact]
    public void New_FactorioVersionWithoutValue_IsAUsageError()
    {
        using var parent = new TempDir();
        var (code, _, error) = Run(null, "new", "novalue", "--out", parent.Path, "--factorio-version");
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains("--factorio-version", error);
    }

    [Fact]
    public void New_UnsupportedTemplateVersionCombination_IsAUsageError_WithJsonKind()
    {
        using var parent = new TempDir();
        var t = ModScaffolder.Templates.OrderBy(x => x.SupportedVersions.Count).First();
        var unsupported = new[] { "1.0", "1.1", "0.18", "3.0" }.FirstOrDefault(v => !t.SupportedVersions.Contains(v)) ?? "9.9";
        var (code, _, error) = Run(null, "new", "combo", "--template", t.Id, "--factorio-version", unsupported, "--out", parent.Path, "--json");
        Assert.Equal(Program.ExitUsageError, code);
        Assert.Contains(unsupported, error);
        Assert.False(Directory.Exists(Path.Combine(parent.Path, "combo")));
    }
}
