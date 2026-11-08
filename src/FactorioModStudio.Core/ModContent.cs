using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <param name="Block">Id of one of <see cref="ModContent.Blocks"/>.</param>
/// <param name="Name">Prototype name: letters, digits, "_" and "-" (like mod names, which it usually starts with).</param>
/// <param name="Title">Optional display name; defaults to the humanized name.</param>
public sealed record ContentRequest(string Block, string Name, string? Title = null);

/// <param name="Create">Files the block adds (relative, '/'-separated).</param>
/// <param name="Change">Existing files the block appends to (data.lua, the English locale file).</param>
/// <param name="Prototypes">Names of the prototypes it defines.</param>
public sealed record ContentPlan(string Block, string Name, string Title, IReadOnlyList<string> Create, IReadOnlyList<string> Change, IReadOnlyList<string> Prototypes);

public enum ContentError { UnknownBlock, InvalidName, InvalidTitle, Exists, UnsupportedVersion, NotAMod }

/// <summary>A block could not be added. <see cref="Kind"/> says why; hosts map it to an exit code or HTTP status.</summary>
public sealed class ContentException : FmsException
{
    public ContentException(ContentError kind, string message, string? existingPath = null) : base(message)
    {
        Kind = kind;
        ExistingPath = existingPath;
    }

    public ContentError Kind { get; }

    /// <summary>For <see cref="ContentError.Exists"/>: the relative path that is already there.</summary>
    public string? ExistingPath { get; }
}

/// <summary>
/// Adds building blocks (<see cref="Blocks"/>) to an existing mod: a new <c>prototypes/&lt;name&gt;.lua</c>, the shared
/// copy helpers when they are missing, a <c>require</c> in data.lua and the English names in the locale file. With the game's data
/// folder at hand it also writes <c>graphics/icons/&lt;prototype&gt;.png</c>: the original icon recoloured in the block's colour.
/// Existing files are never overwritten, only appended to; when anything fails, every file is put back as it was.
/// </summary>
public static class ModContent
{
    public const string HelperPath = "prototypes/fms-copy-v2.lua";
    public const int MaxNameLength = 200;
    public const int MaxTitleLength = 100;

    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex Token = new("@@(id|title|icons|sprites)@@", RegexOptions.Compiled);
    private static readonly object Gate = new();

    public static IReadOnlyList<ContentBlockInfo> Blocks { get; } = ContentBlocks.All.Select(b => b.Info).ToList();

    /// <summary>What <see cref="Apply"/> would do, without touching the disk. Throws <see cref="ContentException"/> when it cannot.</summary>
    /// <param name="gameDataDirectory">Factorio's "data" folder (holding base/); without it the icons are only tinted in the game.</param>
    public static ContentPlan Plan(string modDirectory, ContentRequest request, string? gameDataDirectory = null) =>
        Prepare(modDirectory, request, gameDataDirectory).Plan;

    public static ContentPlan Apply(string modDirectory, ContentRequest request, string? gameDataDirectory = null)
    {
        lock (Gate)
        {
            var prepared = Prepare(modDirectory, request, gameDataDirectory);
            Write(Path.GetFullPath(modDirectory), prepared.Files);
            return prepared.Plan;
        }
    }

    // ------------------------------------------------------------------ preparing

    private sealed record Prepared(ContentPlan Plan, IReadOnlyDictionary<string, byte[]> Files);

