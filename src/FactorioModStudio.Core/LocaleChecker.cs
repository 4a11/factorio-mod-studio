using System.Text;
using System.Text.RegularExpressions;
using FactorioModStudio.Core.Lua;

namespace FactorioModStudio.Core;

/// <summary>Key counts of one locale language relative to the default language ("en").</summary>
public sealed record LocaleCoverage(string Language, int Keys, int Missing, int Extra);

internal sealed record LocaleEntry(string Section, string Key, string Value, string File, int Line);

internal sealed class LocaleLanguage
{
    public LocaleLanguage(string name) => Name = name;

    public string Name { get; }
    public List<string> Files { get; } = new();

    /// <summary>All well-formed entries in file order (files sorted ordinally), duplicates included.</summary>
    public List<LocaleEntry> Entries { get; } = new();

    /// <summary>First definition of each distinct section+key.</summary>
    public Dictionary<string, LocaleEntry> First { get; } = new(StringComparer.Ordinal);

    public static string Id(string section, string key) => section + "\0" + key;
}

/// <summary>
/// Localisation checks (FMS040-FMS048). Everything here is static and heuristic; the rules about how Factorio resolves
/// locale keys are written from documentation and experience and have not been verified against the game itself.
/// </summary>
internal static class LocaleChecker
{
    public const string DefaultLanguage = "en";

    private const long MaxCfgBytes = 16 * 1024 * 1024;
    private const long MaxLuaBytes = 3 * 1024 * 1024;
    private const int MaxUnusedKeyIssues = 30;

    // Item-like prototype types: their name is looked up in "item-name".
    private static readonly HashSet<string> ItemTypes = new(StringComparer.Ordinal)
    {
        "item", "ammo", "capsule", "gun", "module", "tool", "armor", "repair-tool", "rail-planner", "item-with-entity-data",
        "item-with-label", "item-with-inventory", "blueprint", "blueprint-book", "deconstruction-item", "upgrade-item",
        "copy-paste-tool", "selection-tool", "spidertron-remote"
    };

    // Entity types (Factorio 1.1 and 2.0). Unknown types are skipped on purpose. Purely internal/hidden types
    // (projectile, beam, smoke, sticker, ...) are left out because players never see their names.
    private static readonly HashSet<string> EntityTypes = new(StringComparer.Ordinal)
    {
        "accumulator", "agricultural-tower", "ammo-turret", "arithmetic-combinator", "artillery-turret", "artillery-wagon",
        "assembling-machine", "asteroid-collector", "beacon", "boiler", "burner-generator", "car", "cargo-bay",
        "cargo-landing-pad", "cargo-wagon", "cliff", "constant-combinator", "container", "curved-rail", "curved-rail-a",
        "curved-rail-b", "decider-combinator", "display-panel", "electric-energy-interface", "electric-pole",
        "electric-turret", "elevated-curved-rail-a", "elevated-curved-rail-b", "elevated-half-diagonal-rail",
        "elevated-straight-rail", "fluid-turret", "fluid-wagon", "furnace", "fusion-generator", "fusion-reactor", "gate",
        "generator", "half-diagonal-rail", "heat-interface", "heat-pipe", "infinity-container", "infinity-pipe", "inserter",
        "lab", "lamp", "land-mine", "lane-splitter", "legacy-curved-rail", "legacy-straight-rail", "lightning-attractor",
        "linked-belt", "linked-container", "loader", "loader-1x1", "locomotive", "logistic-container", "market",
        "mining-drill", "offshore-pump", "pipe", "pipe-to-ground", "player-port", "power-switch", "programmable-speaker",
        "pump", "radar", "rail-chain-signal", "rail-ramp", "rail-signal", "rail-support", "reactor", "resource", "roboport",
        "rocket-silo", "selector-combinator", "simple-entity", "simple-entity-with-force", "simple-entity-with-owner",
        "solar-panel", "space-platform-hub", "spider-vehicle", "splitter", "storage-tank", "straight-rail", "thruster",
        "train-stop", "transport-belt", "tree", "turret", "underground-belt", "unit", "unit-spawner", "wall",
        "logistic-robot", "construction-robot", "combat-robot", "capture-robot", "character", "fish"
    };

