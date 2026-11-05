using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Black-box tests of LocalePlaceholders / LocaleEditor, written from the localization-editor contract only.</summary>
public class LocaleEditorTests
{
    private const string Sep = "¦";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly byte[] BomBytes = { 0xEF, 0xBB, 0xBF };

    // ------------------------------------------------------------------ helpers

    private static void PutBytes(TempDir dir, string rel, byte[] bytes)
    {
        var full = dir.Combine(rel.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static void Put(TempDir dir, string rel, string content) => PutBytes(dir, rel, Utf8.GetBytes(content));

    private static byte[] Raw(TempDir dir, string rel) => File.ReadAllBytes(dir.Combine(rel.Split('/')));

    /// <summary>File content as UTF-8 text; a BOM, if any, shows up as U+FEFF.</summary>
    private static string Text(TempDir dir, string rel) => Encoding.UTF8.GetString(Raw(dir, rel));

    private static bool Exists(TempDir dir, string rel) => File.Exists(dir.Combine(rel.Split('/')));

    private static string[] Split(string joined) => joined.Length == 0 ? Array.Empty<string>() : joined.Split(Sep);

    private static TempDir Mod(params (string Rel, string Content)[] files)
    {
        var dir = new TempDir();
        foreach (var (rel, content) in files) Put(dir, rel, content);
        return dir;
    }

    private static string Rev(TempDir dir, string lang) => LocaleEditor.Build(dir.Path, lang).Revision;

    private static LocaleApplyResult Apply(TempDir dir, string lang, params LocaleChange[] changes) =>
        LocaleEditor.Apply(dir.Path, lang, changes, Rev(dir, lang));

    private static LocaleChange Set(string section, string key, string? value) => new(section, key, value);

    private static LocaleRow Row(LocaleTable table, string section, string key) =>
        table.Rows.Single(r => r.Section == section && r.Key == key);

    private static Dictionary<string, byte[]> Snapshot(TempDir dir)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(dir.Path, f).Replace('\\', '/')] = File.ReadAllBytes(f);
        return map;
    }