    private static Prepared Prepare(string modDirectory, ContentRequest request, string? gameDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(modDirectory);

        var block = ContentBlocks.All.FirstOrDefault(b => string.Equals(b.Info.Id, request.Block, StringComparison.Ordinal))
                    ?? throw new ContentException(ContentError.UnknownBlock,
                        $"Unknown block \"{request.Block}\". Available: {string.Join(", ", Blocks.Select(b => b.Id))}.");

        var name = (request.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || !NamePattern.IsMatch(name))
            throw new ContentException(ContentError.InvalidName,
                $"A prototype name has 1-{MaxNameLength} letters, digits, \"_\" or \"-\".");
        var title = CleanTitle(request.Title) ?? ModScaffolder.Humanize(name);

        var infoPath = Path.Combine(root, "info.json");
        if (!File.Exists(infoPath)) throw new ContentException(ContentError.NotAMod, "There is no info.json in the mod folder.");
        ModInfo info;
        try { info = ModInfo.Load(infoPath); }
        catch (System.Text.Json.JsonException) { throw new ContentException(ContentError.NotAMod, "The mod's info.json is not valid JSON."); }
        var version = info.FactorioVersion?.Trim() ?? "";
        if (!IsSupported(version, block.Info.SupportedVersions))
            throw new ContentException(ContentError.UnsupportedVersion,
                $"The block \"{block.Info.Id}\" is written for Factorio {string.Join(" and ", block.Info.SupportedVersions)}; this mod targets {(version.Length > 0 ? version : "no factorio_version")}.");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var create = new List<string>();
        var change = new List<string>();

        var prototypePath = $"prototypes/{name}.lua";
        if (FindCaseInsensitive(root, prototypePath) is { } taken)
            throw new ContentException(ContentError.Exists, $"{taken} already exists; choose another name.", taken);

        // Icons: an icon file that is already there is used as it is; otherwise the original is recoloured when the game's data
        // is at hand. A prototype without a file keeps the original icon, tinted in the game.
        var modName = info.Name ?? Path.GetFileName(root);
        var iconEntries = new List<string>();
        foreach (var icon in block.Icons)
        {
            var prototype = name + icon.Suffix;
            var iconPath = $"graphics/icons/{prototype}.png";
            if (FindCaseInsensitive(root, iconPath) is { } existing) iconPath = existing;
            else if (Recolored(gameDataDirectory, $"graphics/icons/{icon.Vanilla}.png", block.Tint) is { } png)
            {
                files[iconPath] = png;
                create.Add(iconPath);
            }
            else continue;
            iconEntries.Add($" [\"{prototype}\"] = \"__{modName}__/{iconPath}\" ");
        }

        var spriteEntries = new List<string>();
        foreach (var sprite in block.Sprites)
        {
            var spritePath = $"graphics/entity/{name}{sprite.Suffix}.png";
            if (FindCaseInsensitive(root, spritePath) is { } existing) spritePath = existing;
            else if (Recolored(gameDataDirectory, sprite.Vanilla, block.Tint) is { } png)
            {
                files[spritePath] = png;
                create.Add(spritePath);
            }
            else continue;
            spriteEntries.Add($" [\"__base__/{sprite.Vanilla}\"] = \"__{modName}__/{spritePath}\" ");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = name, ["title"] = title, ["icons"] = string.Join(",", iconEntries), ["sprites"] = string.Join(",", spriteEntries)
        };
        files[prototypePath] = Utf8(Render(block.Lua, values));
        create.Insert(0, prototypePath);

        if (FindCaseInsensitive(root, HelperPath) is null)
        {
            files[HelperPath] = Utf8(ContentBlocks.Helper);
            create.Insert(1, HelperPath);
        }

        // data.lua: one require per block, appended.
        var requireLine = $"require(\"prototypes.{name}\")";
        var dataPath = Path.Combine(root, "data.lua");
        if (File.Exists(dataPath))
        {
            var text = ReadText(dataPath);
            if (!text.Split('\n').Any(l => l.Trim() == requireLine))
            {
                files["data.lua"] = Utf8(AppendLine(text, requireLine));
                change.Add("data.lua");
            }
        }
        else
        {
            files["data.lua"] = Utf8($"-- Data stage: the prototypes this mod adds.\n{requireLine}\n");
            create.Add("data.lua");
        }

        // The English locale file: the one named after the mod, else the first one, else a new one.
        var localeDir = Path.Combine(root, "locale", "en");
        var localeFile = Directory.Exists(localeDir)
            ? (File.Exists(Path.Combine(localeDir, modName + ".cfg")) ? modName + ".cfg"
               : Directory.EnumerateFiles(localeDir, "*.cfg").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault())
            : null;
        var localePath = $"locale/en/{localeFile ?? modName + ".cfg"}";
        var entries = block.Locale.Select(e => (e.Section, Key: Render(e.Key, values), Value: Render(e.Value, values))).ToList();
        if (localeFile is not null)
        {
            var text = ReadText(Path.Combine(localeDir, localeFile));
            var merged = MergeLocale(text, entries);
            if (merged != text)
            {
                files[localePath] = Utf8(merged);
                change.Add(localePath);
            }
        }
        else
        {
            files[localePath] = Utf8(MergeLocale("", entries));
            create.Add(localePath);
        }

        var prototypes = entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).ToList();
        return new Prepared(new ContentPlan(block.Info.Id, name, title, create, change, prototypes), files);
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>The same recolouring for <see cref="BlockOperations.PlanUpgrade"/>: an old block gets the icon files a new one has.</summary>
    internal static byte[]? RecoloredIcon(string? gameDataDirectory, string relative, (double R, double G, double B) tint) =>
        Recolored(gameDataDirectory, relative, tint);

