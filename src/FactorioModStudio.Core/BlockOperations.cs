using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FactorioModStudio.Core.Lua;

namespace FactorioModStudio.Core;

/// <summary>One file a block operation writes (<see cref="Content"/>) or deletes (<see cref="Content"/> null).</summary>
public sealed record BlockFileChange(string Path, byte[]? Content)
{
    public bool Delete => Content is null;
}

/// <param name="Operation">"rename", "remove" or "upgrade".</param>
/// <param name="Renamed">For a rename: old prototype name to new one.</param>
/// <param name="Warnings">What the studio could not change by itself (references it cannot see, a missing game data folder...).</param>
public sealed record BlockPlan(string Operation, string File, IReadOnlyList<BlockFileChange> Changes,
    IReadOnlyDictionary<string, string> Renamed, IReadOnlyList<string> Warnings);

/// <summary>
/// Renames, removes and upgrades a building block (a Lua file written by <see cref="ModContent"/>) together with everything that
/// belongs to it: the file, its require in data.lua, its locale keys in every language, its icon and sprite files and the places
/// in the mod's Lua that name its prototypes. Each operation is a <see cref="BlockPlan"/> first (shown before it is applied);
/// <see cref="Apply"/> writes it.
/// </summary>
public static class BlockOperations
{
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9_-]{1,200}$", RegexOptions.Compiled);
    private static readonly Regex HeaderId = new(@"^(?<pre>--.*?\()(?<id>[^()]*)(?<post>\),\s*added by Factorio Mod Studio)", RegexOptions.Compiled);
    private static readonly Regex HelperRequire = new(@"require\(\s*[""']prototypes\.(?<helper>fms-copy-v(?<v>\d+))[""']\s*\)", RegexOptions.Compiled);
    private static readonly Regex UseIcons = new(@"copy\.use_icons\(\{(?<body>.*)\}\)", RegexOptions.Compiled);
    private static readonly Regex UseSprites = new(@"copy\.use_sprites\(\{(?<body>.*)\}\)", RegexOptions.Compiled);
    private static readonly Regex ModPath = new(@"""__(?<mod>[^_""]+(?:_[^_""]+)*)__/(?<path>[^""]+)""", RegexOptions.Compiled);

    private static readonly HashSet<string> ItemTypes = new(StringComparer.Ordinal)
    {
        "item", "ammo", "capsule", "gun", "module", "tool", "armor", "repair-tool", "rail-planner", "item-with-entity-data",
        "item-with-label", "item-with-inventory", "blueprint", "blueprint-book", "deconstruction-item", "upgrade-item",
        "copy-paste-tool", "selection-tool", "spidertron-remote"
    };

    // ------------------------------------------------------------------ the block and what belongs to it

    private sealed record BlockInfo(string Root, string File, string Source, string Id, IReadOnlyList<string> Names,
        IReadOnlyList<string> Assets, int HelperVersion);

    private static BlockInfo Load(string modDirectory, string file)
    {
        var root = Path.GetFullPath(modDirectory);
        var full = Inside(root, file) ?? throw new FmsException($"\"{file}\" is not a file of this mod.");
        if (!File.Exists(full)) throw new FmsException($"{file} does not exist.");
        var source = ReadText(full);
        if (!BlockProperties.IsBlock(source)) throw new FmsException($"{file} is not a building block.");

        var first = source.Split('\n', 2)[0].TrimEnd('\r');
        var id = HeaderId.Match(first) is { Success: true } m ? m.Groups["id"].Value : Path.GetFileNameWithoutExtension(file);
        // The names the block defines are the id and names made from it (id .. "-ammo"); anything else it mentions is the game's.
        var names = PrototypeScanner.Scan(source).Prototypes.Select(p => p.Name)
            .Where(n => n == id || n.StartsWith(id + "-", StringComparison.Ordinal) || n.StartsWith(id + "_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
        if (!names.Contains(id)) names.Insert(0, id);

        var modName = ModNameOf(root);
        var assets = new List<string>();
        foreach (var regex in new[] { UseIcons, UseSprites })
            if (regex.Match(source) is { Success: true } use)
                foreach (Match path in ModPath.Matches(use.Groups["body"].Value))
                    if (path.Groups["mod"].Value == modName) assets.Add(path.Groups["path"].Value);

        var helper = HelperRequire.Match(source) is { Success: true } h ? int.Parse(h.Groups["v"].Value) : 0;
        return new BlockInfo(root, file, source, id, names, assets.Distinct(StringComparer.Ordinal).ToList(), helper);
    }

    // ------------------------------------------------------------------ rename

    /// <summary>
    /// Renames the block to <paramref name="newName"/>: its prototypes (the id and names made from it), file, require, locale keys,
    /// icon and sprite files, and every Lua string of the mod equal to one of the old names. With <paramref name="migration"/> a
    /// migrations/*.json is added, so saves made with the old names keep their entities and items.
    /// </summary>
    public static BlockPlan PlanRename(string modDirectory, string file, string newName, bool migration = true)
    {
        var block = Load(modDirectory, file);
        newName = (newName ?? "").Trim();
        if (!NamePattern.IsMatch(newName)) throw new FmsException("A block name has 1-200 letters, digits, \"_\" or \"-\".");
        if (newName == block.Id) throw new FmsException("The block already has this name.");

        var renamed = block.Names.ToDictionary(n => n, n => newName + n[block.Id.Length..], StringComparer.Ordinal);
        var newFile = $"prototypes/{newName}.lua";
        if (File.Exists(Path.Combine(block.Root, "prototypes", newName + ".lua"))) throw new FmsException($"{newFile} already exists.");

        // A name the mod already uses elsewhere would clash after the rename.
        var defined = ModInspector.Inspect(block.Root, validate: false).Prototypes
            .Where(p => !string.Equals(p.File, block.File, StringComparison.Ordinal)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (renamed.Values.FirstOrDefault(defined.Contains) is { } clash) throw new FmsException($"The mod already has a prototype called \"{clash}\".");

        var changes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var assetMoves = block.Assets.Select(a => (Old: a, New: RenamedAsset(a, block.Id, newName))).Where(m => m.New != m.Old).ToList();

        // The block's own file, under its new name.
        var text = ReplaceNames(block.Source, renamed);
        text = HeaderId.Replace(text, m => m.Groups["pre"].Value + newName + m.Groups["post"].Value, 1);
        foreach (var (oldAsset, newAsset) in assetMoves) text = text.Replace($"/{oldAsset}\"", $"/{newAsset}\"", StringComparison.Ordinal);
        changes[file] = null;
        changes[newFile] = Utf8(text);

        // The other Lua files: the require of the block and strings naming its prototypes.
        foreach (var lua in LuaFiles(block.Root).Where(f => f != file && !IsHelperFile(f)))
        {
            var original = ReadText(Path.Combine(block.Root, lua));
            var updated = ReplaceNames(original, renamed)
                .Replace($"\"prototypes.{block.Id}\"", $"\"prototypes.{newName}\"", StringComparison.Ordinal)
                .Replace($"'prototypes.{block.Id}'", $"'prototypes.{newName}'", StringComparison.Ordinal);
            if (updated != original) changes[lua] = Utf8(updated);
        }

        // Locale keys in every language.
        foreach (var cfg in LocaleFiles(block.Root))
        {
            var original = ReadText(Path.Combine(block.Root, cfg));
            var updated = RenameLocaleKeys(original, renamed);
            if (updated != original) changes[cfg] = Utf8(updated);
        }

        // Icons and sprites move with their prototypes.
        var warnings = new List<string>();
        foreach (var (oldAsset, newAsset) in assetMoves)
        {
            var from = Path.Combine(block.Root, oldAsset.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(from)) { warnings.Add($"{oldAsset} is missing; the block names it."); continue; }
            changes[oldAsset] = null;
            changes[newAsset] = File.ReadAllBytes(from);
        }

        if (migration)
        {
            var path = $"migrations/{ModNameOf(block.Root)}-rename-{block.Id}-to-{newName}.json";
            changes[path] = Utf8(MigrationJson(PrototypeScanner.Scan(block.Source).Prototypes
                .Where(p => renamed.ContainsKey(p.Name)).Select(p => (p.Type, p.Name, renamed[p.Name]))));
        }

        return new BlockPlan("rename", file, Order(changes), renamed, warnings);
    }

    // ------------------------------------------------------------------ remove

    /// <summary>Removes the block: its file, require, locale keys, icon and sprite files, and the helpers when no block is left.
    /// Other Lua files that still name its prototypes are listed as warnings (they would fail in the game).</summary>
    public static BlockPlan PlanRemove(string modDirectory, string file)
    {
        var block = Load(modDirectory, file);
        var names = block.Names.ToHashSet(StringComparer.Ordinal);
        var changes = new Dictionary<string, byte[]?>(StringComparer.Ordinal) { [file] = null };
        var warnings = new List<string>();

        foreach (var lua in LuaFiles(block.Root).Where(f => f != file && !IsHelperFile(f)))
        {
            var original = ReadText(Path.Combine(block.Root, lua));
            var updated = RemoveRequire(original, block.Id);
            if (updated != original) changes[lua] = Utf8(updated);
            foreach (var name in names)
                if (Regex.IsMatch(updated, $@"([""']){Regex.Escape(name)}\1"))
                    warnings.Add($"{lua} still names \"{name}\".");
        }

        foreach (var cfg in LocaleFiles(block.Root))
        {
            var original = ReadText(Path.Combine(block.Root, cfg));
            var updated = RemoveLocaleKeys(original, names);
            if (updated != original) changes[cfg] = Utf8(updated);
        }

        foreach (var asset in block.Assets)
            if (File.Exists(Path.Combine(block.Root, asset.Replace('/', Path.DirectorySeparatorChar)))) changes[asset] = null;

        // The shared helpers go with the last block that needs them.
        if (block.HelperVersion > 0)
        {
            var helper = $"prototypes/fms-copy-v{block.HelperVersion}.lua";
            var stillUsed = LuaFiles(block.Root).Where(f => f != file && f != helper)
                .Any(f => ReadText(Path.Combine(block.Root, f)).Contains($"prototypes.fms-copy-v{block.HelperVersion}", StringComparison.Ordinal));
            if (!stillUsed && File.Exists(Path.Combine(block.Root, "prototypes", $"fms-copy-v{block.HelperVersion}.lua"))) changes[helper] = null;
        }

        return new BlockPlan("remove", file, Order(changes), new Dictionary<string, string>(), warnings.Distinct().ToList());
    }

    // ------------------------------------------------------------------ upgrade

    /// <summary>True when the block still uses the first helpers (no icon files of its own).</summary>
    public static bool NeedsUpgrade(string source) =>
        HelperRequire.Match(source) is { Success: true } m && int.Parse(m.Groups["v"].Value) < 2;

    /// <summary>
    /// Moves an old block (helpers v1, icons only tinted in the game) to the current helpers: recoloured icon files of its own, made
    /// from the game's pictures in <paramref name="gameDataDirectory"/>, registered with copy.use_icons. The block's own lines stay.
    /// </summary>
    public static BlockPlan PlanUpgrade(string modDirectory, string file, string? gameDataDirectory)
    {
        var block = Load(modDirectory, file);
        if (!NeedsUpgrade(block.Source)) throw new FmsException($"{file} already uses the current helpers.");
        var changes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var warnings = new List<string>();

        var kind = IdentifyBlock(block.Source, block.Id) ?? throw new FmsException($"{file} does not match any building block the studio knows.");
        var modName = ModNameOf(block.Root);
        var tint = TintOf(block.Source) ?? kind.Tint;

        var icons = new List<string>();
        foreach (var icon in kind.Icons)
        {
            var prototype = block.Id + icon.Suffix;
            var iconPath = $"graphics/icons/{prototype}.png";
            if (!File.Exists(Path.Combine(block.Root, "graphics", "icons", prototype + ".png")))
            {
                if (ModContent.RecoloredIcon(gameDataDirectory, $"graphics/icons/{icon.Vanilla}.png", tint) is not { } png)
                {
                    warnings.Add(gameDataDirectory is null
                        ? "No Factorio installation was found: the icons stay tinted in the game until the block is upgraded with one."
                        : $"The game's icon {icon.Vanilla}.png was not found; {prototype} keeps the tinted original.");
                    continue;
                }
                changes[iconPath] = png;
            }
            icons.Add($" [\"{prototype}\"] = \"__{modName}__/{iconPath}\" ");
        }

        var text = HelperRequire.Replace(block.Source, "require(\"prototypes.fms-copy-v2\")", 1);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var requireLine = HelperRequire.Match(text);
        var lineEnd = text.IndexOf('\n', requireLine.Index);
        var insert = $"copy.use_icons({{{string.Join(",", icons)}}}){newline}copy.use_sprites({{}}){newline}";
        text = lineEnd < 0 ? text + newline + insert : text.Insert(lineEnd + 1, insert);
        // v1 tinted the icons of hand-made copies itself; the current helper uses the icon file when there is one.
        text = Regex.Replace(text, @"^(\s*)(?<var>[A-Za-z_]\w*)\.icons\s*=\s*copy\.tinted_icons\(\s*\k<var>\s*,\s*tint\s*\)\s*$",
            m => $"{m.Groups[1].Value}copy.set_icon({m.Groups["var"].Value}, name, tint)", RegexOptions.Multiline);
        // Copies made by hand (table.deepcopy) get their icon where the current block sets it: right after their name.
        foreach (Match setIcon in Regex.Matches(kind.Lua, @"^\s*copy\.set_icon\((?<var>[A-Za-z_]\w*), name, tint\)", RegexOptions.Multiline))
        {
            var variable = setIcon.Groups["var"].Value;
            if (text.Contains($"copy.set_icon({variable},", StringComparison.Ordinal)) continue;
            text = Regex.Replace(text, $@"^(?<lead>[^\S\r\n]*){variable}\.name = name[^\S\r\n]*(?<nl>\r?\n)",
                m => $"{m.Value}{m.Groups["lead"].Value}copy.set_icon({variable}, name, tint){m.Groups["nl"].Value}", RegexOptions.Multiline);
        }
        changes[file] = Utf8(text);

        if (!File.Exists(Path.Combine(block.Root, "prototypes", "fms-copy-v2.lua")))
            changes[ModContent.HelperPath] = Utf8(ContentBlocks.Helper);

        return new BlockPlan("upgrade", file, Order(changes), new Dictionary<string, string>(), warnings.Distinct().ToList());
    }

    // The block a file was made from: the one whose main prototype copies the same original.
    private static ContentBlock? IdentifyBlock(string source, string id)
    {
        var main = PrototypeScanner.Scan(source).Prototypes.FirstOrDefault(p => p.Name == id && p.CopyOf is not null);
        if (main is null) return null;
        foreach (var candidate in ContentBlocks.All)
        {
            var sample = ContentBlocksSample(candidate);
            if (PrototypeScanner.Scan(sample).Prototypes.FirstOrDefault(p => p.Name == "fms-sample") is { } p
                && p.CopyOf == main.CopyOf && p.Type == main.Type)
                return candidate;
        }
        return null;
    }

    private static string ContentBlocksSample(ContentBlock block) =>
        block.Lua.Replace("@@id@@", "fms-sample").Replace("@@title@@", "Sample").Replace("@@icons@@", "").Replace("@@sprites@@", "");

    private static (double R, double G, double B)? TintOf(string source)
    {
        var m = Regex.Match(source, @"local tint = \{ r = ([0-9.]+), g = ([0-9.]+), b = ([0-9.]+)");
        if (!m.Success) return null;
        double Part(int i) => double.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (Part(1), Part(2), Part(3));
    }

    // ------------------------------------------------------------------ applying

    /// <summary>Writes and deletes the files of <paramref name="plan"/>. Returns the original content of every path it touched
    /// (null for a path that did not exist), so the operation can be undone with <see cref="Restore"/>.</summary>
    public static IReadOnlyDictionary<string, byte[]?> Apply(string modDirectory, BlockPlan plan)
    {
        var root = Path.GetFullPath(modDirectory);
        var before = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var change in plan.Changes)
        {
            var full = Inside(root, change.Path) ?? throw new FmsException($"\"{change.Path}\" is outside the mod.");
            before[change.Path] = File.Exists(full) ? File.ReadAllBytes(full) : null;
        }

        try
        {
            foreach (var change in plan.Changes.Where(c => !c.Delete)) Write(Inside(root, change.Path)!, change.Content!);
            foreach (var change in plan.Changes.Where(c => c.Delete))
            {
                var full = Inside(root, change.Path)!;
                if (File.Exists(full)) File.Delete(full);
                RemoveEmptyParents(root, full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Restore(root, before);
            throw;
        }
        return before;
    }

    /// <summary>Puts back what <see cref="Apply"/> returned: each file as it was, and files that did not exist removed again.</summary>
    public static void Restore(string modDirectory, IReadOnlyDictionary<string, byte[]?> before)
    {
        var root = Path.GetFullPath(modDirectory);
        foreach (var (path, content) in before)
        {
            var full = Inside(root, path);
            if (full is null) continue;
            if (content is null)
            {
                if (File.Exists(full)) File.Delete(full);
                RemoveEmptyParents(root, full); // a folder the operation made (migrations/) goes too
            }
            else Write(full, content);
        }
    }

    private static void RemoveEmptyParents(string root, string full)
    {
        var top = Path.TrimEndingDirectorySeparator(root);
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > top.Length; dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
            try { Directory.Delete(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        }
    }

    private static void Write(string full, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".fms-tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, full, overwrite: true);
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<BlockFileChange> Order(Dictionary<string, byte[]?> changes) =>
        changes.OrderBy(c => c.Value is null ? 1 : 0).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new BlockFileChange(c.Key, c.Value)).ToList();

    private static string ReplaceNames(string text, IReadOnlyDictionary<string, string> renamed)
    {
        foreach (var (oldName, newName) in renamed.OrderByDescending(r => r.Key.Length))
            text = Regex.Replace(text, $@"([""']){Regex.Escape(oldName)}\1", m => m.Groups[1].Value + newName + m.Groups[1].Value);
        return text;
    }

    private static string RenamedAsset(string asset, string oldId, string newId)
    {
        var slash = asset.LastIndexOf('/');
        var name = asset[(slash + 1)..];
        return name.StartsWith(oldId, StringComparison.Ordinal) ? asset[..(slash + 1)] + newId + name[oldId.Length..] : asset;
    }

    private static string RenameLocaleKeys(string text, IReadOnlyDictionary<string, string> renamed) =>
        Regex.Replace(text, @"^(?<lead>\s*)(?<key>[^=\[\];#\r\n]+?)(?<eq>\s*=)", m =>
            renamed.TryGetValue(m.Groups["key"].Value, out var to) ? m.Groups["lead"].Value + to + m.Groups["eq"].Value : m.Value,
            RegexOptions.Multiline);

    private static string RemoveLocaleKeys(string text, IReadOnlySet<string> names) =>
        Regex.Replace(text, @"^\s*(?<key>[^=\[\];#\r\n]+?)\s*=.*(\r?\n|$)", m => names.Contains(m.Groups["key"].Value) ? "" : m.Value,
            RegexOptions.Multiline);

    private static string RemoveRequire(string text, string id) =>
        Regex.Replace(text, $@"^\s*require\(\s*[""']prototypes\.{Regex.Escape(id)}[""']\s*\)\s*(\r?\n|$)", "", RegexOptions.Multiline);

    /// <summary>A Factorio migration: old names to new ones, per category of prototype.</summary>
    private static string MigrationJson(IEnumerable<(string Type, string Old, string New)> renames)
    {
        var root = new JsonObject();
        foreach (var (type, oldName, newName) in renames)
        {
            var category = ItemTypes.Contains(type) ? "item"
                : type is "recipe" or "technology" or "fluid" or "tile" or "recipe-category" or "autoplace-control" ? type
                : type is "item-group" or "item-subgroup" ? "item-group"
                : type.EndsWith("-equipment", StringComparison.Ordinal) ? "equipment"
                : "entity";
            if (root[category] is not JsonArray list) root[category] = list = new JsonArray();
            if (!list.OfType<JsonArray>().Any(pair => (string?)pair[0] == oldName)) list.Add(new JsonArray(oldName, newName));
        }
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static IEnumerable<string> LuaFiles(string root) =>
        ModFiles.Enumerate(root).Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> LocaleFiles(string root) =>
        ModFiles.Enumerate(root).Where(f => f.StartsWith("locale/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase));

    private static bool IsHelperFile(string file) =>
        Regex.IsMatch(file, @"^prototypes/fms-copy-v\d+\.lua$", RegexOptions.IgnoreCase);

    private static string ModNameOf(string root)
    {
        try { return ModInfo.Load(Path.Combine(root, "info.json")).Name ?? Path.GetFileName(root); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FmsException) { return Path.GetFileName(root); }
    }

    private static string? Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string ReadText(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false));
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