    private static void AssertSameSnapshot(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), after.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        foreach (var (k, v) in before) Assert.True(v.AsSpan().SequenceEqual(after[k]), "file changed: " + k);
    }

    // ------------------------------------------------------------------ LocalePlaceholders.Extract

    [Theory]
    [InlineData("", "")]
    [InlineData("plain text without anything", "")]
    [InlineData("a __1__ b __2__", "__1__¦__2__")]
    [InlineData("__1__ and __1__", "__1__¦__1__")]
    [InlineData("__12__", "__12__")]
    [InlineData("__ENTITY__iron-chest__", "__ENTITY__iron-chest__")]
    [InlineData("take __ITEM__iron-plate__ now", "__ITEM__iron-plate__")]
    [InlineData("__FLUID__water__", "__FLUID__water__")]
    [InlineData("__RECIPE__iron-gear-wheel__", "__RECIPE__iron-gear-wheel__")]
    [InlineData("__TECHNOLOGY__automation__", "__TECHNOLOGY__automation__")]
    [InlineData("__TILE__stone-path__", "__TILE__stone-path__")]
    [InlineData("press __CONTROL__open-gui__ to open", "__CONTROL__open-gui__")]
    [InlineData("__ALT_CONTROL__1__open-gui__", "__ALT_CONTROL__1__open-gui__")]
    [InlineData("__CONTROL_STYLE_BEGIN__x__CONTROL_STYLE_END__", "__CONTROL_STYLE_BEGIN__¦__CONTROL_STYLE_END__")]
    [InlineData("__REMARK_COLOR_BEGIN__x__REMARK_COLOR_END__", "__REMARK_COLOR_BEGIN__¦__REMARK_COLOR_END__")]
    [InlineData("__ENTITY__a.b/c_d-e1__ tail", "__ENTITY__a.b/c_d-e1__")]
    [InlineData("__1____2__", "__1__¦__2__")]
    [InlineData("__foo__", "")]
    [InlineData("__UNKNOWN__x__", "")]
    [InlineData("__ENTITY__iron chest__", "")]
    [InlineData("_1_ and ___", "")]
    [InlineData("see [item=iron-plate] and [/color]", "[item=iron-plate]¦[/color]")]
    [InlineData("[color=1,0,0]red[/color]", "[color=1,0,0]¦[/color]")]
    [InlineData("[color=1, 0, 0]x", "[color=1, 0, 0]")]
    [InlineData("[img=item/iron-plate]", "[img=item/iron-plate]")]
    [InlineData("[font=default-bold]x[/font]", "[font=default-bold]¦[/font]")]
    [InlineData("[gps=1,2]", "[gps=1,2]")]
    [InlineData("[bold]x[/bold]", "[bold]¦[/bold]")]
    [InlineData("[ 5 ]", "")]
    [InlineData("[A-Z]", "")]
    [InlineData("[x=", "")]
    [InlineData("[x=abc", "")]
    [InlineData("[a1]", "")]
    [InlineData("[Item=x]", "")]
    [InlineData("[-x]", "")]
    [InlineData("[]", "")]
    [InlineData("Level [ 5 ] and [item=a]", "[item=a]")]
    [InlineData("line\\nbreak", "\\n")]
    [InlineData("a\\nb\\nc", "\\n¦\\n")]
    [InlineData("real\nnewline", "")]
    [InlineData("tab\\tstays", "")]
    [InlineData("[x=abc\ndef]", "")]
    [InlineData("__plural_for_parameter_1_{1=file|rest=files}__", "__plural_for_parameter_1_{1=file|rest=files}__")]
    [InlineData("You have __1__ __plural_for_parameter_1_{1=file|rest=files}__ left", "__1__¦__plural_for_parameter_1_{1=file|rest=files}__")]
    [InlineData("[color=1,0,0]__ENTITY__x__\\n[/color]", "[color=1,0,0]¦__ENTITY__x__¦\\n¦[/color]")]
    [InlineData("Привет __1__ 日本 [item=камень]", "__1__¦[item=камень]")]
    public void Extract_FindsTokensInOrder(string text, string expectedJoined)
    {
        Assert.Equal(Split(expectedJoined).ToList(), LocalePlaceholders.Extract(text).ToList());
    }

    [Fact]
    public void Extract_NullIsEmpty()
    {
        Assert.Empty(LocalePlaceholders.Extract(null!));
    }

    [Fact]
    public void Extract_HugeStringIsFast()
    {
        var text = string.Concat(Enumerable.Repeat("word [item=a] __1__ \\n [ not ", 20000));
        var sw = Stopwatch.StartNew();
        var tokens = LocalePlaceholders.Extract(text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "slow: " + sw.Elapsed);
        Assert.Equal(60000, tokens.Count);
    }

    [Fact]
    public void Extract_PathologicalUnterminatedInputsAreFast()
    {
        // Backtracking bait: many unterminated openers.
        var text = string.Concat(Enumerable.Repeat("[a=", 21000)) + string.Concat(Enumerable.Repeat("__ENTITY__aaaa", 4600)) + string.Concat(Enumerable.Repeat("__plural_for_parameter_1_{", 2500));
        var sw = Stopwatch.StartNew();
        var tokens = LocalePlaceholders.Extract(text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "slow: " + sw.Elapsed);
        Assert.NotNull(tokens);
    }

    // ------------------------------------------------------------------ LocalePlaceholders.Compare

    [Theory]
    [InlineData("a __1__", "b __1__", "", "")]
    [InlineData("a __1__", "b", "__1__", "")]
    [InlineData("a", "b __1__", "", "__1__")]
    [InlineData("", "", "", "")]
    [InlineData("__1__ __2__", "__2__ __1__", "", "")]
    [InlineData("__2__ __1__ __3__", "", "__2__¦__1__¦__3__", "")]
    [InlineData("", "__3__ __1__ __2__", "", "__3__¦__1__¦__2__")]
    [InlineData("__1__ __2__", "__2__ __3__", "__1__", "__3__")]
    [InlineData("__plural_for_parameter_1_{1=file|rest=files}__", "__plural_for_parameter_1_{1=Datei|rest=Dateien}__", "", "")]
    [InlineData("__plural_for_parameter_1_{1=a|rest=b}__", "x", "__plural_for_parameter_1__", "")]
    [InlineData("x", "__plural_for_parameter_2_{1=a|rest=b}__", "", "__plural_for_parameter_2__")]
    [InlineData("__plural_for_parameter_1_{1=a|rest=b}__", "__plural_for_parameter_2_{1=a|rest=b}__", "__plural_for_parameter_1__", "__plural_for_parameter_2__")]
    [InlineData("[color=1,0,0]x[/color]", "x", "[color=1,0,0]¦[/color]", "")]
    [InlineData("[color=red]x", "[color=RED]x", "[color=red]", "[color=RED]")]
    [InlineData("a\\nb", "a b", "\\n", "")]
    [InlineData("a b", "a\\nb", "", "\\n")]
    [InlineData("__ENTITY__a__", "__ENTITY__b__", "__ENTITY__a__", "__ENTITY__b__")]
    [InlineData("__CONTROL__open-gui__", "__ALT_CONTROL__1__open-gui__", "__CONTROL__open-gui__", "__ALT_CONTROL__1__open-gui__")]
    [InlineData("Level [ 5 ]", "Niveau", "", "")]
    [InlineData("Hello __1__ [item=a]", "Привет [item=a] __1__", "", "")]
    public void Compare_ReportsNormalisedDifferences(string source, string target, string missing, string unexpected)
    {
        var diff = LocalePlaceholders.Compare(source, target);
        Assert.Equal(Split(missing).ToList(), diff.Missing.ToList());
        Assert.Equal(Split(unexpected).ToList(), diff.Unexpected.ToList());
        Assert.Equal(missing.Length == 0 && unexpected.Length == 0, diff.IsEmpty);
    }

    [Fact]
    public void Compare_IsAMultiset()
    {
        var less = LocalePlaceholders.Compare("__1__ __1__ __1__", "__1__");
        Assert.True(less.Missing.Count >= 1);
        Assert.True(less.Missing.All(t => t == "__1__"));
        Assert.Empty(less.Unexpected);

        var more = LocalePlaceholders.Compare("__1__", "__1__ __1__");
        Assert.Empty(more.Missing);
        Assert.True(more.Unexpected.Count >= 1);
        Assert.True(more.Unexpected.All(t => t == "__1__"));

        Assert.True(LocalePlaceholders.Compare("__1__ __1__", "__1__ __1__").IsEmpty);
    }

    [Fact]
    public void Compare_PluralContentIsNeverCompared_EvenWhenTranslatedFreely()
    {
        var d = LocalePlaceholders.Compare("__plural_for_parameter_1_{1=one|2=two|rest=many}__ x", "x __plural_for_parameter_1_{rest=много}__");
        Assert.True(d.IsEmpty, string.Join(",", d.Missing) + " / " + string.Join(",", d.Unexpected));
    }

    // ------------------------------------------------------------------ IsValidLanguageCode / SourceLanguage

    [Theory]
    [InlineData("en", true)]
    [InlineData("ru", true)]
    [InlineData("fil", true)]
    [InlineData("zh-CN", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en-US", true)]
    [InlineData("es-419", true)]
    [InlineData("zh-cn", true)]
    [InlineData("zh-12345678", true)]
    [InlineData("", false)]
    [InlineData("e", false)]
    [InlineData("english", false)]
    [InlineData("EN", false)]
    [InlineData("En", false)]
    [InlineData("zh-", false)]
    [InlineData("zh-C", false)]
    [InlineData("zh-123456789", false)]
    [InlineData("en_US", false)]
    [InlineData("en us", false)]
    [InlineData(" en", false)]
    [InlineData("en ", false)]
    [InlineData("../x", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("en/..", false)]
    [InlineData("en/de", false)]
    [InlineData("en\\de", false)]
    [InlineData("en\n", false)]
    [InlineData("\nen", false)]
    [InlineData("en\0", false)]
    [InlineData("ру", false)]
    [InlineData("en-\u0663\u0663", false)]
    [InlineData("en--US", false)]
    [InlineData("en-US-x", false)]
    [InlineData("e1", false)]
    public void IsValidLanguageCode_FollowsTheRegex(string code, bool expected)
    {
        Assert.Equal(expected, LocaleEditor.IsValidLanguageCode(code));
    }

    [Fact]
    public void IsValidLanguageCode_NullIsFalse() => Assert.False(LocaleEditor.IsValidLanguageCode(null));

    [Fact]
    public void SourceLanguage_PrefersEn_ElseFirstOrdinal_ElseNull()
    {
        using var none = Mod(("info.json", "{}"));
        Assert.Null(LocaleEditor.SourceLanguage(none.Path));

        using var emptyLocale = Mod(("info.json", "{}"));
        Directory.CreateDirectory(emptyLocale.Combine("locale"));
        Assert.Null(LocaleEditor.SourceLanguage(emptyLocale.Path));

        using var withEn = Mod(("locale/fr/a.cfg", "[a]\nk=1\n"), ("locale/en/a.cfg", "[a]\nk=1\n"), ("locale/de/a.cfg", "[a]\nk=1\n"));
        Assert.Equal("en", LocaleEditor.SourceLanguage(withEn.Path));

        using var noEn = Mod(("locale/fr/a.cfg", "[a]\nk=1\n"), ("locale/de/a.cfg", "[a]\nk=1\n"), ("locale/ru/a.cfg", "[a]\nk=1\n"));
        Assert.Equal("de", LocaleEditor.SourceLanguage(noEn.Path));

        using var ordinal = Mod(("locale/pt-BR/a.cfg", "[a]\nk=1\n"), ("locale/pt/a.cfg", "[a]\nk=1\n"));
        Assert.Equal("pt", LocaleEditor.SourceLanguage(ordinal.Path));
    }

    [Fact]
    public void DefaultSourceLanguage_IsEn() => Assert.Equal("en", LocaleEditor.DefaultSourceLanguage);

    // ------------------------------------------------------------------ Summaries

    [Fact]
    public void Summaries_OnePerLanguage_OrdinalOrder_AgreeWithBuildAndLocaleChecker()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[x]\na=1\nb=2\nc=3\n[y]\nd=4\n"),
            ("locale/de/a.cfg", "[x]\na=Eins\nb=Zwei\nzzz=extra\n"),
            ("locale/fr/a.cfg", "[x]\na=Un\n"),
            ("locale/readme.txt", "not a language"));
        var summaries = LocaleEditor.Summaries(dir.Path);
        Assert.Equal(new[] { "de", "en", "fr" }, summaries.Select(s => s.Code).ToArray());

        var de = summaries.Single(s => s.Code == "de");
        Assert.Equal(4, de.Total);
        Assert.Equal(2, de.Translated);
        Assert.Equal(2, de.Untranslated);
        Assert.Equal(1, de.Extra);
        Assert.Equal(0, de.Identical);
        Assert.Equal(0, de.PlaceholderProblems);

        foreach (var s in summaries) Assert.Equal(LocaleEditor.Build(dir.Path, s.Code).Summary, s);

        var coverage = LocaleChecker.Coverage(dir.Path);
        foreach (var c in coverage)
        {
            var s = summaries.Single(x => x.Code == c.Language);
            Assert.Equal(c.Missing, s.Untranslated);
            Assert.Equal(c.Extra, s.Extra);
            if (c.Language != "en") Assert.Equal(c.Keys, s.Translated + s.Extra);
        }
    }

    [Fact]
    public void Summaries_NoLocale_IsEmpty()
    {
        using var dir = Mod(("info.json", "{}"));
        Assert.Empty(LocaleEditor.Summaries(dir.Path));
    }

    [Fact]
    public void Summaries_SourceLanguageIsFullyTranslated()
    {
        using var dir = Mod(("locale/en/a.cfg", "[x]\na=1\nb=\n"));
        var s = LocaleEditor.Summaries(dir.Path).Single();
        Assert.Equal("en", s.Code);
        Assert.Equal(2, s.Total);
        Assert.Equal(2, s.Translated);
        Assert.Equal(0, s.Untranslated);
        Assert.Equal(0, s.Extra);
        Assert.Equal(0, s.Identical);
    }

    // ------------------------------------------------------------------ Build: rows

    private static TempDir BasicMod() => Mod(
        ("locale/en/main.cfg", "[a]\nk1=One\nk2=Two\n[b]\nx=X\n"),
        ("locale/de/main.cfg", "[a]\nk1=Eins\nz=Zusatz\n"));

    [Fact]
    public void Build_Basic_StatusOrderFilesAndLines()
    {
        using var dir = BasicMod();
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal("en", t.SourceLanguage);
        Assert.Equal("de", t.Language);
        Assert.Equal(new[] { "k1", "k2", "x", "z" }, t.Rows.Select(r => r.Key).ToArray());
        Assert.Equal(new[] { "a", "a", "b", "a" }, t.Rows.Select(r => r.Section).ToArray());
        Assert.Equal(new[] { LocaleRowStatus.Translated, LocaleRowStatus.Untranslated, LocaleRowStatus.Untranslated, LocaleRowStatus.Extra }, t.Rows.Select(r => r.Status).ToArray());

        var k1 = Row(t, "a", "k1");
        Assert.Equal("One", k1.Source);
        Assert.Equal("Eins", k1.Target);
        Assert.Equal("locale/en/main.cfg", k1.SourceFile);
        Assert.Equal("locale/de/main.cfg", k1.TargetFile);
        Assert.Equal(2, k1.TargetLine);
        Assert.False(k1.Identical);
        Assert.Empty(k1.PlaceholderMissing);
        Assert.Empty(k1.PlaceholderUnexpected);
        Assert.Null(k1.Suggestion);

        var k2 = Row(t, "a", "k2");
        Assert.Null(k2.Target);
        Assert.Null(k2.TargetFile);
        Assert.Null(k2.TargetLine);
        Assert.Equal("locale/en/main.cfg", k2.SourceFile);

        var z = Row(t, "a", "z");
        Assert.Equal("", z.Source);
        Assert.Equal("Zusatz", z.Target);
        Assert.Null(z.SourceFile);
        Assert.Equal("locale/de/main.cfg", z.TargetFile);
        Assert.Equal(3, z.TargetLine);
        Assert.Empty(z.PlaceholderMissing);
        Assert.Empty(z.PlaceholderUnexpected);
        Assert.Null(z.Suggestion);

        var s = t.Summary;
        Assert.Equal("de", s.Code);
        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.Translated);
        Assert.Equal(2, s.Untranslated);
        Assert.Equal(1, s.Extra);
        Assert.Equal(0, s.Identical);
        Assert.Equal(0, s.PlaceholderProblems);
    }

    [Fact]
    public void Build_EmptyAndWhitespaceTargetValues_AreUntranslated_ButPresent()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\na=A\nb=B\nc=C\nd=D\n"),
            ("locale/de/a.cfg", "[s]\na=\nb=   \nc=\t\nd=ok\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal(LocaleRowStatus.Untranslated, Row(t, "s", "a").Status);
        Assert.Equal(LocaleRowStatus.Untranslated, Row(t, "s", "b").Status);
        Assert.Equal(LocaleRowStatus.Untranslated, Row(t, "s", "c").Status);
        Assert.Equal(LocaleRowStatus.Translated, Row(t, "s", "d").Status);
        Assert.NotNull(Row(t, "s", "a").Target);
        Assert.Equal(1, t.Summary.Translated);
        Assert.Equal(3, t.Summary.Untranslated);
        Assert.Equal(0, t.Summary.Extra);
    }

    [Fact]
    public void Build_Identical_IsASubsetOfTranslated()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\na=Same\nb=Different\nc=\nd=Case\n"),
            ("locale/de/a.cfg", "[s]\na=Same\nb=Anders\nc=x\nd=case\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.True(Row(t, "s", "a").Identical);
        Assert.Equal(LocaleRowStatus.Translated, Row(t, "s", "a").Status);
        Assert.False(Row(t, "s", "b").Identical);
        Assert.False(Row(t, "s", "c").Identical);   // source empty
        Assert.False(Row(t, "s", "d").Identical);   // ordinal compare
        Assert.Equal(4, t.Summary.Translated);
        Assert.Equal(1, t.Summary.Identical);
    }

    [Fact]
    public void Build_SourceLanguage_AllTranslated_TargetEqualsSource_NoIdenticalNoProblems()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\na=Hello __1__\nb=\nc=[item=x]\n"), ("locale/de/a.cfg", "[s]\na=Hallo\n"));
        var t = LocaleEditor.Build(dir.Path, "en");
        Assert.Equal("en", t.Language);
        Assert.Equal(3, t.Rows.Count);
        Assert.All(t.Rows, r =>
        {
            Assert.Equal(LocaleRowStatus.Translated, r.Status);
            Assert.Equal(r.Source, r.Target);
            Assert.False(r.Identical);
            Assert.Empty(r.PlaceholderMissing);
            Assert.Empty(r.PlaceholderUnexpected);
            Assert.Null(r.Suggestion);
        });
        Assert.Equal(3, t.Summary.Total);
        Assert.Equal(3, t.Summary.Translated);
        Assert.Equal(0, t.Summary.Untranslated);
        Assert.Equal(0, t.Summary.Extra);
        Assert.Equal(0, t.Summary.Identical);
        Assert.Equal(0, t.Summary.PlaceholderProblems);
    }

    [Fact]
    public void Build_SourceFilesOrderedOrdinallyByRelativePath_ThenLineOrder()
    {
        using var dir = Mod(
            ("locale/en/b.cfg", "[s]\nb2=1\nb1=1\n"),
            ("locale/en/a.cfg", "[s]\na2=1\na1=1\n"),
            ("locale/en/sub/c.cfg", "[s]\nc1=1\n"),
            ("locale/en/C.cfg", "[s]\nupper=1\n"),
            ("locale/de/a.cfg", "[s]\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        // Ordinal: "C.cfg" < "a.cfg" < "b.cfg" < "sub/c.cfg" (no names differing only by case: they merge on Windows)
        Assert.Equal(new[] { "upper", "a2", "a1", "b2", "b1", "c1" }, t.Rows.Select(r => r.Key).ToArray());
        Assert.Equal("locale/en/sub/c.cfg", Row(t, "s", "c1").SourceFile);
    }

    [Fact]
    public void Build_DuplicateKeys_FirstDefinitionWins_InSourceAndTarget()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nk=first\nk=second\n"),
            ("locale/en/b.cfg", "[s]\nk=third\n"),
            ("locale/de/a.cfg", "[s]\nk=\n"),
            ("locale/de/b.cfg", "[s]\nj=1\nk=DE-b\nk=DE-b2\n"),
            ("locale/de/c.cfg", "[s]\nk=DE-c\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        var k = Row(t, "s", "k");
        Assert.Equal("first", k.Source);
        // de/a.cfg defines k first (empty) -> that definition wins -> untranslated
        Assert.Equal("", k.Target);
        Assert.Equal(LocaleRowStatus.Untranslated, k.Status);
        Assert.Equal("locale/de/a.cfg", k.TargetFile);
        Assert.Equal(2, k.TargetLine);
        Assert.Equal(1, t.Rows.Count(r => r.Key == "k"));
        Assert.Equal(1, t.Summary.Total);
    }

    [Fact]
    public void Build_SameKeyInDifferentSections_AreDifferentRows()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[one]\nname=A\n[two]\nname=B\n"),
            ("locale/de/a.cfg", "[two]\nname=Bee\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(LocaleRowStatus.Untranslated, Row(t, "one", "name").Status);
        Assert.Equal("Bee", Row(t, "two", "name").Target);
    }

    [Fact]
    public void Build_IgnoresCommentsBlankAndMalformedLines_AndKeepsEqualsInValues()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "; comment=notakey\n# other=notakey\n\n[s]\n; c=1\n# d=2\nmalformed line without equals\nk=a=b=c\nuni=Привет 日本 😀\n"),
            ("locale/de/a.cfg", "[s]\nk=x=y\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal(new[] { "k", "uni" }, t.Rows.Select(r => r.Key).ToArray());
        Assert.Equal("a=b=c", Row(t, "s", "k").Source);
        Assert.Equal("Привет 日本 😀", Row(t, "s", "uni").Source);
        Assert.Equal("x=y", Row(t, "s", "k").Target);
    }

    [Fact]
    public void Build_HandlesCrlf_Bom_AndMissingTrailingNewline()
    {
        using var dir = new TempDir();
        PutBytes(dir, "locale/en/a.cfg", BomBytes.Concat(Utf8.GetBytes("[s]\r\nk=Value\r\nlast=NoNewline")).ToArray());
        PutBytes(dir, "locale/de/a.cfg", BomBytes.Concat(Utf8.GetBytes("[s]\r\nk=Wert\r\n")).ToArray());
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal(2, t.Rows.Count);
        Assert.All(t.Rows, r => Assert.Equal("s", r.Section));
        Assert.Equal("Value", Row(t, "s", "k").Source);
        Assert.Equal("Wert", Row(t, "s", "k").Target);
        Assert.Equal("NoNewline", Row(t, "s", "last").Source);
        Assert.Equal(LocaleRowStatus.Untranslated, Row(t, "s", "last").Status);
    }

    [Fact]
    public void Build_PlaceholderProblems_OnlyOnTranslatedRows()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nmiss=Hello __1__\nextra=Hello\nfine=[item=a] __1__\nplural=__plural_for_parameter_1_{1=a|rest=b}__\nuntr=Hi __1__\n"),
            ("locale/de/a.cfg", "[s]\nmiss=Hallo\nextra=Hallo __2__ [color=red]\nfine=__1__ [item=a]\nplural=__plural_for_parameter_1_{rest=x}__\nuntr=\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal(new[] { "__1__" }, Row(t, "s", "miss").PlaceholderMissing.ToArray());
        Assert.Empty(Row(t, "s", "miss").PlaceholderUnexpected);
        Assert.Empty(Row(t, "s", "extra").PlaceholderMissing);
        Assert.Equal(new[] { "__2__", "[color=red]" }, Row(t, "s", "extra").PlaceholderUnexpected.ToArray());
        Assert.Empty(Row(t, "s", "fine").PlaceholderMissing);
        Assert.Empty(Row(t, "s", "fine").PlaceholderUnexpected);
        Assert.Empty(Row(t, "s", "plural").PlaceholderMissing);
        Assert.Empty(Row(t, "s", "plural").PlaceholderUnexpected);
        Assert.Empty(Row(t, "s", "untr").PlaceholderMissing);
        Assert.Empty(Row(t, "s", "untr").PlaceholderUnexpected);
        Assert.Equal(2, t.Summary.PlaceholderProblems);
    }

    [Fact]
    public void Build_ExtraRows_FollowSourceRows_InTargetOrder_AcrossFiles()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nk=1\n"),
            ("locale/de/a.cfg", "[s]\nz2=1\nk=1\nz1=1\n"),
            ("locale/de/b.cfg", "[t]\ny=1\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal(new[] { "k", "z2", "z1", "y" }, t.Rows.Select(r => r.Key).ToArray());
        Assert.Equal(3, t.Summary.Extra);
        Assert.Equal(1, t.Summary.Total);
    }

    [Fact]
    public void Build_NoEnglish_UsesFirstLanguageAsSource()
    {
        using var dir = Mod(("locale/fr/a.cfg", "[s]\nk=Un\n"), ("locale/de/a.cfg", "[s]\nk=Eins\nm=Mehr\n"));
        var t = LocaleEditor.Build(dir.Path, "fr");
        Assert.Equal("de", t.SourceLanguage);
        Assert.Equal(new[] { "k", "m" }, t.Rows.Select(r => r.Key).ToArray());
        Assert.Equal(LocaleRowStatus.Untranslated, Row(t, "s", "m").Status);
        Assert.Equal("Eins", Row(t, "s", "k").Source);
    }

    [Fact]
    public void Build_Throws_ForInvalidMissingOrSourcelessLanguages()
    {
        using var dir = BasicMod();
        foreach (var bad in new[] { "../x", "EN", "e", "", "en/../de", "ru", "zh-CN", "locale", "de\n" })
            Assert.Throws<FmsException>(() => LocaleEditor.Build(dir.Path, bad));
        Assert.Throws<FmsException>(() => LocaleEditor.Build(dir.Path, null!));

        using var none = Mod(("info.json", "{}"));
        Assert.Throws<FmsException>(() => LocaleEditor.Build(none.Path, "en"));
        Assert.Throws<FmsException>(() => LocaleEditor.Build(none.Path, "de"));
    }

    [Fact]
    public void Build_UnicodeAndHugeValues()
    {
        var huge = new string('x', 200_000);
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nhuge=" + huge + "\nemoji=😀 ü ß\n"),
            ("locale/de/a.cfg", "[s]\nhuge=" + huge + "\n"));
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal(200_000, Row(t, "s", "huge").Source.Length);
        Assert.True(Row(t, "s", "huge").Identical);
        Assert.Equal("😀 ü ß", Row(t, "s", "emoji").Source);
    }

    [Fact]
    public void Build_LargeTables_ArePerformant_IncludingSuggestions()
    {
        var sbEn = new StringBuilder("[s]\n");
        var memory = new List<LocaleMemoryEntry>();
        for (var i = 0; i < 20000; i++)
        {
            sbEn.Append("k").Append(i).Append("=Text ").Append(i % 500).Append('\n');
            memory.Add(new LocaleMemoryEntry("Text " + (i % 500), "Tekst " + (i % 500)));
        }
        using var dir = Mod(("locale/en/a.cfg", sbEn.ToString()), ("locale/de/a.cfg", "[s]\n"));
        var sw = Stopwatch.StartNew();
        var t = LocaleEditor.Build(dir.Path, "de", memory);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "slow: " + sw.Elapsed);
        Assert.Equal(20000, t.Rows.Count);
        Assert.Equal("Tekst 7", Row(t, "s", "k7").Suggestion);
    }

    // ------------------------------------------------------------------ Revision

    private static string Sha(params (string Rel, byte[] Bytes)[] files)
    {
        using var sha = SHA256.Create();
        var ms = new MemoryStream();
        foreach (var (rel, bytes) in files)
        {
            ms.Write(Utf8.GetBytes(rel)); ms.WriteByte(0); ms.Write(bytes); ms.WriteByte(0);
        }
        return Convert.ToHexString(sha.ComputeHash(ms.ToArray())).ToLowerInvariant();
    }

    [Fact]
    public void Revision_IsLowercaseHexSha256_OfPathsAndBytes()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\nk=Eins\n"), ("locale/de/sub/b.cfg", "[s]\nj=2\n"));
        var rev = Rev(dir, "de");
        Assert.Equal(64, rev.Length);
        Assert.Equal(rev.ToLowerInvariant(), rev);
        Assert.True(rev.All(Uri.IsHexDigit));

        var a = Raw(dir, "locale/de/a.cfg");
        var b = Raw(dir, "locale/de/sub/b.cfg");
        // The contract says "relative path" under locale/<language>; accept relative-to-language-dir or relative-to-mod-root.
        var relToLang = Sha(("a.cfg", a), ("sub/b.cfg", b));
        var relToRoot = Sha(("locale/de/a.cfg", a), ("locale/de/sub/b.cfg", b));
        Assert.True(rev == relToLang || rev == relToRoot, "unexpected revision algorithm: " + rev);
    }

    [Fact]
    public void Revision_EmptyLanguageDir_IsSha256OfNothing()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"));
        Directory.CreateDirectory(dir.Combine("locale", "de"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Rev(dir, "de"));
    }

    [Fact]
    public void Revision_ChangesWithContentNameOrNewCfg_NotWithOthers()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\nk=Eins\n"), ("locale/fr/a.cfg", "[s]\nk=Un\n"));
        var r0 = Rev(dir, "de");
        Assert.Equal(r0, Rev(dir, "de"));

        File.WriteAllText(dir.Combine("locale", "fr", "a.cfg"), "[s]\nk=Changed\n");
        Assert.Equal(r0, Rev(dir, "de"));                      // other language
        File.WriteAllText(dir.Combine("locale", "en", "a.cfg"), "[s]\nk=Changed\n");
        Assert.Equal(r0, Rev(dir, "de"));                      // source language
        File.WriteAllText(dir.Combine("locale", "de", "notes.txt"), "x");
        Assert.Equal(r0, Rev(dir, "de"));                      // not a .cfg

        File.WriteAllText(dir.Combine("locale", "de", "a.cfg"), "[s]\nk=Eins!\n");
        var r1 = Rev(dir, "de");
        Assert.NotEqual(r0, r1);

        File.Move(dir.Combine("locale", "de", "a.cfg"), dir.Combine("locale", "de", "renamed.cfg"));
        var r2 = Rev(dir, "de");
        Assert.NotEqual(r1, r2);

        Put(dir, "locale/de/new.cfg", "");
        var r3 = Rev(dir, "de");
        Assert.NotEqual(r2, r3);

        Put(dir, "locale/de/nested/deep.cfg", "[s]\nq=1\n");
        Assert.NotEqual(r3, Rev(dir, "de"));
    }

    [Fact]
    public void Revision_DistinguishesByteLevelDifferences_LikeLineEndingsAndBom()
    {
        using var dir = new TempDir();
        Put(dir, "locale/en/a.cfg", "[s]\nk=1\n");
        Put(dir, "locale/de/a.cfg", "[s]\nk=1\n");
        var lf = Rev(dir, "de");
        Put(dir, "locale/de/a.cfg", "[s]\r\nk=1\r\n");
        var crlf = Rev(dir, "de");
        PutBytes(dir, "locale/de/a.cfg", BomBytes.Concat(Utf8.GetBytes("[s]\nk=1\n")).ToArray());
        var bom = Rev(dir, "de");
        Assert.Equal(3, new[] { lf, crlf, bom }.Distinct().Count());
    }

    [Fact]
    public void Revision_PathSeparatorAmbiguity_FilesAreNotConfused()
    {
        // "a.cfg"+"bc" vs "a.cfgb"+"c": separators (0x00) must prevent collisions.
        using var d1 = Mod(("locale/en/x.cfg", ""), ("locale/de/a.cfg", "bc"));
        using var d2 = Mod(("locale/en/x.cfg", ""), ("locale/de/a.cfgb.cfg", "c"));
        Assert.NotEqual(Rev(d1, "de"), Rev(d2, "de"));
    }

    // ------------------------------------------------------------------ Suggestions

    private static TempDir SuggestMod(string en, string de) => Mod(("locale/en/a.cfg", en), ("locale/de/a.cfg", de));

    [Fact]
    public void Suggestion_FromMemory_MatchesTrimmedSource_AndNeverChangesStatus()
    {
        using var dir = SuggestMod("[s]\na=Hello\nb=  Hello  \nc=Other\n", "[s]\n");
        var mem = new[] { new LocaleMemoryEntry(" Hello ", "Hallo") };
        var t = LocaleEditor.Build(dir.Path, "de", mem);
        Assert.Equal("Hallo", Row(t, "s", "a").Suggestion);
        Assert.Equal("Hallo", Row(t, "s", "b").Suggestion);
        Assert.Null(Row(t, "s", "c").Suggestion);
        Assert.All(t.Rows, r => { Assert.Equal(LocaleRowStatus.Untranslated, r.Status); Assert.Null(r.Target); });
        Assert.Equal(0, t.Summary.Translated);
        Assert.Equal(3, t.Summary.Untranslated);
    }

    [Fact]
    public void Suggestion_NullMemoryAndNoMatches_AreNull_AndMatchingIsCaseSensitive()
    {
        using var dir = SuggestMod("[s]\na=Hello\n", "[s]\n");
        Assert.Null(Row(LocaleEditor.Build(dir.Path, "de"), "s", "a").Suggestion);
        Assert.Null(Row(LocaleEditor.Build(dir.Path, "de", null), "s", "a").Suggestion);
        Assert.Null(Row(LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("hello", "Hallo") }), "s", "a").Suggestion);
        Assert.Null(Row(LocaleEditor.Build(dir.Path, "de", Array.Empty<LocaleMemoryEntry>()), "s", "a").Suggestion);
    }

    [Fact]
    public void Suggestion_OnlyForUntranslatedRows_WithNonEmptySource()
    {
        using var dir = SuggestMod("[s]\ndone=Hello\ntodo=Hello\nempty=\n", "[s]\ndone=Servus\nextra=Hello\nempty=\n");
        var t = LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("Hello", "Hallo"), new LocaleMemoryEntry("", "Leer") });
        Assert.Null(Row(t, "s", "done").Suggestion);                 // translated
        Assert.Equal("Hallo", Row(t, "s", "todo").Suggestion);        // tie memory "Hallo" vs own row "Servus" -> ordinal smallest
        Assert.Null(Row(t, "s", "empty").Suggestion);
    }

    [Fact]
    public void Suggestion_EmptySource_NeverGetsOne_AndExtraNever()
    {
        using var dir = SuggestMod("[s]\nempty=\nspaces=   \n", "[s]\nextra=Hello\n");
        var t = LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("", "Leer"), new LocaleMemoryEntry("   ", "Leer2"), new LocaleMemoryEntry("Hello", "Hallo") });
        Assert.Null(Row(t, "s", "empty").Suggestion);
        Assert.Null(Row(t, "s", "extra").Suggestion);
    }

    [Fact]
    public void Suggestion_MostFrequent_Wins()
    {
        using var dir = SuggestMod("[s]\na=Hi\n", "[s]\n");
        var mem = new[]
        {
            new LocaleMemoryEntry("Hi", "Servus"), new LocaleMemoryEntry("Hi", "Hallo"), new LocaleMemoryEntry("Hi", "Hallo"),
            new LocaleMemoryEntry("Hi", "Servus"), new LocaleMemoryEntry("Hi", "Hallo"), new LocaleMemoryEntry("Hi", "Moin"),
        };
        Assert.Equal("Hallo", Row(LocaleEditor.Build(dir.Path, "de", mem), "s", "a").Suggestion);
    }

    [Fact]
    public void Suggestion_Ties_GoToOrdinalSmallest()
    {
        using var dir = SuggestMod("[s]\na=Hi\n", "[s]\n");
        Assert.Equal("Abc", Row(LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("Hi", "Zed"), new LocaleMemoryEntry("Hi", "Abc") }), "s", "a").Suggestion);
        // ordinal, not culture: 'B' (66) < 'a' (97) < 'b' (98)
        Assert.Equal("B", Row(LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("Hi", "b"), new LocaleMemoryEntry("Hi", "B") }), "s", "a").Suggestion);
        Assert.Equal("B", Row(LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("Hi", "a"), new LocaleMemoryEntry("Hi", "B") }), "s", "a").Suggestion);
        // order of the input must not matter
        Assert.Equal("Abc", Row(LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("Hi", "Abc"), new LocaleMemoryEntry("Hi", "Zed") }), "s", "a").Suggestion);
    }

    [Fact]
    public void Suggestion_PlaceholderIncompatibleCandidates_AreRejected_EvenIfMoreFrequent()
    {
        using var dir = SuggestMod("[s]\na=Hello __1__\nb=Plain\n", "[s]\n");
        var mem = new[]
        {
            new LocaleMemoryEntry("Hello __1__", "Hallo"), new LocaleMemoryEntry("Hello __1__", "Hallo"), new LocaleMemoryEntry("Hello __1__", "Hallo"),
            new LocaleMemoryEntry("Hello __1__", "Hallo __1__"),
            new LocaleMemoryEntry("Plain", "Schlicht __1__"),
        };
        var t = LocaleEditor.Build(dir.Path, "de", mem);
        Assert.Equal("Hallo __1__", Row(t, "s", "a").Suggestion);
        Assert.Null(Row(t, "s", "b").Suggestion);
    }

    [Fact]
    public void Suggestion_EmptyCandidatesIgnored()
    {
        using var dir = SuggestMod("[s]\na=Hi\n", "[s]\n");
        var mem = new[] { new LocaleMemoryEntry("Hi", ""), new LocaleMemoryEntry("Hi", ""), new LocaleMemoryEntry("Hi", "Hallo") };
        Assert.Equal("Hallo", Row(LocaleEditor.Build(dir.Path, "de", mem), "s", "a").Suggestion);
        Assert.Null(Row(LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("Hi", "") }), "s", "a").Suggestion);
    }

    [Fact]
    public void Suggestion_FromOwnTranslatedRows_IncludingTheirFrequency()
    {
        using var dir = SuggestMod("[s]\nt1=Save\nt2=Save\nt3=Save\nu=Save\n", "[s]\nt1=Speichern\nt2=Speichern\nt3=Sichern\n");
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal("Speichern", Row(t, "s", "u").Suggestion);
        Assert.Null(Row(t, "s", "t1").Suggestion);
        // memory adds weight: two extra votes for "Sichern" beat the two own votes for "Speichern"
        var t2 = LocaleEditor.Build(dir.Path, "de", new[] { new LocaleMemoryEntry("Save", "Sichern"), new LocaleMemoryEntry("Save", "Sichern") });
        Assert.Equal("Sichern", Row(t2, "s", "u").Suggestion);
    }

    [Fact]
    public void Suggestion_OwnRowsWithPlaceholderProblems_AreNotCandidates()
    {
        using var dir = SuggestMod("[s]\na=Hello __1__\nb=Hello __1__\n", "[s]\na=Hallo\n");
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal(1, t.Summary.PlaceholderProblems);
        Assert.Null(Row(t, "s", "b").Suggestion);
    }

    [Fact]
    public void Suggestion_UntranslatedOwnRows_AreNotCandidates()
    {
        using var dir = SuggestMod("[s]\na=Hi\nb=Hi\n", "[s]\na=\n");
        Assert.Null(Row(LocaleEditor.Build(dir.Path, "de"), "s", "b").Suggestion);
    }

    [Fact]
    public void Suggestion_SourceLanguageBuild_HasNone()
    {
        using var dir = SuggestMod("[s]\na=Hi\n", "[s]\n");
        var t = LocaleEditor.Build(dir.Path, "en", new[] { new LocaleMemoryEntry("Hi", "Hallo") });
        Assert.All(t.Rows, r => Assert.Null(r.Suggestion));
    }

    // ------------------------------------------------------------------ Apply: updates

    [Fact]
    public void Apply_Update_ReplacesOnlyTheValuePart_KeepingEverythingElse()
    {
        var src = "; head comment\n\n[s]\n# note\nk1=old\n  k2 = old2\nk3=keep\nbroken line\n\n[t]\nk1=other section\n";
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk1=1\nk2=2\nk3=3\n[t]\nk1=1\n"), ("locale/de/a.cfg", src));
        var res = Apply(dir, "de", Set("s", "k1", "NEW"), Set("s", "k2", "NEW2"));
        Assert.Equal(0, res.Added);
        Assert.Equal(2, res.Updated);
        Assert.Equal(0, res.Removed);
        Assert.Equal(new[] { "locale/de/a.cfg" }, res.TouchedFiles.ToArray());
        Assert.Equal("; head comment\n\n[s]\n# note\nk1=NEW\n  k2 =NEW2\nk3=keep\nbroken line\n\n[t]\nk1=other section\n", Text(dir, "locale/de/a.cfg"));
        Assert.Equal(Rev(dir, "de"), res.Revision);
    }

    [Fact]
    public void Apply_Update_ValuesAreStoredRaw_NoTrimNoEscape()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\nj=1\nm=1\n"), ("locale/de/a.cfg", "[s]\nk=old\nj=old\nm=old\n"));
        Apply(dir, "de", Set("s", "k", "  spaced  "), Set("s", "j", "a=b ; # \\n [x] \\\\ \"q\""), Set("s", "m", "Привет 😀"));
        Assert.Equal("[s]\nk=  spaced  \nj=a=b ; # \\n [x] \\\\ \"q\"\nm=Привет 😀\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_Update_ToEmptyValue_KeepsTheKey()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\nk=old\n"));
        var res = Apply(dir, "de", Set("s", "k", ""));
        Assert.Equal(1, res.Updated);
        Assert.Equal("[s]\nk=\n", Text(dir, "locale/de/a.cfg"));
        Assert.Equal(LocaleRowStatus.Untranslated, Row(LocaleEditor.Build(dir.Path, "de"), "s", "k").Status);
    }

    [Fact]
    public void Apply_Update_ToIdenticalBytes_IsNotCounted_AndFileNotRewritten()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\nk=same\n"));
        var path = dir.Combine("locale", "de", "a.cfg");
        var old = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, old);
        var before = Rev(dir, "de");
        var res = Apply(dir, "de", Set("s", "k", "same"));
        Assert.Equal(0, res.Updated);
        Assert.Equal(0, res.Added);
        Assert.Equal(0, res.Removed);
        Assert.Empty(res.TouchedFiles);
        Assert.Equal(before, res.Revision);
        Assert.Equal(old, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Apply_Update_OnlyFirstDefinition_OfDuplicates_AndAcrossFilesInOrdinalOrder()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nk=1\n"),
            ("locale/de/b.cfg", "[s]\nk=B-first-by-name?\n"),
            ("locale/de/a.cfg", "[s]\nk=A1\nk=A2\n"));
        var res = Apply(dir, "de", Set("s", "k", "NEW"));
        Assert.Equal(1, res.Updated);
        Assert.Equal(new[] { "locale/de/a.cfg" }, res.TouchedFiles.ToArray());
        Assert.Equal("[s]\nk=NEW\nk=A2\n", Text(dir, "locale/de/a.cfg"));
        Assert.Equal("[s]\nk=B-first-by-name?\n", Text(dir, "locale/de/b.cfg"));
    }

    [Fact]
    public void Apply_SameKeyTwiceInBatch_LastWins()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\nj=1\n"), ("locale/de/a.cfg", "[s]\nk=old\nj=old\n"));
        var res = Apply(dir, "de", Set("s", "k", "first"), Set("s", "k", "second"), Set("s", "j", "x"), Set("s", "j", null), Set("s", "j", "final"));
        Assert.Equal(2, res.Updated);
        Assert.Equal(0, res.Removed);
        Assert.Equal("[s]\nk=second\nj=final\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_SameKeyTwice_ValueThenNull_RemovesIt()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\nj=1\n"), ("locale/de/a.cfg", "[s]\nk=old\nj=old\n"));
        var res = Apply(dir, "de", Set("s", "k", "x"), Set("s", "k", null));
        Assert.Equal(1, res.Removed);
        Assert.Equal(0, res.Updated);
        Assert.Equal("[s]\nj=old\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_Update_PreservesCrlf_Bom_AndMissingTrailingNewline()
    {
        var bom = BomBytes;
        using var dir = new TempDir();
        Put(dir, "locale/en/a.cfg", "[s]\nk=1\nlast=1\n");
        PutBytes(dir, "locale/de/a.cfg", bom.Concat(Utf8.GetBytes("; c\r\n[s]\r\nk=old\r\n\r\nlast=oldlast")).ToArray());
        Apply(dir, "de", Set("s", "k", "A"), Set("s", "last", "B"));
        var bytes = Raw(dir, "locale/de/a.cfg");
        Assert.True(bytes.Take(3).SequenceEqual(bom), "BOM lost");
        Assert.Equal("\uFEFF; c\r\n[s]\r\nk=A\r\n\r\nlast=B", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Apply_Update_KeepsMixedLineEndingsOfUntouchedAndTouchedLines()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\nj=1\n"));
        PutBytes(dir, "locale/de/a.cfg", Utf8.GetBytes("[s]\r\nk=old\nj=old\r\nx=y\n"));
        Apply(dir, "de", Set("s", "k", "A"), Set("s", "j", "B"));
        Assert.Equal("[s]\r\nk=A\nj=B\r\nx=y\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_Update_IsByteLosslessForInvalidUtf8InUntouchedLines()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"));
        var bytes = new List<byte>();
        bytes.AddRange(Utf8.GetBytes("; latin1 caf"));
        bytes.Add(0xE9);               // invalid UTF-8
        bytes.Add(0xFF);
        bytes.AddRange(Utf8.GetBytes("\n[s]\nk=old\n"));
        PutBytes(dir, "locale/de/a.cfg", bytes.ToArray());
        Apply(dir, "de", Set("s", "k", "new"));
        var expected = new List<byte>();
        expected.AddRange(Utf8.GetBytes("; latin1 caf")); expected.Add(0xE9); expected.Add(0xFF); expected.AddRange(Utf8.GetBytes("\n[s]\nk=new\n"));
        Assert.True(expected.ToArray().AsSpan().SequenceEqual(Raw(dir, "locale/de/a.cfg")), "bytes of untouched line changed");
    }

    [Fact]
    public void Apply_Update_HugeAndMaxLengthValues()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\nk=old\n"));
        var v = new string('ä', 65536);
        var res = Apply(dir, "de", Set("s", "k", v));
        Assert.Equal(1, res.Updated);
        Assert.Equal("[s]\nk=" + v + "\n", Text(dir, "locale/de/a.cfg"));
        Assert.Equal(v, Row(LocaleEditor.Build(dir.Path, "de"), "s", "k").Target);
    }

    // ------------------------------------------------------------------ Apply: additions

    [Fact]
    public void Apply_Add_InsertsAfterLastEntryOfLastSectionOccurrence_NotAfterTrailingBlankOrComments()
    {
        var de = "[a]\nk1=1\n; comment\n\n[b]\nx=1\n[a]\nk2=2\nstray malformed\n\n; trailing\n";
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk1=1\nk2=2\nk3=3\nk4=4\n[b]\nx=1\n"), ("locale/de/m.cfg", de));
        var res = Apply(dir, "de", Set("a", "k3", "three"), Set("a", "k4", "four"));
        Assert.Equal(2, res.Added);
        Assert.Equal(new[] { "locale/de/m.cfg" }, res.TouchedFiles.ToArray());
        Assert.Equal("[a]\nk1=1\n; comment\n\n[b]\nx=1\n[a]\nk2=2\nk3=three\nk4=four\nstray malformed\n\n; trailing\n", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_SeveralInOneBatch_KeepBatchOrder_NotSorted()
    {
        using var dir = Mod(("locale/en/m.cfg", "[a]\nz=1\ny=1\nx=1\n"), ("locale/de/m.cfg", "[a]\nq=1\n"));
        Apply(dir, "de", Set("a", "z", "Z"), Set("a", "x", "X"), Set("a", "y", "Y"));
        Assert.Equal("[a]\nq=1\nz=Z\nx=X\ny=Y\n", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_AppendsNewSection_WithBlankLineSeparator()
    {
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk=1\n[b]\nx=1\ny=1\n"), ("locale/de/m.cfg", "[a]\nk=1\n"));
        Apply(dir, "de", Set("b", "x", "X"), Set("b", "y", "Y"));
        Assert.Equal("[a]\nk=1\n\n[b]\nx=X\ny=Y\n", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_NewSection_NoExtraBlankWhenFileAlreadyEndsWithBlank()
    {
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk=1\n[b]\nx=1\n"), ("locale/de/m.cfg", "[a]\nk=1\n\n"));
        Apply(dir, "de", Set("b", "x", "X"));
        Assert.Equal("[a]\nk=1\n\n[b]\nx=X\n", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_NewSection_InCrlfFile_UsesCrlfForInsertedLines()
    {
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk=1\n[b]\nx=1\n"));
        PutBytes(dir, "locale/de/m.cfg", Utf8.GetBytes("[a]\r\nk=1\r\n"));
        Apply(dir, "de", Set("b", "x", "X"));
        Assert.Equal("[a]\r\nk=1\r\n\r\n[b]\r\nx=X\r\n", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_InCrlfFile_ExistingSection_UsesCrlf()
    {
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk=1\nj=1\n"));
        PutBytes(dir, "locale/de/m.cfg", Utf8.GetBytes("[a]\r\nk=1\r\n"));
        Apply(dir, "de", Set("a", "j", "J"));
        Assert.Equal("[a]\r\nk=1\r\nj=J\r\n", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_LineEndingStyleIsDecidedByTheFirstBreak()
    {
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk=1\nj=1\n"));
        PutBytes(dir, "locale/de/m.cfg", Utf8.GetBytes("[a]\nk=1\r\nx=1\r\n"));
        Apply(dir, "de", Set("a", "j", "J"));
        Assert.Equal("[a]\nk=1\r\nx=1\r\nj=J\n", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_AtVeryEndOfFileWithoutTrailingNewline_NewLastLineHasNone()
    {
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk=1\nj=1\n[b]\nx=1\n"), ("locale/de/m.cfg", "[a]\nk=1"));
        Apply(dir, "de", Set("a", "j", "J"));
        Assert.Equal("[a]\nk=1\nj=J", Text(dir, "locale/de/m.cfg"));
        Apply(dir, "de", Set("b", "x", "X"));
        Assert.Equal("[a]\nk=1\nj=J\n\n[b]\nx=X", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_AtEndWithoutTrailingNewline_Crlf()
    {
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk=1\nj=1\n"));
        PutBytes(dir, "locale/de/m.cfg", Utf8.GetBytes("[a]\r\nk=1"));
        Apply(dir, "de", Set("a", "j", "J"));
        Assert.Equal("[a]\r\nk=1\r\nj=J", Text(dir, "locale/de/m.cfg"));
    }

    [Fact]
    public void Apply_Add_KeepsBom_AndDoesNotAddOne()
    {
        var bom = BomBytes;
        using var dir = Mod(("locale/en/m.cfg", "[a]\nk=1\nj=1\n"));
        PutBytes(dir, "locale/de/m.cfg", bom.Concat(Utf8.GetBytes("[a]\nk=1\n")).ToArray());
        Apply(dir, "de", Set("a", "j", "J"));
        var bytes = Raw(dir, "locale/de/m.cfg");
        Assert.True(bytes.Take(3).SequenceEqual(bom));
        Assert.Equal("\uFEFF[a]\nk=1\nj=J\n", Encoding.UTF8.GetString(bytes));

        // a file without a BOM stays without one
        Put(dir, "locale/de/n.cfg", "[a]\nk=1\n");
        Put(dir, "locale/en/n.cfg", "[a]\nk=1\nq=1\n");
        Apply(dir, "de", Set("a", "q", "Q"));
        Assert.Equal((byte)'[', Raw(dir, "locale/de/n.cfg")[0]);
    }

    [Fact]
    public void Apply_Add_IntoMirrorFile_WhichIsCreatedWhenMissing_WithLfUtf8NoBom()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[x]\nk1=1\nk2=2\n"),
            ("locale/en/b.cfg", "[y]\nk3=3\nk4=4\n"),
            ("locale/en/sub/deep/c.cfg", "[z]\nk5=5\n"),
            ("locale/de/a.cfg", "[x]\nk1=Eins\n"));
        var res = Apply(dir, "de", Set("x", "k2", "Zwei"), Set("y", "k3", "Drei"), Set("z", "k5", "Fuenf"), Set("y", "k4", "Vier"));
        Assert.Equal(4, res.Added);
        Assert.Equal(new[] { "locale/de/a.cfg", "locale/de/b.cfg", "locale/de/sub/deep/c.cfg" }, res.TouchedFiles.ToArray());
        Assert.Equal("[x]\nk1=Eins\nk2=Zwei\n", Text(dir, "locale/de/a.cfg"));
        Assert.Equal("[y]\nk3=Drei\nk4=Vier\n", Text(dir, "locale/de/b.cfg"));
        Assert.Equal("[z]\nk5=Fuenf\n", Text(dir, "locale/de/sub/deep/c.cfg"));
        Assert.Equal((byte)'[', Raw(dir, "locale/de/b.cfg")[0]);
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal("locale/de/b.cfg", Row(t, "y", "k3").TargetFile);
        Assert.Equal(0, t.Summary.Untranslated);
    }

    [Fact]
    public void Apply_Add_MirrorFileExistsButLacksTheSection_AppendsSection()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[x]\nk1=1\n[w]\nq=1\n"),
            ("locale/de/a.cfg", "[x]\nk1=Eins\n"),
            ("locale/de/other.cfg", "[w]\nunrelated=1\n"));
        Apply(dir, "de", Set("w", "q", "Q"));
        // the mirror of the key's source file wins over any other file that has the section
        Assert.Equal("[x]\nk1=Eins\n\n[w]\nq=Q\n", Text(dir, "locale/de/a.cfg"));
        Assert.Equal("[w]\nunrelated=1\n", Text(dir, "locale/de/other.cfg"));
    }

    [Fact]
    public void Apply_Add_KeyWhoseSourceIsInSeveralFiles_UsesTheFirstDefinitionsFile()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nk=1\n"),
            ("locale/en/b.cfg", "[s]\nk=2\n"),
            ("locale/de/z.cfg", "[s]\nj=1\n"));
        Apply(dir, "de", Set("s", "k", "K"));
        Assert.True(Exists(dir, "locale/de/a.cfg"));
        Assert.False(Exists(dir, "locale/de/b.cfg"));
        Assert.Equal("[s]\nk=K\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_Add_ExtraKey_GoesToFirstOrdinalTargetFileWithTheSection_ElseExtraCfg()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nk=1\n"),
            ("locale/de/b.cfg", "[s]\nj=1\n"),
            ("locale/de/c.cfg", "[s]\nm=1\n[t]\nn=1\n"));
        var res = Apply(dir, "de", Set("s", "extra-key", "E"), Set("t", "extra2", "E2"), Set("brand-new", "q", "Q"));
        Assert.Equal(3, res.Added);
        Assert.Equal("[s]\nj=1\nextra-key=E\n", Text(dir, "locale/de/b.cfg"));
        Assert.Equal("[s]\nm=1\n[t]\nn=1\nextra2=E2\n", Text(dir, "locale/de/c.cfg"));
        Assert.Equal("[brand-new]\nq=Q\n", Text(dir, "locale/de/extra.cfg"));
        Assert.Equal(new[] { "locale/de/b.cfg", "locale/de/c.cfg", "locale/de/extra.cfg" }, res.TouchedFiles.ToArray());
    }

    [Fact]
    public void Apply_Add_ExtraKey_IntoExistingExtraCfgWithoutTheSection_AppendsSection()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/extra.cfg", "[old]\nq=1\n"));
        Apply(dir, "de", Set("new", "q", "Q"));
        Assert.Equal("[old]\nq=1\n\n[new]\nq=Q\n", Text(dir, "locale/de/extra.cfg"));
    }

    [Fact]
    public void Apply_Add_SectionAndKeyWithSpecialButLegalCharacters()
    {
        using var dir = Mod(("locale/en/a.cfg", "[a b]\nkey with spaces=1\n[sec-ü]\nключ=1\n"), ("locale/de/a.cfg", "; stub\n"));
        Apply(dir, "de", Set("a b", "key with spaces", "v"), Set("sec-ü", "ключ", "значение"));
        Assert.Equal("; stub\n\n[a b]\nkey with spaces=v\n\n[sec-ü]\nключ=значение\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_Add_ToEmptyLanguageDirectory_CreatesMirrorFile()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"));
        Directory.CreateDirectory(dir.Combine("locale", "de"));
        var res = Apply(dir, "de", Set("s", "k", "K"));
        Assert.Equal(new[] { "locale/de/a.cfg" }, res.TouchedFiles.ToArray());
        Assert.Equal("[s]\nk=K\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_Add_ToEmptyExistingFile_DoesNotStartWithBlankLine()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", ""));
        Apply(dir, "de", Set("s", "k", "K"));
        var text = Text(dir, "locale/de/a.cfg");
        Assert.StartsWith("[s]", text);
        Assert.Contains("k=K", text);
    }

    [Fact]
    public void Apply_Add_AfterSectionHeaderOnlySection_StaysInSection()
    {
        // Section exists, has no entries yet. The key must end up inside that section (after its header), not in a new duplicate section.
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n[t]\nx=1\n"), ("locale/de/a.cfg", "[s]\n\n[t]\nx=1\n"));
        Apply(dir, "de", Set("s", "k", "K"));
        var t = LocaleEditor.Build(dir.Path, "de");
        Assert.Equal("K", Row(t, "s", "k").Target);
        Assert.Equal(1, Text(dir, "locale/de/a.cfg").Split("[s]").Length - 1);
    }

    // ------------------------------------------------------------------ Apply: removals

    [Fact]
    public void Apply_Remove_DeletesOnlyTheFirstDefinitionLine()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\n; c\nk=first\nj=1\nk=second\n"));
        var res = Apply(dir, "de", Set("s", "k", null));
        Assert.Equal(1, res.Removed);
        Assert.Equal(0, res.Updated);
        Assert.Equal(0, res.Added);
        Assert.Equal("[s]\n; c\nj=1\nk=second\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_Remove_AbsentKey_IsIgnored_AndTouchesNothing()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\nj=1\n"));
        var path = dir.Combine("locale", "de", "a.cfg");
        var old = new DateTime(2005, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, old);
        var res = Apply(dir, "de", Set("s", "k", null), Set("nosuch", "k", null));
        Assert.Equal(0, res.Removed);
        Assert.Empty(res.TouchedFiles);
        Assert.Equal("[s]\nj=1\n", Text(dir, "locale/de/a.cfg"));
        Assert.Equal(old, File.GetLastWriteTimeUtc(path));
        Assert.False(Exists(dir, "locale/de/extra.cfg"));
    }

    [Fact]
    public void Apply_Remove_KeepsBomAndCrlf()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"));
        PutBytes(dir, "locale/de/a.cfg", BomBytes.Concat(Utf8.GetBytes("[s]\r\nk=1\r\nj=2\r\n")).ToArray());
        Apply(dir, "de", Set("s", "k", null));
        Assert.Equal("\uFEFF[s]\r\nj=2\r\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_Remove_LastLineWithTrailingNewline()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\nj=1\nk=2\n"));
        Apply(dir, "de", Set("s", "k", null));
        Assert.Equal("[s]\nj=1\n", Text(dir, "locale/de/a.cfg"));
    }

    [Fact]
    public void Apply_MixedAddUpdateRemove_CountsAreExact()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\na=1\nb=1\nc=1\nd=1\n"),
            ("locale/de/a.cfg", "[s]\na=A\nb=B\nz=Z\n"));
        var res = Apply(dir, "de", Set("s", "a", "A2"), Set("s", "c", "C"), Set("s", "z", null), Set("s", "nope", null), Set("s", "b", "B"));
        Assert.Equal(1, res.Added);
        Assert.Equal(1, res.Updated);
        Assert.Equal(1, res.Removed);
        Assert.Equal("[s]\na=A2\nb=B\nc=C\n", Text(dir, "locale/de/a.cfg"));
    }

    // ------------------------------------------------------------------ Apply: no-op / revision / conflicts

    [Fact]
    public void Apply_EmptyChanges_IsNoOp_ReturningCurrentRevision()
    {
        using var dir = BasicMod();
        var rev = Rev(dir, "de");
        var before = Snapshot(dir);
        var res = LocaleEditor.Apply(dir.Path, "de", Array.Empty<LocaleChange>(), rev);
        Assert.Equal(rev, res.Revision);
        Assert.Equal(0, res.Added + res.Updated + res.Removed);
        Assert.Empty(res.TouchedFiles);
        AssertSameSnapshot(before, Snapshot(dir));
    }

    [Fact]
    public void Apply_WrongRevision_ThrowsConflict_WithCurrentRevision_AndWritesNothing()
    {
        using var dir = BasicMod();
        var rev = Rev(dir, "de");
        var before = Snapshot(dir);
        var ex = Assert.Throws<LocaleConflictException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Zwei") }, "0000"));
        Assert.Equal(rev, ex.CurrentRevision);
        Assert.True(ex is FmsException);
        AssertSameSnapshot(before, Snapshot(dir));

        Assert.Throws<LocaleConflictException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Zwei") }, ""));
        Assert.Throws<LocaleConflictException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Zwei") }, rev + "0"));
        Assert.Throws<LocaleConflictException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Zwei") }, "\"" + rev + "\""));
        AssertSameSnapshot(before, Snapshot(dir));
    }

    [Fact]
    public void Apply_StaleRevision_AfterExternalEditOrOwnApply_Conflicts()
    {
        using var dir = BasicMod();
        var r0 = Rev(dir, "de");
        var first = LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Zwei") }, r0);
        Assert.NotEqual(r0, first.Revision);
        Assert.Equal(first.Revision, Rev(dir, "de"));
        var after = Snapshot(dir);
        var ex = Assert.Throws<LocaleConflictException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Other") }, r0));
        Assert.Equal(first.Revision, ex.CurrentRevision);
        AssertSameSnapshot(after, Snapshot(dir));

        File.AppendAllText(dir.Combine("locale", "de", "main.cfg"), "; external\n");
        Assert.Throws<LocaleConflictException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Other") }, first.Revision));
    }

    [Fact]
    public void Apply_RevisionOfOtherLanguage_DoesNotApply()
    {
        using var dir = BasicMod();
        Put(dir, "locale/fr/main.cfg", "[a]\nk1=Un\n");
        Assert.Throws<LocaleConflictException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Zwei") }, Rev(dir, "fr")));
    }

    [Fact]
    public void Apply_ConcurrentWritersWithTheSameRevision_ExactlyOneWins_NoLostUpdates()
    {
        for (var round = 0; round < 3; round++)
        {
            using var dir = Mod(
                ("locale/en/a.cfg", string.Concat(Enumerable.Range(0, 8).Select(i => $"[s]\nk{i}=1\n"))),
                ("locale/de/a.cfg", "[s]\nbase=1\n"));
            var rev = Rev(dir, "de");
            var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run<object>(() =>
            {
                start.Wait();
                try { return LocaleEditor.Apply(dir.Path, "de", new[] { Set("s", "k" + i, "V" + i) }, rev); }
                catch (Exception ex) { return ex; }
            })).ToArray();
            start.Set();
            Task.WaitAll(tasks);
            var results = tasks.Select(t => t.Result).ToList();
            var ok = results.OfType<LocaleApplyResult>().Count();
            var conflicts = results.OfType<LocaleConflictException>().Count();
            var others = results.OfType<Exception>().Where(e => e is not LocaleConflictException).Select(e => e.GetType().Name + ": " + e.Message).ToList();
            Assert.Equal(0, others.Count);
            Assert.Equal(1, ok);
            Assert.Equal(7, conflicts);
            var text = Text(dir, "locale/de/a.cfg");
            Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("k", StringComparison.Ordinal) && l.Contains("=V")));
        }
    }

    [Fact]
    public void Apply_LeavesNoTemporaryFilesBehind()
    {
        using var dir = BasicMod();
        var rev = Rev(dir, "de");
        var res = LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k2", "Zwei"), Set("b", "x", "X") }, rev);
        Assert.Equal(new[] { "locale/de/main.cfg" }, Directory.GetFiles(dir.Combine("locale", "de"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir.Path, f).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "locale/de/main.cfg" }, res.TouchedFiles.ToArray());

        // failed validation also leaves no litter
        Assert.Throws<FmsException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("a", "k9", "ok"), Set("a", "bad=key", "x") }, res.Revision));
        Assert.Equal(1, Directory.GetFiles(dir.Combine("locale", "de"), "*", SearchOption.AllDirectories).Length);
        Assert.Equal(0, Directory.GetFiles(dir.Combine("locale"), "*.tmp*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Apply_UntouchedFilesKeepTheirBytes()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/en/b.cfg", "[t]\nj=1\n"),
            ("locale/de/a.cfg", "[s]\nk=old\n"), ("locale/de/b.cfg", "[t]\r\nj=eins"));
        var b = Raw(dir, "locale/de/b.cfg");
        var res = Apply(dir, "de", Set("s", "k", "new"));
        Assert.Equal(new[] { "locale/de/a.cfg" }, res.TouchedFiles.ToArray());
        Assert.True(b.AsSpan().SequenceEqual(Raw(dir, "locale/de/b.cfg")));
    }

    // ------------------------------------------------------------------ Apply: validation (all-or-nothing)

    private static void AssertRejected(TempDir dir, params LocaleChange[] bad)
    {
        var before = Snapshot(dir);
        var good = Set("a", "k2", "will-not-be-written");
        var all = new[] { good }.Concat(bad).ToArray();
        Assert.Throws<FmsException>(() => LocaleEditor.Apply(dir.Path, "de", all, Rev(dir, "de")));
        AssertSameSnapshot(before, Snapshot(dir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a]b")]
    [InlineData("a[b")]
    [InlineData("a=b")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData(";comment")]
    [InlineData("#comment")]
    public void Apply_RejectsInvalidSection(string section)
    {
        using var dir = BasicMod();
        AssertRejected(dir, Set(section, "k", "v"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a=b")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData(";k")]
    [InlineData("#k")]
    [InlineData("[k")]
    [InlineData(" k")]
    [InlineData("k ")]
    [InlineData("\tk")]
    [InlineData("k\t")]
    [InlineData("  ")]
    public void Apply_RejectsInvalidKey(string key)
    {
        using var dir = BasicMod();
        AssertRejected(dir, Set("a", key, "v"));
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\nline2")]
    [InlineData("x\ry")]
    [InlineData("trailing\n")]
    public void Apply_RejectsValuesWithLineBreaks(string value)
    {
        using var dir = BasicMod();
        AssertRejected(dir, Set("a", "k", value));
    }

    [Fact]
    public void Apply_RejectsOverlongValue_AcceptsMaxLength()
    {
        using var dir = BasicMod();
        AssertRejected(dir, Set("a", "k", new string('x', 65537)));
        var res = Apply(dir, "de", Set("a", "k2", new string('x', 65536)));
        Assert.Equal(1, res.Added);
    }

    [Fact]
    public void Apply_RejectsMoreThan5000Changes_AcceptsExactly5000()
    {
        using var dir = BasicMod();
        var tooMany = Enumerable.Range(0, 5001).Select(i => Set("bulk", "k" + i, "v")).ToArray();
        var before = Snapshot(dir);
        Assert.Throws<FmsException>(() => LocaleEditor.Apply(dir.Path, "de", tooMany, Rev(dir, "de")));
        AssertSameSnapshot(before, Snapshot(dir));

        var max = Enumerable.Range(0, 5000).Select(i => Set("bulk", "k" + i, "v" + i)).ToArray();
        var res = LocaleEditor.Apply(dir.Path, "de", max, Rev(dir, "de"));
        Assert.Equal(5000, res.Added);
        var text = Text(dir, "locale/de/extra.cfg");
        Assert.True(text.Contains("k4999=v4999"));
        Assert.True(text.IndexOf("k0=v0", StringComparison.Ordinal) < text.IndexOf("k1=v1", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_KeyMayContainInnerSpacesAndBracketsAfterTheFirstChar()
    {
        using var dir = BasicMod();
        var res = Apply(dir, "de", Set("a", "has space", "v1"), Set("a", "br]acket", "v2"), Set("a", "semi;colon", "v3"), Set("a", "hash#tag", "v4"));
        Assert.Equal(4, res.Added);
    }

    [Fact]
    public void Apply_ValidationRunsBeforeConflictDecisionIsIrrelevant_ButInvalidLanguageIsRejected()
    {
        using var dir = BasicMod();
        var before = Snapshot(dir);
        foreach (var lang in new[] { "../x", "..", "EN", "e", "", "de/..", "ru", "zh-CN" })
            Assert.Throws<FmsException>(() => LocaleEditor.Apply(dir.Path, lang, new[] { Set("a", "k", "v") }, Rev(dir, "de")));
        AssertSameSnapshot(before, Snapshot(dir));
    }

    [Fact]
    public void Apply_PathTraversalLanguage_NeverWritesOutsideTheMod()
    {
        using var outer = new TempDir();
        outer.Write("sibling/locale/en/a.cfg", "[s]\nk=1\n");
        outer.Write("mod/locale/en/a.cfg", "[s]\nk=1\n");
        outer.Write("mod/locale/de/a.cfg", "[s]\nk=1\n");
        var before = Snapshot(outer);
        foreach (var lang in new[] { "../../sibling/locale/en", "../en", "..\\..\\x" })
            Assert.Throws<FmsException>(() => LocaleEditor.Apply(outer.Combine("mod"), lang, new[] { Set("s", "k", "pwned") }, "x"));
        AssertSameSnapshot(before, Snapshot(outer));
    }

    // ------------------------------------------------------------------ Apply: reparse points

    [Fact]
    public void Apply_ReparsePointLanguageDirectory_IsRejected_AndTargetUntouched()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"));
        using var outside = new TempDir();
        outside.Write("a.cfg", "[s]\nk=old\n");
        try { Directory.CreateSymbolicLink(dir.Combine("locale", "de"), outside.Path); }
        catch (Exception) { return; /* cannot create symlinks here */ }
        var before = Snapshot(outside);
        Assert.Throws<FmsException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("s", "k", "pwned") }, "anything"));
        Assert.Throws<FmsException>(() => LocaleEditor.Apply(dir.Path, "de", new[] { Set("s", "k", "pwned") }, Rev(dir, "de")));
        AssertSameSnapshot(before, Snapshot(outside));
    }

    [Fact]
    public void Apply_SymlinkedSubdirectoryInsideLanguage_MustNotLeakWritesOutside()
    {
        using var dir = Mod(("locale/en/sub/c.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\n"));
        using var outside = new TempDir();
        outside.Write("keep.txt", "keep");
        try { Directory.CreateSymbolicLink(dir.Combine("locale", "de", "sub"), outside.Path); }
        catch (Exception) { return; }
        var before = Snapshot(outside);
        try { Apply(dir, "de", Set("s", "k", "pwned")); } catch (FmsException) { /* rejecting is fine */ }
        AssertSameSnapshot(before, Snapshot(outside));
    }

    // ------------------------------------------------------------------ Losslessness (property-style)

    private sealed record Ln(string Text, string Term);

    private static List<Ln> SplitLines(string s)
    {
        var res = new List<Ln>();
        var i = 0;
        while (i < s.Length)
        {
            var j = i;
            while (j < s.Length && s[j] != '\n' && s[j] != '\r') j++;
            var term = "";
            if (j < s.Length) { term = s[j] == '\r' && j + 1 < s.Length && s[j + 1] == '\n' ? "\r\n" : s[j].ToString(); }
            res.Add(new Ln(s[i..j], term));
            i = j + term.Length;
        }
        return res;
    }

    [Fact]
    public void Apply_Update_Fuzz_ChangesExactlyTheFirstDefinitionValue_ByteForByte()
    {
        var rnd = new Random(12345);
        string[] filler =
        {
            "; comment", "# hash comment", "", "   ", "malformed line", "\tindented text without equals", ";k=commented-out", "#k=commented-out2",
            "[other]", "[s]", "[t u]", "ключ=значение", "emoji=😀", "pad = spaced", "pad2=  lead and trail  ",
        };
        for (var iter = 0; iter < 60; iter++)
        {
            var lines = new List<string>();
            var n = rnd.Next(3, 25);
            for (var i = 0; i < n; i++)
                lines.Add(rnd.Next(3) == 0 ? $"key{rnd.Next(4)}=v{rnd.Next(100)}" : filler[rnd.Next(filler.Length)]);
            lines.Insert(rnd.Next(lines.Count + 1), "[s]");
            lines.Add("key0=tail");

            var sb = new StringBuilder();
            var style = rnd.Next(3);   // 0 LF, 1 CRLF, 2 mixed
            for (var i = 0; i < lines.Count; i++)
            {
                sb.Append(lines[i]);
                var last = i == lines.Count - 1;
                if (last && rnd.Next(2) == 0) break;   // no trailing newline
                sb.Append(style == 0 ? "\n" : style == 1 ? "\r\n" : rnd.Next(2) == 0 ? "\n" : "\r\n");
            }
            var content = sb.ToString();
            var bom = rnd.Next(2) == 0;

            // independent model of "first definition of [s] key<k>"
            var parsed = SplitLines(content);
            var section = "";
            var wanted = "key" + rnd.Next(4);
            var hit = -1;
            for (var i = 0; i < parsed.Count && hit < 0; i++)
            {
                var t = parsed[i].Text.Trim();
                if (t.StartsWith('[') && t.EndsWith(']') && t.Length > 2) { section = t[1..^1]; continue; }
                if (t.Length == 0 || t[0] == ';' || t[0] == '#') continue;
                var eq = parsed[i].Text.IndexOf('=');
                if (eq <= 0) continue;
                if (section == "s" && parsed[i].Text[..eq].Trim() == wanted) hit = i;
            }
            if (hit < 0) continue;   // key never defined in [s] by the generator: nothing to check

            var newValue = "NEW" + iter + (rnd.Next(2) == 0 ? "" : " ü=1");
            var expected = new StringBuilder();
            for (var i = 0; i < parsed.Count; i++)
            {
                var text = parsed[i].Text;
                if (i == hit) text = text[..(text.IndexOf('=') + 1)] + newValue;
                expected.Append(text).Append(parsed[i].Term);
            }

            using var dir = Mod(("locale/en/a.cfg", "[s]\nkey0=1\nkey1=1\nkey2=1\nkey3=1\n"));
            var bytes = (bom ? BomBytes : Array.Empty<byte>()).Concat(Utf8.GetBytes(content)).ToArray();
            PutBytes(dir, "locale/de/a.cfg", bytes);
            var res = Apply(dir, "de", Set("s", wanted, newValue));

            var expectedBytes = (bom ? BomBytes : Array.Empty<byte>()).Concat(Utf8.GetBytes(expected.ToString())).ToArray();
            var actual = Raw(dir, "locale/de/a.cfg");
            Assert.True(expectedBytes.AsSpan().SequenceEqual(actual),
                $"iter {iter} key {wanted}\n--- original ---\n{content.Replace("\r", "\\r")}\n--- expected ---\n{expected.ToString().Replace("\r", "\\r")}\n--- actual ---\n{Encoding.UTF8.GetString(actual).Replace("\r", "\\r")}");
            Assert.Equal(Rev(dir, "de"), res.Revision);
        }
    }

    [Fact]
    public void Apply_Roundtrip_UpdateThenRestore_YieldsOriginalBytes()
    {
        var original = "\uFEFF; header\r\n\r\n[s]\r\n  k = old value ; not a comment\r\n# tail\r\nlast=x";
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"));
        PutBytes(dir, "locale/de/a.cfg", Utf8.GetBytes(original));
        var before = Raw(dir, "locale/de/a.cfg");
        Apply(dir, "de", Set("s", "k", "changed"));
        Assert.False(before.AsSpan().SequenceEqual(Raw(dir, "locale/de/a.cfg")));
        Apply(dir, "de", Set("s", "k", " old value ; not a comment"));
        // indentation and spacing before '=' are kept, so restoring the part after '=' restores the file exactly
        Assert.True(before.AsSpan().SequenceEqual(Raw(dir, "locale/de/a.cfg")));
    }

    // ------------------------------------------------------------------ CreateLanguage

    [Fact]
    public void CreateLanguage_CreatesStubPerSourceFile_WithExactlyOneCommentLine()
    {
        using var dir = Mod(
            ("locale/en/a.cfg", "[s]\nk=1\nj=2\n"),
            ("locale/en/sub/b.cfg", "[t]\nm=3\n"),
            ("locale/en/notes.txt", "ignored"));
        LocaleEditor.CreateLanguage(dir.Path, "ru");
        Assert.Equal("; Translation: ru\n", Text(dir, "locale/ru/a.cfg"));
        Assert.Equal("; Translation: ru\n", Text(dir, "locale/ru/sub/b.cfg"));
        Assert.Equal("ru", LocaleEditor.Summaries(dir.Path).Select(s => s.Code).Single(c => c == "ru"));
        var ru = LocaleEditor.Summaries(dir.Path).Single(s => s.Code == "ru");
        Assert.Equal(3, ru.Total);
        Assert.Equal(0, ru.Translated);
        Assert.Equal(3, ru.Untranslated);
        Assert.Equal(0, ru.Extra);
        Assert.Equal(new[] { "en", "ru" }, LocaleEditor.Summaries(dir.Path).Select(s => s.Code).ToArray());
        var t = LocaleEditor.Build(dir.Path, "ru");
        Assert.All(t.Rows, r => Assert.Equal(LocaleRowStatus.Untranslated, r.Status));
    }

    [Fact]
    public void CreateLanguage_RegionCodes_AndTheNewLanguageIsEditable()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"));
        LocaleEditor.CreateLanguage(dir.Path, "zh-CN");
        Assert.True(Exists(dir, "locale/zh-CN/a.cfg"));
        var res = Apply(dir, "zh-CN", Set("s", "k", "一"));
        Assert.Equal(1, res.Added);
        Assert.Equal("; Translation: zh-CN\n\n[s]\nk=一\n", Text(dir, "locale/zh-CN/a.cfg"));
        Assert.Equal(1, LocaleEditor.Build(dir.Path, "zh-CN").Summary.Translated);
    }

    [Fact]
    public void CreateLanguage_Rejects_InvalidExistingCaseInsensitiveExisting_AndSourcelessMods()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk=1\n"), ("locale/zh-CN/a.cfg", "[s]\nk=1\n"), ("locale/de/a.cfg", "[s]\nk=1\n"));
        var before = Snapshot(dir);
        foreach (var bad in new[] { "../x", "EN", "e", "", "de/..", "x y", "en\n" })
            Assert.Throws<FmsException>(() => LocaleEditor.CreateLanguage(dir.Path, bad));
        Assert.Throws<FmsException>(() => LocaleEditor.CreateLanguage(dir.Path, null!));
        Assert.Throws<FmsException>(() => LocaleEditor.CreateLanguage(dir.Path, "de"));
        Assert.Throws<FmsException>(() => LocaleEditor.CreateLanguage(dir.Path, "zh-cn"));
        Assert.Throws<FmsException>(() => LocaleEditor.CreateLanguage(dir.Path, "en"));
        AssertSameSnapshot(before, Snapshot(dir));
        Assert.False(Directory.Exists(dir.Combine("x")));

        using var none = Mod(("info.json", "{}"));
        Assert.Throws<FmsException>(() => LocaleEditor.CreateLanguage(none.Path, "ru"));
        Assert.False(Directory.Exists(none.Combine("locale", "ru")));
    }

    [Fact]
    public void CreateLanguage_PathTraversal_CreatesNothingOutside()
    {
        using var outer = new TempDir();
        outer.Write("mod/locale/en/a.cfg", "[s]\nk=1\n");
        var before = Snapshot(outer);
        var dirsBefore = Directory.GetDirectories(outer.Path, "*", SearchOption.AllDirectories).Length;
        foreach (var bad in new[] { "../x", "../../x", "..", "../mod/locale/zz" })
            Assert.Throws<FmsException>(() => LocaleEditor.CreateLanguage(outer.Combine("mod"), bad));
        AssertSameSnapshot(before, Snapshot(outer));
        Assert.Equal(dirsBefore, Directory.GetDirectories(outer.Path, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void CreateLanguage_WithEmptySourceLanguage_CreatesAnEmptyLanguageDirectory()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("locale", "en"));
        LocaleEditor.CreateLanguage(dir.Path, "fr");
        Assert.True(Directory.Exists(dir.Combine("locale", "fr")));
        var fr = LocaleEditor.Summaries(dir.Path).Single(s => s.Code == "fr");
        Assert.Equal(0, fr.Total);
    }

    [Fact]
    public void CreateLanguage_DoesNotIntroduceValidatorErrors()
    {
        using var dir = Fixtures.ValidMod();
        dir.Write("locale/en/main.cfg", "[mod-name]\nmy-mod=My Mod\n[item-name]\nx=X\n");
        var errorsBefore = ModInspector.Inspect(dir.Path).Issues.Where(i => i.Severity == Severity.Error).Select(i => i.Code + "|" + i.File).OrderBy(x => x, StringComparer.Ordinal).ToList();
        LocaleEditor.CreateLanguage(dir.Path, "ru");
        var after = ModInspector.Inspect(dir.Path).Issues;
        var errorsAfter = after.Where(i => i.Severity == Severity.Error).Select(i => i.Code + "|" + i.File).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(errorsBefore, errorsAfter);
        Assert.DoesNotContain(after, i => i.Severity == Severity.Error && (i.File ?? "").Contains("locale/ru", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateLanguage_ThenTranslateEverything_ReachesFullCoverage()
    {
        using var dir = Mod(("locale/en/a.cfg", "[s]\nk1=1\nk2=2\n"), ("locale/en/b.cfg", "[t]\nk3=3\n"));
        LocaleEditor.CreateLanguage(dir.Path, "pl");
        var rev = Rev(dir, "pl");
        var res = LocaleEditor.Apply(dir.Path, "pl", new[] { Set("s", "k1", "jeden"), Set("s", "k2", "dwa"), Set("t", "k3", "trzy") }, rev);
        Assert.Equal(3, res.Added);
        var s = LocaleEditor.Build(dir.Path, "pl").Summary;
        Assert.Equal(3, s.Translated);
        Assert.Equal(0, s.Untranslated);
        Assert.Equal("; Translation: pl\n\n[s]\nk1=jeden\nk2=dwa\n", Text(dir, "locale/pl/a.cfg"));
        Assert.Equal("; Translation: pl\n\n[t]\nk3=trzy\n", Text(dir, "locale/pl/b.cfg"));
    }
}