    /// <summary>data/base/&lt;relative&gt; recoloured to <paramref name="tint"/>, or null when it cannot be read.</summary>
    private static byte[]? Recolored(string? gameDataDirectory, string relative, (double R, double G, double B) tint)
    {
        if (string.IsNullOrEmpty(gameDataDirectory)) return null;
        var source = Path.Combine(new[] { gameDataDirectory, "base" }.Concat(relative.Split('/')).ToArray());
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists || info.Length > 4 * 1024 * 1024) return null;
            return IconRecolor.Recolor(File.ReadAllBytes(source), tint.R, tint.G, tint.B);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsSupported(string version, IReadOnlyList<string> supported)
    {
        var m = Regex.Match(version, @"^(\d{1,4})\.(\d{1,4})");
        if (!m.Success) return false;
        var normalised = $"{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)}.{int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)}";
        return supported.Contains(normalised, StringComparer.Ordinal);
    }

    private static string? CleanTitle(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > MaxTitleLength) throw new ContentException(ContentError.InvalidTitle, $"The title is longer than {MaxTitleLength} characters.");
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (c < ' ' || c == '\u007f' || char.IsSurrogate(c))
                throw new ContentException(ContentError.InvalidTitle, "The title must be one line of plain text.");
        }
        return text;
    }

    /// <summary>The relative path as it exists on disk when a file with that name exists, ignoring case; otherwise null.</summary>
    private static string? FindCaseInsensitive(string root, string relative)
    {
        var dir = root;
        var parts = relative.Split('/');
        var found = new List<string>();
        foreach (var part in parts)
        {
            if (!Directory.Exists(dir)) return null;
            var match = Directory.EnumerateFileSystemEntries(dir)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), part, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            found.Add(Path.GetFileName(match));
            dir = match;
        }
        return string.Join('/', found);
    }

    private static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Token.Replace(template, m => values[m.Groups[1].Value]).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ReadText(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false));
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>Appends a line, keeping the file's line endings.</summary>
    private static string AppendLine(string text, string line)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (text.Length > 0 && !text.EndsWith('\n')) text += newline;
        return text + line + newline;
    }

    /// <summary>Adds each key to its section (a new section goes to the end); keys that are already there keep their value.</summary>
    internal static string MergeLocale(string text, IEnumerable<(string Section, string Key, string Value)> entries)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        foreach (var (section, key, value) in entries)
        {
            var header = lines.FindIndex(l => l.Trim() == $"[{section}]");
            if (header < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add($"[{section}]");
                lines.Add($"{key}={value}");
                continue;
            }

            var end = header + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            if (lines.Skip(header + 1).Take(end - header - 1).Any(l => l.Split('=')[0].Trim() == key)) continue;

            var insertAt = end;
            while (insertAt > header + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
            lines.Insert(insertAt, $"{key}={value}");
        }

        return string.Join(newline, lines) + newline;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Writes every file through a temporary copy; on failure the files written so far are restored or removed.</summary>
    private static void Write(string root, IReadOnlyDictionary<string, byte[]> files)
    {
        var done = new List<(string Path, byte[]? Original)>();
        var createdDirs = new List<string>();
        string? temp = null;
        try
        {
            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    createdDirs.Add(dir);
                }
                var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
                temp = path + ".fms-tmp";
                File.WriteAllBytes(temp, content);
                // A new file must not replace one that appeared meanwhile: overwrite only what was read above.
                File.Move(temp, path, overwrite: original is not null);
                temp = null;
                done.Add((path, original));
            }
        }
        catch
        {
            try { if (temp is not null) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            foreach (var (path, original) in Enumerable.Reverse(done))
            {
                try
                {
                    if (original is null) File.Delete(path);
                    else File.WriteAllBytes(path, original);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            }
            foreach (var dir in Enumerable.Reverse(createdDirs))
            {
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            }
            throw;
        }
    }
}
