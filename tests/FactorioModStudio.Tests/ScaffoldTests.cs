using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactorioModStudio.Cli;
using FactorioModStudio.Core;
using FactorioModStudio.Core.Lua;
using Xunit;

namespace FactorioModStudio.Tests;

/// <summary>Black-box tests of the new-mod scaffolder (Core and CLI), written from the feature contract only.</summary>
public class ScaffoldTests
{
    private static readonly DateOnly Today = new(2026, 3, 5);
    private static readonly string[] TemplateIds = { "empty", "item-recipe", "entity", "runtime-script" };
    private const string DefaultDescription = "A Factorio mod created with Factorio Mod Studio.";

    // ------------------------------------------------------------------ helpers

    private static ScaffoldResult Make(string parent, string name, string template = "empty", string? title = null, string? author = null, string? description = null, DateOnly? today = null) =>
        ModScaffolder.Create(new ScaffoldRequest(name, template, title, author, description), parent, today ?? Today);

    private static ScaffoldError ErrorOf(Action action) => Assert.Throws<ScaffoldException>(action).Kind;

    private static List<string> DiskFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();

    private static List<string> Entries(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : new List<string>();

    private static string Read(string folder, string rel) => File.ReadAllText(Path.Combine(folder, rel), new UTF8Encoding(false));

    private static string AllLua(string folder) =>
        string.Join("\n", DiskFiles(folder).Where(f => f.EndsWith(".lua", StringComparison.Ordinal)).Select(f => Read(folder, f)));

    // ---- PNG parsing -------------------------------------------------------------------

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] data, int offset, int length)
    {
        var c = 0xFFFFFFFFu;
        for (var i = 0; i < length; i++) c = CrcTable[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint Be32(byte[] b, int pos) => ((uint)b[pos] << 24) | ((uint)b[pos + 1] << 16) | ((uint)b[pos + 2] << 8) | b[pos + 3];

    /// <summary>Strictly parses a PNG (signature, chunk CRCs, IHDR first, IEND last, zlib stream inflates to the exact size). Returns its dimensions.</summary>
    private static (int Width, int Height) ParsePng(byte[] b, string what)
    {
        Assert.True(b.Length > 33, $"{what}: too short");
        Assert.True(b.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), $"{what}: bad signature");
        var pos = 8;
        int w = 0, h = 0, channels = 0, depth = 0, interlace = 0;
        var idat = new MemoryStream();
        var first = true;
        var sawIend = false;
        var sawPlte = false;
        var colorType = -1;
        while (pos < b.Length)
        {
            Assert.False(sawIend, $"{what}: data after IEND");
            Assert.True(pos + 12 <= b.Length, $"{what}: truncated chunk header");
            var len = (int)Be32(b, pos);
            Assert.True(len >= 0 && pos + 12 + len <= b.Length, $"{what}: chunk length out of range");
            var type = Encoding.ASCII.GetString(b, pos + 4, 4);
            var crc = Be32(b, pos + 8 + len);
            Assert.True(Crc32(b, pos + 4, 4 + len) == crc, $"{what}: bad CRC in {type}");
            if (first)
            {
                Assert.Equal("IHDR", type);
                Assert.Equal(13, len);
                w = (int)Be32(b, pos + 8); h = (int)Be32(b, pos + 12);
                depth = b[pos + 16]; colorType = b[pos + 17]; interlace = b[pos + 20];
                Assert.True(b[pos + 18] == 0 && b[pos + 19] == 0, $"{what}: bad compression/filter method");
                channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new XunitException($"{what}: bad color type {colorType}") };
                first = false;
            }
            else if (type == "IDAT") idat.Write(b, pos + 8, len);
            else if (type == "PLTE") sawPlte = true;
            else if (type == "IEND") { Assert.Equal(0, len); sawIend = true; }
            pos += 12 + len;
        }
        Assert.True(sawIend, $"{what}: no IEND");
        if (colorType == 3) Assert.True(sawPlte, $"{what}: indexed PNG without PLTE");
        Assert.True(w > 0 && h > 0, $"{what}: zero dimensions");

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        if (interlace == 0)
        {
            var stride = (w * channels * depth + 7) / 8;
            Assert.Equal((long)h * (1 + stride), raw.Length);
            var bytes = raw.ToArray();
            for (var row = 0; row < h; row++) Assert.True(bytes[row * (1 + stride)] <= 4, $"{what}: bad filter byte in row {row}");
        }
        return (w, h);
    }

    private static (int Width, int Height) PngOf(string folder, string rel) => ParsePng(File.ReadAllBytes(Path.Combine(folder, rel)), rel);

    // ---- cfg parsing -------------------------------------------------------------------

    private static Dictionary<string, Dictionary<string, string>> ParseCfg(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[')
            {
                Assert.True(Regex.IsMatch(line, @"^\[[A-Za-z0-9_-]+\]\s*$"), $"bad cfg section line: {line}");
                current = new Dictionary<string, string>();
                result[line.Trim().Trim('[', ']')] = current;
                continue;
            }
            Assert.True(current is not null, $"cfg line outside any section: {line}");
            var eq = line.IndexOf('=');
            Assert.True(eq > 0, $"cfg line without key=value: {line}");
            current![line[..eq]] = line[(eq + 1)..];
        }
        return result;
    }

    // ---- the key property ----------------------------------------------------------------

    private static void AssertMatchesContract(ScaffoldResult r, string parent, string name, string template,
        string? expectedTitle, string? expectedAuthor, string expectedDescription, bool strictLocaleValues)
    {
        var folder = r.Folder;
        Assert.Equal(name, r.Name);
        Assert.Equal("0.1.0", r.Version);
        Assert.Equal(Path.GetFullPath(Path.Combine(parent, name)), Path.GetFullPath(folder));
        Assert.True(Directory.Exists(folder));

        // Files: relative, '/', sorted, exactly what is on disk.
        var disk = DiskFiles(folder);
        Assert.Equal(disk, r.Files.ToList());
        Assert.All(r.Files, f => Assert.False(f.Contains('\\') || f.StartsWith("/", StringComparison.Ordinal) || f.Contains(".."), "bad relative path " + f));
        Assert.Equal(r.Files.OrderBy(f => f, StringComparer.Ordinal).ToList(), r.Files.ToList());
        foreach (var required in new[] { "info.json", "changelog.txt", "thumbnail.png", $"locale/en/{name}.cfg" })
            Assert.Contains(required, disk);

        // Staging leftovers.
        var siblings = Entries(parent);
        Assert.Contains(name, siblings);
        foreach (var sibling in siblings.Where(s => s != name))
        {
            if (sibling != ".fmstudio") continue;
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(parent, ".fmstudio")));
        }

        // No BOM, no raw placeholders in text files.
        foreach (var f in disk.Where(f => !f.EndsWith(".png", StringComparison.Ordinal)))
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, f));
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, f + " has a BOM");
            var text = new UTF8Encoding(false, true).GetString(bytes); // throws on invalid UTF-8
            Assert.DoesNotContain("\0", text);
        }

        // info.json round trip.
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "info.json"))))
        {
            var root = doc.RootElement;
            Assert.Equal(name, root.GetProperty("name").GetString());
            Assert.Equal("0.1.0", root.GetProperty("version").GetString());
            if (expectedTitle is not null) Assert.Equal(expectedTitle, root.GetProperty("title").GetString());
            else Assert.True(root.GetProperty("title").GetString()!.Trim().Length is > 0 and <= 100);
            if (expectedAuthor is not null) Assert.Equal(expectedAuthor, root.GetProperty("author").GetString());
            else Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("author").GetString()));
            Assert.Equal(expectedDescription, root.GetProperty("description").GetString());
            Assert.Equal("2.0", root.GetProperty("factorio_version").GetString());
            Assert.Equal(new[] { "base >= 2.0.0" }, root.GetProperty("dependencies").EnumerateArray().Select(e => e.GetString()!).ToArray());
        }

        // changelog
        var changelog = Read(folder, "changelog.txt");
        Assert.Contains("Version: 0.1.0", changelog);
        Assert.True(Regex.IsMatch(changelog, @"Date: \d{4}-\d{2}-\d{2}"));
        Assert.True(Regex.IsMatch(changelog, @"^-{99}\r?\n", RegexOptions.Multiline), "changelog needs the 99-dash separator");

        // Validator: zero issues of any severity.
        var validation = ModValidator.Validate(folder);
        Assert.True(validation.Issues.Count == 0, "validator issues: " + string.Join("; ", validation.Issues.Select(i => $"{i.Severity} {i.Code} {i.File}:{i.Line} {i.Message}")));

        // Lua diagnostics.
        foreach (var lua in disk.Where(f => f.EndsWith(".lua", StringComparison.Ordinal)))
        {
            var diagnostics = LuaDiagnostics.Check(Read(folder, lua));
            Assert.True(diagnostics.Count == 0, lua + ": " + string.Join("; ", diagnostics.Select(d => $"{d.Line}:{d.Column} {d.Code} {d.Message}")));
        }

        // PNGs.
        foreach (var png in disk.Where(f => f.EndsWith(".png", StringComparison.Ordinal)))
            ParsePng(File.ReadAllBytes(Path.Combine(folder, png)), png);
        Assert.Equal((144, 144), PngOf(folder, "thumbnail.png"));

        // Every __name__/ reference resolves.
        var lua0 = AllLua(folder);
        foreach (Match m in Regex.Matches(lua0, "__" + Regex.Escape(name) + @"__/([A-Za-z0-9_./-]+)"))
            Assert.True(File.Exists(Path.Combine(folder, m.Groups[1].Value)), "dangling reference " + m.Value);

        // Locale.
        var cfg = ParseCfg(Read(folder, $"locale/en/{name}.cfg"));
        Assert.True(cfg.ContainsKey("mod-name") && cfg["mod-name"].ContainsKey(name), "mod-name section");
        Assert.True(cfg.ContainsKey("mod-description") && cfg["mod-description"].ContainsKey(name), "mod-description section");
        if (strictLocaleValues && expectedTitle is not null)
        {
            Assert.Equal(expectedTitle, cfg["mod-name"][name]);
            Assert.Equal(expectedDescription, cfg["mod-description"][name]);
        }

        AssertTemplateSpecific(folder, name, template, disk, cfg, lua0);
    }

    private static void AssertTemplateSpecific(string folder, string name, string template, List<string> disk,
        Dictionary<string, Dictionary<string, string>> cfg, string lua)
    {
        switch (template)
        {
            case "empty":
                Assert.Contains("data.lua", disk);
                Assert.Contains("control.lua", disk);
                break;
            case "item-recipe":
                {
                    // Factorio 2.0+: a recoloured copy of the iron gear wheel (the building block "item"); 1.1: a new item with a generated icon.
                    if (disk.Contains($"prototypes/{name}-item.lua"))
                    {
                        Assert.Contains("copy.item(\"iron-gear-wheel\"", lua);
                        Assert.Contains("copy.recipe(\"iron-gear-wheel\"", lua);
                        Assert.Contains($"\"{name}-item\"", lua);
                    }
                    else
                    {
                        Assert.Equal((64, 64), PngOf(folder, $"graphics/icons/{name}-item.png"));
                        Assert.True(Regex.Matches(lua, Regex.Escape($"{name}-item")).Count >= 2, "item and recipe both named <name>-item");
                        Assert.True(Regex.IsMatch(lua, @"type\s*=\s*[""']item[""']"));
                        Assert.True(Regex.IsMatch(lua, @"type\s*=\s*[""']recipe[""']"));
                    }
                    Assert.True(cfg.ContainsKey("item-name") && cfg["item-name"].ContainsKey($"{name}-item"));
                    Assert.True(cfg.ContainsKey("item-description") && cfg["item-description"].ContainsKey($"{name}-item"));
                    break;
                }
            case "entity":
                {
                    // A recoloured copy of the iron chest (the building block "chest"), placed by its own item.
                    Assert.Contains($"prototypes/{name}-chest.lua", disk);
                    Assert.Contains("copy.entity(\"container\", \"iron-chest\"", lua);
                    Assert.Contains("copy.item(\"iron-chest\"", lua);
                    Assert.Contains("copy.recipe(\"iron-chest\"", lua);
                    Assert.Contains("copy.replace_sprites(chest)", lua);
                    Assert.Contains($"require(\"prototypes.{name}-chest\")", Read(folder, "data.lua"));
                    Assert.True(cfg.ContainsKey("entity-name") && cfg["entity-name"].ContainsKey($"{name}-chest"));
                    break;
                }
            case "runtime-script":
                {
                    Assert.Contains("settings.lua", disk);
                    Assert.Contains("control.lua", disk);
                    var settings = Read(folder, "settings.lua");
                    foreach (var token in new[] { "startup", "runtime-global", "runtime-per-user", "bool", "int", "string", "minimum_value", "maximum_value", "allowed_values" })
                        Assert.Contains(token, settings);
                    var control = Read(folder, "control.lua");
                    foreach (var token in new[] { "on_init", "storage", "defines.events.on_player_created", "commands.add_command", "on_nth_tick", "on_event" })
                        Assert.Contains(token, control);
                    Assert.True(cfg.ContainsKey("mod-setting-name") && cfg["mod-setting-name"].Count >= 3);
                    Assert.True(cfg.ContainsKey("mod-setting-description") && cfg["mod-setting-description"].Count >= 3);
                    Assert.True(cfg.ContainsKey("command-help") && cfg["command-help"].Count >= 1);
                    break;
                }
        }
    }

    private static void FullCheck(string parent, string name, string template, string? title = null, string? author = null, string? description = null, bool strictLocale = true)
    {
        var r = Make(parent, name, template, title, author, description);
        var t = title is null || title.Trim().Length == 0 ? HumanizeOrNull(name) : title.Trim();
        var a = author is null || author.Trim().Length == 0 ? null : author.Trim();
        var d = description is null || description.Trim().Length == 0 ? DefaultDescription : description.Trim();
        AssertMatchesContract(r, parent, name, template, t, a, d, strictLocale);
    }

    private static string? HumanizeOrNull(string name) => Regex.IsMatch(name, "^[a-z][a-z0-9]*(-[a-z0-9]+)*$") ? Humanize(name) : null;

    private static string Humanize(string name) =>
        string.Join(" ", name.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    // ------------------------------------------------------------------ templates list

    [Fact]
    public void Templates_AreTheFourContractTemplates_InOrder()
    {
        var ids = ModScaffolder.Templates.Select(t => t.Id).ToList();
        Assert.Equal(TemplateIds.ToList(), ids);
        Assert.All(ModScaffolder.Templates, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        });
        Assert.Equal(4, ModScaffolder.Templates.Select(t => t.Title).Distinct().Count());
    }

    // ------------------------------------------------------------------ the key property

    [Theory]
    [InlineData("empty")]
    [InlineData("item-recipe")]
    [InlineData("entity")]
    [InlineData("runtime-script")]
    public void EveryTemplate_IsCleanUnderValidatorLuaLintAndPng(string template)
    {
        using var parent = new TempDir();
        FullCheck(parent.Path, "hello-world", template);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("item-recipe")]
    [InlineData("entity")]
    [InlineData("runtime-script")]
    public void EveryTemplate_PacksIntoAValidZip(string template)
    {
        using var parent = new TempDir();
        using var output = new TempDir();
        var r = Make(parent.Path, "packme", template);
        var pack = ModPackager.Pack(r.Folder, output.Path);
        Assert.True(pack.Success, string.Join("; ", pack.Validation.Issues.Select(i => i.Code + " " + i.Message)));
        Assert.Equal(Path.Combine(output.Path, "packme_0.1.0.zip"), pack.ArchivePath);
        using var zip = ZipFile.OpenRead(pack.ArchivePath!);
        Assert.All(zip.Entries, e => Assert.StartsWith("packme_0.1.0/", e.FullName));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("packme_0.1.0/info.json", names);
        Assert.Contains("packme_0.1.0/thumbnail.png", names);
        Assert.Contains("packme_0.1.0/locale/en/packme.cfg", names);
        foreach (var file in r.Files) Assert.Contains("packme_0.1.0/" + file, names);

        // PNGs survive packing byte-for-byte.
        foreach (var png in r.Files.Where(f => f.EndsWith(".png", StringComparison.Ordinal)))
        {
            using var s = zip.GetEntry("packme_0.1.0/" + png)!.Open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.True(ms.ToArray().SequenceEqual(File.ReadAllBytes(Path.Combine(r.Folder, png))));
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("A")]
    [InlineData("7")]
    [InlineData("_")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("my-mod")]
    [InlineData("My_Mod2")]
    [InlineData("under_score-and-dash_9")]
    [InlineData("CONSOLE")]
    [InlineData("AUXILIARY")]
    [InlineData("com")]
    [InlineData("LPT")]
    [InlineData("nullable")]
    [InlineData("a-")]
    [InlineData("-a-b--c-")]
    [InlineData("X_____Y")]
    [InlineData("123")]
    public void ValidNames_WorkForEveryTemplate(string name)
    {
        foreach (var template in TemplateIds)
        {
            using var parent = new TempDir();
            FullCheck(parent.Path, name, template, strictLocale: true);
        }
    }

    [Fact]
    public void LongestName_100Chars_Works_AndTitleStaysWithinLimits()
    {
        foreach (var template in TemplateIds)
        {
            using var parent = new TempDir();
            var name = new string('a', 100);
            var r = Make(parent.Path, name, template);
            Assert.True(Directory.Exists(r.Folder));
            Assert.True(ModValidator.Validate(r.Folder).Issues.Count == 0);
            // A 100-character hyphenated name humanizes to a title that must still fit the 100 character limit.
            var hyphenated = string.Join("-", Enumerable.Repeat("ab", 33)) + "-x"; // 100 chars
            Assert.Equal(100, hyphenated.Length);
            using var parent2 = new TempDir();
            var r2 = Make(parent2.Path, hyphenated, template);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r2.Folder, "info.json")));
            Assert.True(doc.RootElement.GetProperty("title").GetString()!.Length <= 100);
            Assert.True(ModValidator.Validate(r2.Folder).Issues.Count == 0);
        }
    }

    // ------------------------------------------------------------------ names

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" a")]
    [InlineData("a ")]
    [InlineData("a b")]
    [InlineData("a.b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("/abs")]
    [InlineData("C:\\x")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a|b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("é")]
    [InlineData("名前")]
    [InlineData("mod😀")]
    [InlineData("a%b")]
    [InlineData("a+b")]
    [InlineData("a@b")]
    [InlineData("a~b")]
    [InlineData("a[b]")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con")]
    [InlineData("PRN")]
    [InlineData("prn")]
    [InlineData("AUX")]
    [InlineData("aux")]
    [InlineData("NUL")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("com1")]
    [InlineData("COM5")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("lpt1")]
    [InlineData("LPT5")]
    [InlineData("LPT9")]
    public void InvalidNames_AreRejected_WithoutTouchingDisk(string name)
    {
        using var parent = new TempDir();
        File.WriteAllText(parent.Combine("keep.txt"), "keep");
        var before = Entries(parent.Path);
        var kind = ErrorOf(() => Make(parent.Path, name));
        Assert.Equal(ScaffoldError.InvalidName, kind);
        Assert.Equal(before, Entries(parent.Path));
    }

    [Fact]
    public void NameOf101Chars_IsRejected()
    {
        using var parent = new TempDir();
        Assert.Equal(ScaffoldError.InvalidName, ErrorOf(() => Make(parent.Path, new string('a', 101))));
        Assert.Equal(ScaffoldError.InvalidName, ErrorOf(() => Make(parent.Path, new string('a', 5000))));
        Assert.Empty(Entries(parent.Path));
    }

    [Fact]
    public void TraversalNames_NeverWriteOutsideTheParent_AndLeaveACanaryAlone()
    {
        using var root = new TempDir();
        var parent = root.Combine("mods");
        Directory.CreateDirectory(parent);
        var canaryName = Guid.NewGuid().ToString("N") + ".txt";
        var canary = root.Combine(canaryName);
        File.WriteAllText(canary, "keep");
        foreach (var name in new[] { "../" + canaryName, "..\\" + canaryName, "../evil", "a/../../evil", "/tmp/fms-evil-" + Guid.NewGuid().ToString("N"), "..", "." })
        {
            Assert.Equal(ScaffoldError.InvalidName, ErrorOf(() => Make(parent, name)));
        }
        Assert.Equal("keep", File.ReadAllText(canary));
        Assert.Equal(new[] { canaryName, "mods" }.OrderBy(x => x, StringComparer.Ordinal).ToList(), Entries(root.Path));
        Assert.Empty(Entries(parent));
    }

    [Fact]
    public void InvalidName_TakesPartInValidationBeforeUnknownTemplateOrFields_ButEachIsReported()
    {
        using var parent = new TempDir();
        Assert.Equal(ScaffoldError.UnknownTemplate, ErrorOf(() => Make(parent.Path, "good", "nope")));
        Assert.Equal(ScaffoldError.InvalidName, ErrorOf(() => Make(parent.Path, "bad name")));
        Assert.Equal(ScaffoldError.InvalidField, ErrorOf(() => Make(parent.Path, "good", "entity", title: new string('x', 101))));
    }

    // ------------------------------------------------------------------ templates

    [Theory]
    [InlineData("Empty")]
    [InlineData("ENTITY")]
    [InlineData("item_recipe")]
    [InlineData("items")]
    [InlineData("nope")]
    [InlineData("../empty")]
    [InlineData("empty ")]
    [InlineData("")]
    [InlineData("tech")]
    public void UnknownTemplates_AreRejected_WithoutTouchingDisk(string template)
    {
        using var parent = new TempDir();
        Assert.Equal(ScaffoldError.UnknownTemplate, ErrorOf(() => Make(parent.Path, "mymod", template)));
        Assert.Empty(Entries(parent.Path));
    }

    [Fact]
    public void DefaultTemplate_IsEmpty()
    {
        using var parent = new TempDir();
        var r = ModScaffolder.Create(new ScaffoldRequest("defaulted"), parent.Path, Today);
        Assert.Contains("data.lua", r.Files);
        Assert.Contains("control.lua", r.Files);
        Assert.DoesNotContain("graphics/icons/defaulted-item.png", r.Files);
        Assert.Equal(DefaultDescription, JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Folder, "info.json"))).RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public void EmptyTemplate_HasOnlyCommentsInLua()
    {
        using var parent = new TempDir();
        var r = Make(parent.Path, "bare");
        foreach (var lua in new[] { "data.lua", "control.lua" })
        {
            var text = Read(r.Folder, lua);
            var code = Regex.Replace(text, @"--\[(=*)\[.*?\]\1\]|--[^\n]*", "", RegexOptions.Singleline);
            Assert.True(code.Trim().Length == 0, $"{lua} should be comments only, found: {code.Trim()}");
        }
        Assert.DoesNotContain(r.Files, f => f.StartsWith("graphics/", StringComparison.Ordinal));
    }

    [Fact]
    public void TemplatesDiffer_AndDoNotShareLeftoverFiles()
    {
        using var parent = new TempDir();
        var sets = TemplateIds.Select(t => Make(parent.Path, "m-" + t, t).Files.ToHashSet()).ToList();
        Assert.False(sets[0].Contains("settings.lua"));
        Assert.True(sets[3].Contains("settings.lua"));
        Assert.Contains("prototypes/m-item-recipe-item.lua", sets[1]);
        Assert.Contains("prototypes/m-entity-chest.lua", sets[2]);
        Assert.False(sets[0].Any(f => f.StartsWith("prototypes/", StringComparison.Ordinal)));
        Assert.False(sets[0].Any(f => f.EndsWith(".png", StringComparison.Ordinal) && f != "thumbnail.png"));
        Assert.False(sets[3].Any(f => f.EndsWith(".png", StringComparison.Ordinal) && f != "thumbnail.png"));
    }

    // ------------------------------------------------------------------ fields

    [Fact]
    public void Defaults_TitleHumanized_DescriptionDefault()
    {
        using var parent = new TempDir();
        var r = Make(parent.Path, "hello-world");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Folder, "info.json")));
        Assert.Equal("Hello World", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(DefaultDescription, doc.RootElement.GetProperty("description").GetString());
        var single = Make(parent.Path, "single");
        Assert.Equal("Single", JsonDocument.Parse(File.ReadAllText(Path.Combine(single.Folder, "info.json"))).RootElement.GetProperty("title").GetString());
        var multi = Make(parent.Path, "a-b-c");
        Assert.Equal("A B C", JsonDocument.Parse(File.ReadAllText(Path.Combine(multi.Folder, "info.json"))).RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Author_Default_UsesEnvironmentVariableOtherwiseYourName_AndRestoresEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("FMS_AUTHOR");
        try
        {
            using var parent = new TempDir();
            string AuthorOf(string name, string? explicitAuthor = null)
            {
                var r = Make(parent.Path, name, author: explicitAuthor);
                return JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Folder, "info.json"))).RootElement.GetProperty("author").GetString()!;
            }

            Environment.SetEnvironmentVariable("FMS_AUTHOR", null);
            Assert.Equal("Your Name", AuthorOf("noenv"));
            Assert.Equal("Your Name", AuthorOf("noenv-blank", "   "));
            Assert.Equal("Explicit", AuthorOf("noenv-explicit", "  Explicit  "));

            Environment.SetEnvironmentVariable("FMS_AUTHOR", "Env Author");
            Assert.Equal("Env Author", AuthorOf("withenv"));
            Assert.Equal("Env Author", AuthorOf("withenv-blank", " \t "));
            Assert.Equal("Explicit", AuthorOf("withenv-explicit", "Explicit"));
            FullCheck(parent.Path, "withenv-check", "entity");

            Environment.SetEnvironmentVariable("FMS_AUTHOR", "O\"Brien \\ [[x]] {{y}} 100%");
            Assert.Equal("O\"Brien \\ [[x]] {{y}} 100%", AuthorOf("nasty-env"));
            foreach (var template in TemplateIds) FullCheck(parent.Path, "nasty-env-" + template, template);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FMS_AUTHOR", previous);
        }
    }

    [Fact]
    public void Fields_AreTrimmed_AndBlankMeansDefault()
    {
        using var parent = new TempDir();
        var r = Make(parent.Path, "trim-me", title: "  Padded Title \t ", author: " Auth ", description: "\u00a0 spaced out  ");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Folder, "info.json")));
        Assert.Equal("Padded Title", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("Auth", doc.RootElement.GetProperty("author").GetString());
        Assert.Equal("spaced out", doc.RootElement.GetProperty("description").GetString().Trim('\u00a0', ' '));

        var blank = Make(parent.Path, "blank-fields", title: "   ", description: "  ");
        using var doc2 = JsonDocument.Parse(File.ReadAllText(Path.Combine(blank.Folder, "info.json")));
        Assert.Equal("Blank Fields", doc2.RootElement.GetProperty("title").GetString());
        Assert.Equal(DefaultDescription, doc2.RootElement.GetProperty("description").GetString());
        var empty = Make(parent.Path, "empty-fields", title: "", description: "", author: "");
        using var doc3 = JsonDocument.Parse(File.ReadAllText(Path.Combine(empty.Folder, "info.json")));
        Assert.Equal("Empty Fields", doc3.RootElement.GetProperty("title").GetString());
        Assert.Equal(DefaultDescription, doc3.RootElement.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("title", "a\nb")]
    [InlineData("title", "a\rb")]
    [InlineData("title", "a\tb")]
    [InlineData("title", "a\0b")]
    [InlineData("title", "a\u0001b")]
    [InlineData("title", "a\u001fb")]
    [InlineData("title", "a\u007fb")]
    [InlineData("title", "a\u001bb")]
    [InlineData("author", "a\nb")]
    [InlineData("author", "a\0b")]
    [InlineData("author", "a\tb")]
    [InlineData("author", "a\u007fb")]
    [InlineData("author", "a\u0008b")]
    [InlineData("description", "a\nb")]
    [InlineData("description", "line1\r\nline2")]
    [InlineData("description", "a\0b")]
    [InlineData("description", "a\tb")]
    [InlineData("description", "a\u007fb")]
    [InlineData("description", "a\u001fb")]
    public void ControlCharactersInFields_AreInvalidField(string field, string value)
    {
        using var parent = new TempDir();
        var kind = ErrorOf(() => Make(parent.Path, "ctl", "entity",
            title: field == "title" ? value : null, author: field == "author" ? value : null, description: field == "description" ? value : null));
        Assert.Equal(ScaffoldError.InvalidField, kind);
        Assert.Empty(Entries(parent.Path));
    }

    [Fact]
    public void FieldLengthLimits_AreExactlyEnforced()
    {
        using var parent = new TempDir();
        string T100 = new('t', 100), T101 = new('t', 101), D500 = new('d', 500), D501 = new('d', 501);
        Make(parent.Path, "len-ok", "empty", T100, T100, D500);
        Assert.Equal(ScaffoldError.InvalidField, ErrorOf(() => Make(parent.Path, "len-t", "empty", title: T101)));
        Assert.Equal(ScaffoldError.InvalidField, ErrorOf(() => Make(parent.Path, "len-a", "empty", author: T101)));
        Assert.Equal(ScaffoldError.InvalidField, ErrorOf(() => Make(parent.Path, "len-d", "empty", description: D501)));
        Assert.Equal(ScaffoldError.InvalidField, ErrorOf(() => Make(parent.Path, "len-d2", "empty", description: new string('d', 100000))));
        // Padding is trimmed before the limit applies.
        Make(parent.Path, "len-pad", "empty", "  " + T100 + "  ", " " + T100 + " ", " " + D500 + " ");
        Assert.Equal(new[] { "len-ok", "len-pad" }, Entries(parent.Path).Where(e => e != ".fmstudio").ToArray());
        // Full contract check with maximal values on every template.
        foreach (var template in TemplateIds)
            FullCheck(parent.Path, "max-" + template, template, T100, T100, D500, strictLocale: true);
    }

    internal static readonly string[] NastyValues =
    {
        "He said \"hi\" \\ back",
        "Ünïcødé ✓",
        "emoji \U0001F600 pair \U0001F468\u200d\U0001F4BB",
        "日本語のタイトル",
        "Ωmega — “smart quotes” ‘x’",
        "a__b__c",
        "__MOD__ __1__ __CONTROL__ __ENTITY__x__",
        "]]",
        "[[",
        "]] .. os.exit() .. [[",
        "]=] [=[ ]==]",
        "--[[ comment",
        "]]-- end",
        "{{x}}",
        "{{title}} {{name}} {{author}} {{description}} {{{x}}}",
        "{{ x }} }} {{",
        "\\n",
        "\\n\\t\\r\\0 \\u0041 \\x41",
        "\\",
        "\\\\",
        "\\\"",
        "\"",
        "'",
        "'single' \"double\" `tick`",
        "100% %s %d %1% %% %",
        "$1 $& ${x} $$",
        "</script><b>x</b> &amp; <!-- --> ",
        "key=value",
        "[section]",
        "; semicolon # hash",
        "a = b = c",
        "\" .. os.execute('x') .. \"",
        "end end end",
        "x\u2028y\u2029z",
        "\u00a0nbsp\u00a0inside",
        "trailing backslash \\",
        "{ } [ ] ( ) , ; :",
        "a\u200bb\ufeffc",
    };

    [Fact]
    public void NastyFieldValues_RoundTripAndNeverBreakAnyTemplate()
    {
        foreach (var value in NastyValues)
        {
            foreach (var template in TemplateIds)
            {
                using var parent = new TempDir();
                try
                {
                    var trimmed = value.Trim();
                    var r = Make(parent.Path, "nasty", template, value, value, value);
                    AssertMatchesContract(r, parent.Path, "nasty", template, trimmed, trimmed, trimmed, strictLocaleValues: false);
                    // Raw `{{x}}` must be literal and the same in info.json.
                    if (value.Contains("{{x}}"))
                        Assert.Contains("{{x}}", JsonDocument.Parse(File.ReadAllText(Path.Combine(r.Folder, "info.json"))).RootElement.GetProperty("title").GetString());
                }
                catch (Exception ex) when (ex is not XunitException)
                {
                    throw new XunitException($"value [{value}] template {template}: {ex}");
                }
                catch (XunitException ex)
                {
                    throw new XunitException($"value [{value}] template {template}: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void NastyDescriptionOnly_DoesNotLeakIntoOtherFiles()
    {
        // A description containing Lua/cfg/JSON syntax must not change the structure of data.lua/cfg.
        foreach (var template in TemplateIds)
        {
            using var parent = new TempDir();
            using var reference = new TempDir();
            var plain = Make(reference.Path, "leak", template);
            var nasty = Make(parent.Path, "leak", template, description: "\"]] ) end } {{name}} \\ literal\\n");
            foreach (var lua in plain.Files.Where(f => f.EndsWith(".lua", StringComparison.Ordinal)))
                Assert.Equal(LuaDiagnostics.Check(Read(plain.Folder, lua)).Count, LuaDiagnostics.Check(Read(nasty.Folder, lua)).Count);
            Assert.Equal(plain.Files.ToList(), nasty.Files.ToList());
            // Only description-bearing files may differ; the number of lines in cfg and lua stays the same.
            foreach (var file in plain.Files.Where(f => f.EndsWith(".cfg", StringComparison.Ordinal) || f.EndsWith(".lua", StringComparison.Ordinal)))
                Assert.Equal(Read(plain.Folder, file).Split('\n').Length, Read(nasty.Folder, file).Split('\n').Length);
        }
    }

    [Fact]
    public void PlainValues_LeaveNoUnexpandedPlaceholders()
    {
        foreach (var template in TemplateIds)
        {
            using var parent = new TempDir();
            var r = Make(parent.Path, "plain-mod", template, "Plain Title", "Plain Author", "Plain description.");
            foreach (var f in r.Files.Where(f => !f.EndsWith(".png", StringComparison.Ordinal)))
            {
                var text = Read(r.Folder, f);
                Assert.False(Regex.IsMatch(text, @"\{\{\s*[A-Za-z_]+\s*\}\}"), f + " has an unexpanded {{placeholder}}");
                Assert.DoesNotContain("$name", text);
            }
        }
    }

    // ------------------------------------------------------------------ changelog / date

    [Fact]
    public void Changelog_HasEntryDatedToday_AndExplicitDateIsUsed()
    {
        using var parent = new TempDir();
        var r = Make(parent.Path, "dated", today: new DateOnly(2031, 12, 31));
        var text = Read(r.Folder, "changelog.txt");
        Assert.Contains("Version: 0.1.0", text);
        Assert.Contains("Date: 2031-12-31", text);
        Assert.DoesNotContain("2026-03-05", text);

        var leap = Make(parent.Path, "leap", today: new DateOnly(2028, 2, 29));
        Assert.Contains("Date: 2028-02-29", Read(leap.Folder, "changelog.txt"));
        Assert.True(ModValidator.Validate(leap.Folder).Issues.Count == 0);
    }

    [Fact]
    public void Changelog_DefaultDate_IsTodayUtc()
    {
        using var parent = new TempDir();
        var before = DateOnly.FromDateTime(DateTime.UtcNow);
        var r = ModScaffolder.Create(new ScaffoldRequest("utc-day"), parent.Path);
        var after = DateOnly.FromDateTime(DateTime.UtcNow);
        var text = Read(r.Folder, "changelog.txt");
        var m = Regex.Match(text, @"Date: (\d{4}-\d{2}-\d{2})");
        Assert.True(m.Success);
        var date = DateOnly.ParseExact(m.Groups[1].Value, "yyyy-MM-dd");
        Assert.True(date == before || date == after, $"changelog date {date} is not today UTC ({before})");
        Assert.True(ModValidator.Validate(r.Folder).Issues.Count == 0);
    }

    // ------------------------------------------------------------------ determinism

    [Theory]
    [InlineData("empty")]
    [InlineData("item-recipe")]
    [InlineData("entity")]
    [InlineData("runtime-script")]
    public void SameInputAndDate_ProducesByteIdenticalOutput(string template)
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var ra = Make(a.Path, "determ", template, "T \"q\"", "Auth", "Desc \\ ]]");
        var rb = Make(b.Path, "determ", template, "T \"q\"", "Auth", "Desc \\ ]]");
        Assert.Equal(ra.Files.ToList(), rb.Files.ToList());
        foreach (var f in ra.Files)
            Assert.True(File.ReadAllBytes(Path.Combine(ra.Folder, f)).SequenceEqual(File.ReadAllBytes(Path.Combine(rb.Folder, f))), "differs: " + f);

        // A different date changes only the changelog.
        using var c = new TempDir();
        var rc = Make(c.Path, "determ", template, "T \"q\"", "Auth", "Desc \\ ]]", today: new DateOnly(2030, 1, 1));
        foreach (var f in ra.Files.Where(f => f != "changelog.txt"))
            Assert.True(File.ReadAllBytes(Path.Combine(ra.Folder, f)).SequenceEqual(File.ReadAllBytes(Path.Combine(rc.Folder, f))), "date leaked into: " + f);
        Assert.NotEqual(Read(ra.Folder, "changelog.txt"), Read(rc.Folder, "changelog.txt"));
    }

    [Fact]
    public void DifferentNames_ProduceDifferentInfoAndLocale_ButSameStructure()
    {
        using var parent = new TempDir();
        var a = Make(parent.Path, "alpha-mod", "entity");
        var b = Make(parent.Path, "beta-mod", "entity");
        Assert.Equal(a.Files.Select(f => f.Replace("alpha-mod", "X")).ToList(), b.Files.Select(f => f.Replace("beta-mod", "X")).ToList());
        Assert.DoesNotContain("beta", AllLua(a.Folder));
        Assert.DoesNotContain("alpha", AllLua(b.Folder));
    }

    // ------------------------------------------------------------------ filesystem behavior

    [Fact]
    public void ParentIsCreatedIfMissing_IncludingNestedLevels()
    {
        using var root = new TempDir();
        var parent = root.Combine("a", "b", "c");
        var r = Make(parent, "deep");
        Assert.Equal(Path.Combine(parent, "deep"), r.Folder);
        Assert.True(File.Exists(Path.Combine(parent, "deep", "info.json")));
        Assert.Equal(new[] { "deep" }, Entries(parent).Where(e => e != ".fmstudio").ToArray());
    }

    [Fact]
    public void RelativeParent_ResolvesToAFullFolderPath()
    {
        using var root = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        // Avoid changing the process cwd (other tests run in parallel); use a relative path computed from it instead.
        var relative = Path.GetRelativePath(previous, root.Combine("rel"));
        var r = Make(relative, "relmod");
        Assert.True(Path.IsPathRooted(r.Folder), "Folder must be a full path");
        Assert.Equal(Path.GetFullPath(root.Combine("rel", "relmod")), Path.GetFullPath(r.Folder));
    }

    [Fact]
    public void AfterSuccess_ParentHoldsOnlyModFolders_AndAtMostAnEmptyStagingRoot()
    {
        using var parent = new TempDir();
        foreach (var template in TemplateIds) Make(parent.Path, "clean-" + template, template);
        var entries = Entries(parent.Path);
        Assert.Equal(TemplateIds.Select(t => "clean-" + t).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            entries.Where(e => e != ".fmstudio").ToList());
        if (entries.Contains(".fmstudio"))
            Assert.Empty(Directory.GetFileSystemEntries(parent.Combine(".fmstudio")));
    }

    [Fact]
    public void ExistingFolder_IsNeverOverwrittenOrMerged()
    {
        using var parent = new TempDir();
        parent.Write("taken/precious.txt", "mine");
        parent.Write("taken/info.json", "{ not mine }");
        var before = DiskFiles(parent.Combine("taken"));
        foreach (var template in TemplateIds)
            Assert.Equal(ScaffoldError.Exists, ErrorOf(() => Make(parent.Path, "taken", template)));
        Assert.Equal(before, DiskFiles(parent.Combine("taken")));
        Assert.Equal("mine", File.ReadAllText(parent.Combine("taken", "precious.txt")));
        Assert.Equal("{ not mine }", File.ReadAllText(parent.Combine("taken", "info.json")));
        Assert.Equal(new[] { "taken" }, Entries(parent.Path).Where(e => e != ".fmstudio").ToArray());
    }

    [Fact]
    public void ExistingEmptyFolder_IsExists()
    {
        using var parent = new TempDir();
        Directory.CreateDirectory(parent.Combine("hollow"));
        Assert.Equal(ScaffoldError.Exists, ErrorOf(() => Make(parent.Path, "hollow")));
        Assert.Empty(Entries(parent.Combine("hollow")));
    }

    [Fact]
    public void ExistingFileWithTheName_IsExists_AndStaysUntouched()
    {
        using var parent = new TempDir();
        parent.Write("filename", "I am a file");
        Assert.Equal(ScaffoldError.Exists, ErrorOf(() => Make(parent.Path, "filename", "entity")));
        Assert.Equal("I am a file", File.ReadAllText(parent.Combine("filename")));
        Assert.Equal(new[] { "filename" }, Entries(parent.Path).Where(e => e != ".fmstudio").ToArray());
    }

    [Fact]
    public void SecondCreateOfSameName_Fails_AndFirstResultSurvivesIntact()
    {
        using var parent = new TempDir();
        var first = Make(parent.Path, "twice", "entity", title: "First");
        var snapshot = first.Files.ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(first.Folder, f)));
        Assert.Equal(ScaffoldError.Exists, ErrorOf(() => Make(parent.Path, "twice", "runtime-script", title: "Second")));
        Assert.Equal(snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), DiskFiles(first.Folder));
        foreach (var (f, bytes) in snapshot) Assert.True(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(first.Folder, f))));
    }

    [Fact]
    public void Failures_LeaveNoPartialFolderAndNoStagingDirectory()
    {
        using var parent = new TempDir();
        parent.Write("existing/x.txt", "x");
        var before = Entries(parent.Path);
        ErrorOf(() => Make(parent.Path, "existing"));
        ErrorOf(() => Make(parent.Path, "bad name"));
        ErrorOf(() => Make(parent.Path, "good", "nope"));
        ErrorOf(() => Make(parent.Path, "good", "entity", title: "x\ny"));
        ErrorOf(() => Make(parent.Path, "good", "entity", description: new string('d', 501)));
        var after = Entries(parent.Path);
        Assert.Equal(before.Where(e => e != ".fmstudio").ToList(), after.Where(e => e != ".fmstudio").ToList());
        var staging = parent.Combine(".fmstudio");
        if (Directory.Exists(staging)) Assert.Empty(Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ParentThatIsAFile_FailsCleanly_AndStaysAFile()
    {
        using var root = new TempDir();
        var file = root.Write("im-a-file", "data");
        Exception? thrown = null;
        try { Make(file, "x"); } catch (Exception e) { thrown = e; }
        Assert.NotNull(thrown);
        Assert.Equal("data", File.ReadAllText(file));
        Assert.Equal(new[] { "im-a-file" }, Entries(root.Path));
    }

    [Fact]
    public void ConcurrentCreatesOfTheSameName_ExactlyOneWins_OthersGetExists()
    {
        for (var round = 0; round < 5; round++)
        {
            using var parent = new TempDir();
            const int n = 12;
            using var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, n).Select(i => Task.Run(() =>
            {
                gate.Wait();
                try { return (Result: (ScaffoldResult?)Make(parent.Path, "race", TemplateIds[i % 4], title: "Racer " + i), Error: (Exception?)null); }
                catch (Exception ex) { return (null, ex); }
            })).ToArray();
            gate.Set();
            Task.WaitAll(tasks);
            var winners = tasks.Select(t => t.Result).Where(r => r.Result is not null).ToList();
            var losers = tasks.Select(t => t.Result).Where(r => r.Result is null).ToList();
            Assert.Equal(1, winners.Count);
            Assert.Equal(n - 1, losers.Count);
            foreach (var l in losers)
            {
                var ex = Assert.IsType<ScaffoldException>(l.Error);
                Assert.Equal(ScaffoldError.Exists, ex.Kind);
            }
            // The surviving folder is internally consistent (one winner's files only) and clean.
            var winner = winners[0].Result!;
            Assert.Equal(winner.Files.ToList(), DiskFiles(winner.Folder));
            Assert.True(ModValidator.Validate(winner.Folder).Issues.Count == 0);
            var entries = Entries(parent.Path);
            Assert.Equal(new[] { "race" }, entries.Where(e => e != ".fmstudio").ToArray());
            if (entries.Contains(".fmstudio")) Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Combine(".fmstudio"), "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void ConcurrentCreatesOfDifferentNames_AllSucceed_AndAreIndependent()
    {
        using var parent = new TempDir();
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() => Make(parent.Path, $"par-{i}", TemplateIds[i % 4], title: "Mod " + i))).ToArray();
        Task.WaitAll(tasks);
        Assert.Equal(16, tasks.Select(t => t.Result.Name).Distinct().Count());
        foreach (var t in tasks)
        {
            Assert.True(ModValidator.Validate(t.Result.Folder).Issues.Count == 0, t.Result.Name);
            Assert.Equal(t.Result.Files.ToList(), DiskFiles(t.Result.Folder));
        }
        var entries = Entries(parent.Path);
        Assert.Equal(16, entries.Count(e => e != ".fmstudio"));
        if (entries.Contains(".fmstudio")) Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Combine(".fmstudio"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CreatedModsCanBePackedTwice_AndReusedAsWorkspaceMods()
    {
        using var parent = new TempDir();
        using var output = new TempDir();
        var r = Make(parent.Path, "repack", "item-recipe");
        Assert.True(ModPackager.Pack(r.Folder, output.Path).Success);
        Assert.True(ModPackager.Pack(r.Folder, output.Path, force: true).Success);
        // Packing does not change the scaffold.
        Assert.Equal(r.Files.ToList(), DiskFiles(r.Folder));
    }

    // ------------------------------------------------------------------ seeded fuzz

    [Fact]
    public void Fuzz_RandomNamesAndFields_AgreeWithTheContractOracle()
    {
        var rng = new Random(20261102);
        const string nameAlphabet = "abcXYZ019_-";
        const string badNameChars = "./\\ :é*";
        const string fieldAlphabet = "abc XYZ09\"\\_-[]{}%'.,;:=#é✓😀<>&|$`~!?()+";
        string[] controls = { "\n", "\r", "\t", "\0", "\u0001", "\u007f", "\u001f" };
        var reserved = new Regex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);

        string RandomName()
        {
            var len = rng.Next(1, 14);
            var sb = new StringBuilder();
            for (var i = 0; i < len; i++)
                sb.Append(rng.Next(12) == 0 ? badNameChars[rng.Next(badNameChars.Length)] : nameAlphabet[rng.Next(nameAlphabet.Length)]);
            return rng.Next(25) == 0 ? new[] { "CON", "com3", "Lpt9", "aux", "NUL", "PRN" }[rng.Next(6)] : sb.ToString();
        }

        string? RandomField(int max)
        {
            switch (rng.Next(8))
            {
                case 0: return null;
                case 1: return "";
                case 2: return new string(' ', rng.Next(1, 4));
            }
            var len = rng.Next(1, 40);
            var sb = new StringBuilder();
            for (var i = 0; i < len; i++)
            {
                if (rng.Next(60) == 0) sb.Append(controls[rng.Next(controls.Length)]);
                else if (rng.Next(25) == 0) sb.Append(new[] { "]]", "[[", "{{x}}", "__", "\\n", "%s", "\\\"" }[rng.Next(7)]);
                else
                {
                    var s = fieldAlphabet.Length;
                    // pick by text element to keep surrogate pairs intact
                    var idx = rng.Next(s);
                    var ch = fieldAlphabet[idx];
                    if (char.IsHighSurrogate(ch)) sb.Append(ch).Append(fieldAlphabet[idx + 1]);
                    else if (char.IsLowSurrogate(ch)) sb.Append("x");
                    else sb.Append(ch);
                }
            }
            var value = sb.ToString();
            if (rng.Next(20) == 0) value += new string('z', max); // push over the limit sometimes
            return value;
        }

        static bool Ctl(char c) => c < 0x20 || c == 0x7f;
        bool FieldInvalid(string? v, int max) => v is not null && (v.Trim().Length > max || v.Trim().Any(Ctl));

        var ok = 0;
        var rejected = 0;
        for (var i = 0; i < 160; i++)
        {
            var name = RandomName();
            var template = TemplateIds[rng.Next(4)];
            var title = RandomField(100);
            var author = RandomField(100);
            var description = RandomField(500);
            var nameValid = name.Length is >= 1 and <= 100 && name.All(c => c < 128 && (char.IsLetterOrDigit(c) || c is '_' or '-')) && !reserved.IsMatch(name);
            // Leading/trailing control chars may be trimmed away before the control-character check; skip such ambiguous inputs.
            bool Ambiguous(string? v) => v is not null && v.Any(Ctl) && !v.Trim().Any(Ctl);
            if (Ambiguous(title) || Ambiguous(author) || Ambiguous(description)) continue;
            var fieldsInvalid = FieldInvalid(title, 100) || FieldInvalid(author, 100) || FieldInvalid(description, 500);

            using var parent = new TempDir();
            var label = $"#{i} name=[{name}] template={template} title=[{title}] author=[{author}] description=[{description}]";
            try
            {
                if (!nameValid)
                {
                    Assert.Equal(ScaffoldError.InvalidName, ErrorOf(() => Make(parent.Path, name, template, title, author, description)));
                    Assert.Empty(Entries(parent.Path).Where(e => e != ".fmstudio"));
                    rejected++;
                }
                else if (fieldsInvalid)
                {
                    Assert.Equal(ScaffoldError.InvalidField, ErrorOf(() => Make(parent.Path, name, template, title, author, description)));
                    Assert.Empty(Entries(parent.Path).Where(e => e != ".fmstudio"));
                    rejected++;
                }
                else
                {
                    var r = Make(parent.Path, name, template, title, author, description);
                    var t = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
                    var a = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
                    var d = string.IsNullOrWhiteSpace(description) ? DefaultDescription : description.Trim();
                    AssertMatchesContract(r, parent.Path, name, template, t ?? HumanizeOrNull(name), a, d, strictLocaleValues: false);
                    ok++;
                }
            }
            catch (XunitException ex) { throw new XunitException(label + "\n" + ex.Message); }
        }
        Assert.True(ok > 40 && rejected > 10, $"fuzz produced a poor mix: ok={ok}, rejected={rejected}");
    }

    // ------------------------------------------------------------------ CLI

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Program.Run(args, output, error, null);
        return (code, output.ToString(), error.ToString());
    }

    private static JsonElement Prop(JsonElement el, string name) =>
        el.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonElement[] ArrayOf(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        var arr = root.EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.Array).Value;
        return arr.EnumerateArray().ToArray();
    }

    [Fact]
    public void Cli_List_ShowsAllTemplatesInOrder()
    {
        var (code, output, _) = Run("new", "--list");
        Assert.Equal(Program.ExitOk, code);
        var positions = TemplateIds.Select(id => output.IndexOf(id, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0, "missing template in listing:\n" + output));
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);
    }

    [Fact]
    public void Cli_List_Json_IsMachineReadable_AndMatchesCore()
    {
        var (code, output, _) = Run("new", "--list", "--json");
        Assert.Equal(Program.ExitOk, code);
        using var doc = JsonDocument.Parse(output);
        var items = ArrayOf(doc.RootElement);
        Assert.Equal(TemplateIds.ToList(), items.Select(i => Prop(i, "id").GetString()!).ToList());
        for (var i = 0; i < items.Length; i++)
        {
            Assert.Equal(ModScaffolder.Templates[i].Title, Prop(items[i], "title").GetString());
            Assert.Equal(ModScaffolder.Templates[i].Description, Prop(items[i], "description").GetString());
        }
    }

    [Fact]
    public void Cli_New_CreatesModInOut_AndReportsPathFilesAndNextSteps()
    {
        using var dir = new TempDir();
        var (code, output, error) = Run("new", "cli-mod", "--out", dir.Path);
        Assert.Equal(Program.ExitOk, code);
        Assert.Equal("", error.Trim());
        var folder = Path.Combine(dir.Path, "cli-mod");
        Assert.Contains(folder, output);
        Assert.Contains("info.json", output);
        Assert.Contains("data.lua", output);
        Assert.True(output.Split('\n').Length >= 6, "expected a file list and next steps:\n" + output);
        Assert.True(ModValidator.Validate(folder).Issues.Count == 0);
        Assert.Equal("Cli Mod", JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "info.json"))).RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Cli_New_AcceptsOptionsBeforeAndAfterTheName_AndAllFields()
    {
        using var dir = new TempDir();
        var title = "He said \"hi\" \\ back";
        var (code, _, error) = Run("new", "--template", "entity", "--title", title, "opt-first", "--author", "Ünïcødé ✓", "--description", "100% {{x}} ]]", "--out", dir.Path);
        Assert.True(code == Program.ExitOk, error);
        var folder = Path.Combine(dir.Path, "opt-first");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "info.json")));
        Assert.Equal(title, doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("Ünïcødé ✓", doc.RootElement.GetProperty("author").GetString());
        Assert.Equal("100% {{x}} ]]", doc.RootElement.GetProperty("description").GetString());
        Assert.True(File.Exists(Path.Combine(folder, "graphics")) || Directory.Exists(Path.Combine(folder, "graphics")));
        Assert.True(ModValidator.Validate(folder).Issues.Count == 0);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("item-recipe")]
    [InlineData("entity")]
    [InlineData("runtime-script")]
    public void Cli_New_EveryTemplate_IsCleanAndPackable(string template)
    {
        using var dir = new TempDir();
        using var output = new TempDir();
        var (code, _, error) = Run("new", "cli-" + template, "-t", template, "--out", dir.Path);
        if (code != Program.ExitOk) (code, _, error) = Run("new", "cli-" + template, "--template", template, "--out", dir.Path);
        Assert.True(code == Program.ExitOk, error);
        var folder = Path.Combine(dir.Path, "cli-" + template);
        Assert.True(ModValidator.Validate(folder).Issues.Count == 0);
        Assert.True(Run("validate", folder).Code == Program.ExitOk);
        Assert.True(ModPackager.Pack(folder, output.Path).Success);
    }

    [Fact]
    public void Cli_New_Json_ReportsTheResult()
    {
        using var dir = new TempDir();
        var (code, output, error) = Run("new", "json-mod", "--template", "item-recipe", "--out", dir.Path, "--json");
        Assert.True(code == Program.ExitOk, error);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("json-mod", Prop(doc.RootElement, "name").GetString());
        Assert.Equal("0.1.0", Prop(doc.RootElement, "version").GetString());
        Assert.Equal(Path.Combine(dir.Path, "json-mod"), Prop(doc.RootElement, "folder").GetString());
        var files = Prop(doc.RootElement, "files").EnumerateArray().Select(f => f.GetString()!).ToList();
        Assert.Equal(DiskFiles(Path.Combine(dir.Path, "json-mod")), files);
    }

    [Fact]
    public void Cli_New_DefaultOutput_IsTheProjectWorkspace_CreatedIfMissing()
    {
        using var project = new TempDir();
        var (code, _, error) = Run("new", "ws-default", "--project", project.Path);
        Assert.True(code == Program.ExitOk, error);
        Assert.True(File.Exists(project.Combine("mods", "ws-default", "info.json")));

        // fmstudio.json "workspace" wins over "mods".
        using var configured = new TempDir();
        File.WriteAllText(configured.Combine(ProjectConfig.FileName), "{ \"workspace\": \"my-ws/nested\" }");
        var (code2, _, error2) = Run("new", "ws-config", "-p", configured.Path);
        Assert.True(code2 == Program.ExitOk, error2);
        Assert.True(File.Exists(configured.Combine("my-ws", "nested", "ws-config", "info.json")));
        Assert.False(Directory.Exists(configured.Combine("mods")));
    }

    [Fact]
    public void Cli_New_OutOverridesWorkspace()
    {
        using var project = new TempDir();
        using var elsewhere = new TempDir();
        File.WriteAllText(project.Combine(ProjectConfig.FileName), "{ \"workspace\": \"ws\" }");
        var (code, _, error) = Run("new", "out-wins", "-p", project.Path, "--out", elsewhere.Combine("custom"));
        Assert.True(code == Program.ExitOk, error);
        Assert.True(File.Exists(elsewhere.Combine("custom", "out-wins", "info.json")));
        Assert.False(Directory.Exists(project.Combine("ws")));
        Assert.False(Directory.Exists(project.Combine("mods")));
    }

    [Fact]
    public void Cli_New_MissingName_IsUsageError()
    {
        using var dir = new TempDir();
        var (code, _, error) = Run("new", "--out", dir.Path);
        Assert.Equal(Program.ExitUsageError, code);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(Entries(dir.Path));
    }

    [Theory]
    [InlineData("bad.name")]
    [InlineData("bad name")]
    [InlineData("bad/name")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("com1")]
    [InlineData("héllo")]
    public void Cli_New_InvalidName_IsUsageError(string name)
    {
        using var dir = new TempDir();
        var (code, output, error) = Run("new", name, "--out", dir.Path);
        Assert.Equal(Program.ExitUsageError, code);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(Entries(dir.Path).Where(e => e != ".fmstudio"));
        _ = output;
    }

    [Fact]
    public void Cli_New_UnknownTemplate_InvalidField_AndExists_AreUsageErrors()
    {
        using var dir = new TempDir();
        var (c1, _, e1) = Run("new", "mm", "--template", "nope", "--out", dir.Path);
        Assert.Equal(Program.ExitUsageError, c1);
        Assert.Contains("nope", e1);

        var (c2, _, e2) = Run("new", "mm", "--title", new string('x', 101), "--out", dir.Path);
        Assert.Equal(Program.ExitUsageError, c2);
        Assert.False(string.IsNullOrWhiteSpace(e2));
        var (c3, _, e3) = Run("new", "mm", "--description", "two\nlines", "--out", dir.Path);
        Assert.Equal(Program.ExitUsageError, c3);
        Assert.False(string.IsNullOrWhiteSpace(e3));
        Assert.Empty(Entries(dir.Path).Where(e => e != ".fmstudio"));

        Assert.Equal(Program.ExitOk, Run("new", "mm", "--out", dir.Path).Code);
        var before = DiskFiles(dir.Combine("mm"));
        var (c4, _, e4) = Run("new", "mm", "--out", dir.Path);
        Assert.Equal(Program.ExitUsageError, c4);
        Assert.Contains("exist", e4.ToLowerInvariant());
        Assert.Equal(before, DiskFiles(dir.Combine("mm")));
    }

    [Fact]
    public void Cli_New_Errors_WithJson_AreJsonObjectsOnStderr()
    {
        using var dir = new TempDir();
        Run("new", "dupe", "--out", dir.Path);
        foreach (var args in new[]
        {
            new[] { "new", "--json", "--out", dir.Path },
            new[] { "new", "bad.name", "--json", "--out", dir.Path },
            new[] { "new", "x", "--template", "nope", "--json", "--out", dir.Path },
            new[] { "new", "x", "--author", new string('a', 101), "--json", "--out", dir.Path },
            new[] { "new", "dupe", "--json", "--out", dir.Path },
        })
        {
            var (code, output, error) = Run(args);
            Assert.True(code == Program.ExitUsageError, string.Join(" ", args) + " -> " + code);
            using var doc = JsonDocument.Parse(error);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            var message = doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!).FirstOrDefault(s => s.Length > 0);
            Assert.NotNull(message);
            Assert.False(output.Contains("\"files\""), "no success payload on error");
        }
    }

    [Fact]
    public void Cli_New_DoesNotBreakTheOtherCommands()
    {
        Assert.Equal(Program.ExitUsageError, Run().Code);
        var (code, output, _) = Run("--help");
        Assert.True(code == Program.ExitOk || code == Program.ExitUsageError);
        Assert.Contains("new", output);
        Assert.Equal(Program.ExitUsageError, Run("frobnicate").Code);
    }

    [Fact]
    public void Cli_New_NastyValuesViaCommandLine_StayValid()
    {
        foreach (var value in NastyValues.Take(20))
        {
            using var dir = new TempDir();
            var (code, _, error) = Run("new", "nasty-cli", "--template", "runtime-script", "--title", value, "--author", value, "--description", value, "--out", dir.Path);
            Assert.True(code == Program.ExitOk, $"[{value}] {error}");
            var folder = Path.Combine(dir.Path, "nasty-cli");
            Assert.True(ModValidator.Validate(folder).Issues.Count == 0, value);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "info.json")));
            Assert.Equal(value.Trim(), doc.RootElement.GetProperty("title").GetString());
        }
    }
}