    // Sections Factorio looks up automatically (plus a few the mod code commonly reads with a fixed prefix).
    // Keys in these sections are never reported as unused by FMS047.
    private static readonly HashSet<string> StandardSections = new(StringComparer.Ordinal)
    {
        "item-name", "item-description", "entity-name", "entity-description", "fluid-name", "fluid-description",
        "recipe-name", "recipe-description", "technology-name", "technology-description", "equipment-name",
        "equipment-description", "tile-name", "tile-description", "virtual-signal-name", "virtual-signal-description",
        "item-group-name", "item-group-description", "item-subgroup-name", "achievement-name", "achievement-description",
        "mod-name", "mod-description", "controls", "string-mod-setting", "mod-setting-name", "mod-setting-description",
        "shortcut-name", "shortcut-description", "ammo-category-name", "damage-type-name", "fuel-category-name",
        "resource-category-name", "recipe-category-name", "equipment-category-name", "equipment-grid-name",
        "module-category-name", "modifier-description", "tips-and-tricks-name", "tips-and-tricks-description",
        "tips-and-tricks-category-name", "decorative-name", "autoplace-control-names", "map-gen-preset-name",
        "map-gen-preset-description", "space-location-name", "space-location-description", "space-connection-name",
        "surface-property-name", "surface-property-description", "quality-name", "quality-description", "planet-name",
        "asteroid-chunk-name", "named-noise-expression-name", "noise-layer-name", "burner-usage"
    };

    // Sections of the game's own core locale that mods read keys from ({"description.fuel-value"}, {"gui.confirm"}); a mod
    // that adds a few keys of its own to one of them still uses the game's other keys, so FMS043 cannot judge them.
    private static readonly HashSet<string> GameSections = new(StringComparer.Ordinal)
    {
        "description", "entity-status", "gui", "gui-control-behavior", "gui-control-behavior-modes", "tooltip-category", "item-limitation",
        "gui-alert-tooltip", "gui-train", "gui-technology-preview", "gui-mod-info", "gui-map-generator", "gui-logistic"
    };

    private static readonly Regex NumberedPlaceholder = new(@"__([1-9])__", RegexOptions.Compiled);

    // __ENTITY__name__, __CONTROL__name__, __ALT_CONTROL__1__name__ ...: removed before comparing numbered placeholders.
    private static readonly Regex NamedPlaceholder = new(@"__[A-Z][A-Z_]*__\S*?__", RegexOptions.Compiled);

    private static readonly Regex KeyLikeText = new(@"[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-.]*[A-Za-z0-9_\-]", RegexOptions.Compiled);

    // ------------------------------------------------------------------ reading

    /// <summary>Reads every language directory of <c>locale/</c>. Never throws on bad content.</summary>
    internal static List<LocaleLanguage> ReadAll(string modDirectory, Action<string, int, string>? malformed = null)
    {
        var languages = new List<LocaleLanguage>();
        var localeDir = Path.Combine(modDirectory, "locale");
        if (!Directory.Exists(localeDir)) return languages;

        List<string> names;
        try { names = LocaleData.Languages(modDirectory).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return languages; }

        foreach (var name in names)
        {
            var language = new LocaleLanguage(name);
            languages.Add(language);

            var langDir = Path.Combine(localeDir, name);
            var relative = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(langDir, "*", SearchOption.AllDirectories))
                {
                    if (!Path.GetExtension(file).Equals(".cfg", StringComparison.OrdinalIgnoreCase)) continue;
                    relative.Add("locale/" + name + "/" + Path.GetRelativePath(langDir, file).Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial list */ }

            relative.Sort(StringComparer.Ordinal);
            foreach (var rel in relative)
            {
                language.Files.Add(rel);
                string text;
                try
                {
                    var full = Path.Combine(modDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (new FileInfo(full).Length > MaxCfgBytes) continue;
                    text = Encoding.UTF8.GetString(File.ReadAllBytes(full));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                ParseCfg(text, rel, language.Entries, malformed);
            }

            foreach (var entry in language.Entries)
                language.First.TryAdd(LocaleLanguage.Id(entry.Section, entry.Key), entry);
        }

        return languages;
    }

    /// <summary>Parses one .cfg text. Malformed lines are reported through <paramref name="malformed"/> (file, line, reason) and skipped.</summary>
    internal static void ParseCfg(string text, string file, List<LocaleEntry> entries, Action<string, int, string>? malformed)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        string? section = null;
        var lineNo = 0;
        var pos = 0;
        while (pos <= text.Length)
        {
            var end = text.IndexOf('\n', pos);
            if (end < 0) end = text.Length;
            lineNo++;

            var line = text.AsSpan(pos, end - pos).Trim();
            pos = end + 1;
            if (line.Length == 0 || line[0] is ';' or '#') continue;

            if (line[0] == '[')
            {
                if (line[^1] != ']' || line.Length < 2)
                {
                    malformed?.Invoke(file, lineNo, "Unterminated section header (missing ']').");
                    continue;
                }

                var name = line[1..^1].Trim();
                if (name.Length == 0)
                {
                    malformed?.Invoke(file, lineNo, "Empty section header.");
                    continue;
                }

                section = name.ToString();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                malformed?.Invoke(file, lineNo, "Line is not a comment, section header or key=value pair (missing '=').");
                continue;
            }

            var key = line[..eq].Trim();
            if (key.Length == 0)
            {
                malformed?.Invoke(file, lineNo, "Empty key before '='.");
                continue;
            }

            // Valid Factorio: keys before any [section] have no section and are used as {"key"} (flib's dictionary.cfg,
            // locale-identifier=...). The studio does not check or edit them; the editor keeps such lines untouched too.
            if (section is null) continue;

            entries.Add(new LocaleEntry(section, key.ToString(), line[(eq + 1)..].Trim().ToString(), file, lineNo));
        }
    }

    // ------------------------------------------------------------------ overview

    public static IReadOnlyList<LocaleCoverage> Coverage(string modDirectory)
    {
        try
        {
            var languages = ReadAll(modDirectory);
            var def = languages.FirstOrDefault(l => l.Name == DefaultLanguage && l.Files.Count > 0);

            return languages
                .OrderBy(l => l.Name == DefaultLanguage ? 0 : 1)
                .ThenBy(l => l.Name, StringComparer.Ordinal)
                .Select(l =>
                {
                    if (def is null || l == def) return new LocaleCoverage(l.Name, l.First.Count, 0, 0);
                    var missing = def.First.Keys.Count(k => !l.First.ContainsKey(k));
                    var extra = l.First.Keys.Count(k => !def.First.ContainsKey(k));
                    return new LocaleCoverage(l.Name, l.First.Count, missing, extra);
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<LocaleCoverage>();
        }
    }

    // ------------------------------------------------------------------ validation

    /// <param name="runtimeOnly">Scripts only the runtime stage loads (<see cref="ModReferenceReport.RuntimeOnly"/>); their tables are not prototypes.</param>
    public static void Check(ValidationResult result, string modDirectory, IReadOnlySet<string>? runtimeOnly = null)
    {
        var languages = ReadAll(modDirectory, (file, line, reason) =>
            result.Add(Severity.Error, "FMS040", $"Malformed locale line: {reason}", file, line));

        // FMS041: duplicate keys within one language
        foreach (var language in languages)
        {
            var seen = new Dictionary<string, LocaleEntry>(StringComparer.Ordinal);
            foreach (var entry in language.Entries)
            {
                var id = LocaleLanguage.Id(entry.Section, entry.Key);
                if (seen.TryGetValue(id, out var first))
                    result.Add(Severity.Warning, "FMS041",
                        $"Duplicate key \"{entry.Section}.{entry.Key}\" in language \"{language.Name}\"; first defined at {first.File}:{first.Line}.",
                        entry.File, entry.Line);
                else
                    seen[id] = entry;
            }
        }

        var def = languages.FirstOrDefault(l => l.Name == DefaultLanguage && l.Files.Count > 0);
        var others = languages.Where(l => l != def && l.Files.Count > 0).OrderBy(l => l.Name, StringComparer.Ordinal).ToList();

        if (def is null)
        {
            if (others.Count > 0)
                result.Add(Severity.Warning, "FMS048",
                    $"Locale for {string.Join(", ", others.Select(o => o.Name))} exists but there is no locale/{DefaultLanguage} with a .cfg file; " +
                    "Factorio falls back to the English locale, so missing strings will show as keys.",
                    "locale");
            return; // the remaining rules compare against the default language
        }

        // FMS044 / FMS045 / FMS046
        foreach (var other in others)
        {
            var missing = def.First.Where(p => !other.First.ContainsKey(p.Key)).Select(p => p.Value).ToList();
            if (missing.Count > 0)
                result.Add(Severity.Warning, "FMS044",
                    $"Language \"{other.Name}\" is missing {missing.Count} key(s) that \"{def.Name}\" has: {Examples(missing)}.",
                    other.Files[0]);

            var extra = other.First.Where(p => !def.First.ContainsKey(p.Key)).Select(p => p.Value).ToList();
            if (extra.Count > 0)
                result.Add(Severity.Info, "FMS045",
                    $"Language \"{other.Name}\" has {extra.Count} key(s) that \"{def.Name}\" does not have: {Examples(extra)}.",
                    other.Files[0]);

            foreach (var entry in other.Entries)
            {
                if (!def.First.TryGetValue(LocaleLanguage.Id(entry.Section, entry.Key), out var original)) continue;
                var expected = Placeholders(original.Value);
                var actual = Placeholders(entry.Value);
                if (expected != actual)
                    result.Add(Severity.Warning, "FMS046",
                        $"Placeholders of \"{entry.Section}.{entry.Key}\" differ from \"{def.Name}\" (expected {Describe(expected)}, found {Describe(actual)}).",
                        entry.File, entry.Line);
            }
        }

        CheckLua(result, modDirectory, def, runtimeOnly);
    }

    private static string Examples(List<LocaleEntry> entries)
    {
        var text = string.Join(", ", entries.Take(5).Select(e => $"{e.Section}.{e.Key}"));
        return entries.Count > 5 ? text + ", ..." : text;
    }

    private static string Placeholders(string value)
    {
        var stripped = NamedPlaceholder.Replace(value, "");
        var digits = NumberedPlaceholder.Matches(stripped).Select(m => m.Groups[1].Value[0]).Distinct().OrderBy(c => c).ToArray();
        return new string(digits);
    }

    private static string Describe(string placeholders) =>
        placeholders.Length == 0 ? "none" : string.Join(", ", placeholders.Select(c => $"__{c}__"));

    // ------------------------------------------------------------------ Lua-dependent rules

    private sealed record ProtoSite(RawPrototype Proto, string File, bool FileAssignsLocalisedName);

    private static void CheckLua(ValidationResult result, string modDirectory, LocaleLanguage def, IReadOnlySet<string>? runtimeOnly)
    {
        var defSections = new HashSet<string>(def.First.Values.Select(e => e.Section), StringComparer.Ordinal);
        var references = new HashSet<string>(StringComparer.Ordinal);
        var dynamicSections = new HashSet<string>(StringComparer.Ordinal);
        var sites = new List<ProtoSite>();

        foreach (var file in ModFiles.Enumerate(modDirectory))
        {
            if (!file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) continue;

            string source;
            try
            {
                var full = Path.Combine(modDirectory, file.Replace('/', Path.DirectorySeparatorChar));
                if (new FileInfo(full).Length > MaxLuaBytes) continue;
                source = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var tokens = LuaLexer.Tokenize(source);

            var assignsLocalisedName = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];

                if (t.Kind == TokenKind.Name && t.Text == "localised_name" && i > 0 && tokens[i - 1] is { Kind: TokenKind.Symbol, Text: "." }
                    && i + 1 < tokens.Count && tokens[i + 1] is { Kind: TokenKind.Symbol, Text: "=" })
                    assignsLocalisedName = true;

                if (t.Kind != TokenKind.String) continue;
                var text = t.Text;

                // FMS047 bookkeeping: remember every string that looks like "section.key" (exact or embedded)
                if (text.Length <= 300 && text.Contains('.'))
                {
                    references.Add(text);
                    if (text.Length > 1 && text[^1] == '.') dynamicSections.Add(text[..^1]); // "section." .. name
                    foreach (Match m in KeyLikeText.Matches(text)) references.Add(m.Value);
                }

                // FMS043: {"section.key" ...}
                if (i > 0 && tokens[i - 1] is { Kind: TokenKind.Symbol, Text: "{" }
                    && i + 1 < tokens.Count && tokens[i + 1] is { Kind: TokenKind.Symbol, Text: "," or "}" })
                {
                    var dot = text.IndexOf('.');
                    if (dot <= 0 || dot == text.Length - 1 || text[0] == '?') continue;
                    var section = text[..dot];
                    var key = text[(dot + 1)..];
                    if (!defSections.Contains(section) || key.AsSpan().IndexOfAny(" \t\r\n") >= 0) continue;
                    // {"fluid-name.water"}, {"entity-name.stone-furnace"}: keys of the standard sections come from the game or any
                    // dependency as well, which this check cannot see; only a mod's own sections are known in full.
                    if (StandardSections.Contains(section) || GameSections.Contains(section)) continue;
                    if (!def.First.ContainsKey(LocaleLanguage.Id(section, key)))
                        result.Add(Severity.Warning, "FMS043",
                            $"Locale key \"{section}.{key}\" is used here but not defined in the \"{def.Name}\" locale of this mod.",
                            file, t.Line);
                }
            }

            if (IsPrototypeFile(file) && runtimeOnly?.Contains(file) != true)
            {
                var scan = PrototypeScanner.Scan(tokens);
                foreach (var proto in scan.Prototypes) sites.Add(new ProtoSite(proto, file, assignsLocalisedName));
            }
        }

        CheckPrototypeNames(result, def, sites);
        CheckUnusedKeys(result, def, references, dynamicSections);
    }

    // control.lua tables such as { type = "item", name = ... } can be GUI/runtime data, so only data-stage style files are inspected
    // (files that only control.lua requires are left out by the caller through runtimeOnly).
    private static bool IsPrototypeFile(string file) =>
        !file.Equals("control.lua", StringComparison.OrdinalIgnoreCase)
        && !file.StartsWith("migrations/", StringComparison.OrdinalIgnoreCase)
        && !Path.GetFileName(file).StartsWith("settings", StringComparison.OrdinalIgnoreCase);

    private static void CheckPrototypeNames(ValidationResult result, LocaleLanguage def, List<ProtoSite> sites)
    {
        bool Has(string section, string name) => def.First.ContainsKey(LocaleLanguage.Id(section, name));

        // Names of mod prototypes that set localised_name themselves (fallback targets we must not second-guess).
        var withLocalisedName = new HashSet<string>(
            sites.Where(s => s.Proto.Table.Fields.ContainsKey("localised_name")).Select(s => s.Proto.Name), StringComparer.Ordinal);

        var ownTechnologies = new HashSet<string>(sites.Where(s => s.Proto.Type == "technology").Select(s => s.Proto.Name), StringComparer.Ordinal);

        // entity name -> names of mod items that place it (an entity falls back to the name of its placing item)
        var placers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in sites.Where(s => ItemTypes.Contains(s.Proto.Type)))
            if (s.Proto.Table.String("place_result") is { } placed)
            {
                if (!placers.TryGetValue(placed, out var list)) placers[placed] = list = new List<string>();
                list.Add(s.Proto.Name);
            }

        foreach (var site in sites)
        {
            var p = site.Proto;
            var table = p.Table;
            if (site.FileAssignsLocalisedName || table.Fields.ContainsKey("localised_name")) continue;

            string section;
            string[] alsoAccept = Array.Empty<string>();
            var type = p.Type;

            if (ItemTypes.Contains(type))
            {
                section = "item-name";
                // An item may fall back to the thing it places; unresolvable targets make us stay silent.
                var unsure = false;
                foreach (var (field, fallbackSection) in new[] { ("place_result", "entity-name"), ("place_as_equipment_result", "equipment-name"), ("place_as_tile", "tile-name") })
                {
                    if (!table.Fields.TryGetValue(field, out var value)) continue;
                    if (field == "place_as_tile" && value is LuaTable tileTable) value = tileTable.Fields.GetValueOrDefault("result") ?? value;
                    if (value is LuaString s)
                    {
                        if (Has(fallbackSection, s.Value) || withLocalisedName.Contains(s.Value)) unsure = true;
                    }
                    else unsure = true;
                }
                if (unsure) continue;
            }
            else if (type == "fluid") section = "fluid-name";
            else if (type == "technology")
            {
                section = "technology-name";
                // Technology levels ("name-3") are looked up without the numeric suffix.
                var m = Regex.Match(p.Name, @"^(.+)-\d+$");
                if (m.Success && Has(section, m.Groups[1].Value)) continue;
                // A further level of a technology this mod does not define (bob's "automation-4", "toolbelt-2") takes the name of
                // the game's or another mod's technology, which this check cannot see.
                if (m.Success && !ownTechnologies.Contains(m.Groups[1].Value)) continue;
            }
            else if (type == "tile") section = "tile-name";
            else if (type == "virtual-signal") section = "virtual-signal-name";
            else if (type == "item-group") section = "item-group-name";
            else if (type.EndsWith("-equipment", StringComparison.Ordinal)) section = "equipment-name";
            else if (EntityTypes.Contains(type))
            {
                section = "entity-name";
                alsoAccept = new[] { "item-name" };
                if (placers.TryGetValue(p.Name, out var items) &&
                    items.Any(i => Has("item-name", i) || withLocalisedName.Contains(i)))
                    continue;
            }
            else continue; // item-subgroup, recipe, unknown types

            if (Has(section, p.Name) || alsoAccept.Any(a => Has(a, p.Name))) continue;

            var expected = $"\"{section}.{p.Name}\"" + (alsoAccept.Length > 0 ? $" (or \"{alsoAccept[0]}.{p.Name}\")" : "");
            result.Add(Severity.Warning, "FMS042",
                $"Prototype \"{p.Name}\" ({type}) has no localised name in the \"{def.Name}\" locale; expected {expected}.",
                site.File, p.Line);
        }
    }

    private static void CheckUnusedKeys(ValidationResult result, LocaleLanguage def, HashSet<string> references, HashSet<string> dynamicSections)
    {
        var reported = 0;
        var total = 0;
        foreach (var entry in def.First.Values)
        {
            if (StandardSections.Contains(entry.Section) || dynamicSections.Contains(entry.Section)) continue;
            if (references.Contains(entry.Section + "." + entry.Key)) continue;

            total++;
            if (reported >= MaxUnusedKeyIssues) continue;
            reported++;
            result.Add(Severity.Info, "FMS047",
                $"Locale key \"{entry.Section}.{entry.Key}\" is not in an automatically looked-up section and no Lua file of this mod references it.",
                entry.File, entry.Line);
        }

        if (total > reported)
            result.Add(Severity.Info, "FMS047",
                $"{total - reported} more locale key(s) look unused; only the first {MaxUnusedKeyIssues} are listed ({total} in total).");
    }
}
