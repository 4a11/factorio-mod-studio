using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FactorioModStudio.Core.Lua;

namespace FactorioModStudio.Core;

/// <param name="Key">Identifies the property within one version of the file: line, target and kind.</param>
/// <param name="Kind">"value" (<c>chest.inventory_size = 48</c>), "multiplier" (<c>x.speed = x.speed * 1.5</c>) or
/// "damage" (<c>copy.scale_damage(x, 2)</c>: every damage of the prototype times the factor).</param>
/// <param name="Path">The field inside the prototype, e.g. <c>attack_parameters.range</c>; "damage" for a damage factor.</param>
/// <param name="ValueType">"number" or "string".</param>
/// <param name="Value">The literal as written (a string without its quotes).</param>
/// <param name="PrototypeType">The prototype the variable holds, when the scanner knows it (also its name and original).</param>
/// <param name="AlsoFor">A <c>local speed = 0.125</c> used by several prototypes: the other ones.</param>
/// <param name="Comment">The comment at the end of the line, a hint the block itself gives.</param>
public sealed record BlockProperty(string Key, string Kind, string Variable, string Path, string ValueType, string Value, int Line,
    string? PrototypeType, string? PrototypeName, string? CopyOf, IReadOnlyList<string> AlsoFor, string? Comment)
{
    /// <summary>The value of the original prototype (for a copy), from a game data report; null when not known.</summary>
    public JsonNode? Original { get; init; }

    /// <summary>The value the game has with the mod, from a game data report; null when not known.</summary>
    public JsonNode? Game { get; init; }
}

/// <param name="Title">From the block's first line: "-- Shiny armor (my-armor), added by Factorio Mod Studio."</param>
/// <param name="Version">A hash of the file's text: a write must name the version it was read from.</param>
public sealed record BlockFile(string File, string? Title, string Version, IReadOnlyList<BlockProperty> Properties);

/// <summary>
/// The editable numbers of a building block (<see cref="ContentBlocks"/>): read from the lines of its Lua file, written back by
/// replacing just the literal, so comments, layout and everything else the author changed stay as they are. Only one statement per
/// line is understood, the way blocks are written; anything else in the file is left alone.
/// </summary>
public static class BlockProperties
{
    private const string Number = @"-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";
    private const string Literal = $@"(?<lit>{Number}|""(?<str>[^""\\]*)"")";
    private const string Tail = @"\s*(?:--\s*(?<comment>.*?))?\s*$";

    // chest.inventory_size = 48 -- the iron chest holds 32 stacks
    private static readonly Regex Assignment = new($@"^\s*(?<var>[A-Za-z_]\w*)\.(?<path>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*=\s*{Literal}{Tail}", RegexOptions.Compiled);

    // inserter.rotation_speed = inserter.rotation_speed * 1.5
    private static readonly Regex Multiplier = new($@"^\s*(?<var>[A-Za-z_]\w*)\.(?<path>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*=\s*\k<var>\.\k<path>\s*\*\s*(?<lit>{Number}){Tail}", RegexOptions.Compiled);

    // local speed = 0.125 -- tiles per tick   (numbers only: string locals are names)
    private static readonly Regex LocalNumber = new($@"^\s*local\s+(?<name>[A-Za-z_]\w*)\s*=\s*(?<lit>{Number}){Tail}", RegexOptions.Compiled);

    // belt.speed = speed
    private static readonly Regex FromLocal = new(@"^\s*(?<var>[A-Za-z_]\w*)\.(?<path>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*=\s*(?<name>[A-Za-z_]\w*)\s*(?:--.*)?$", RegexOptions.Compiled);

    // copy.scale_damage(projectile, 2)   /   copy.scale_damage(biter.attack_parameters, 1.5)
    private static readonly Regex Damage = new($@"^\s*[A-Za-z_]\w*\.scale_damage\(\s*(?<var>[A-Za-z_]\w*)(?:\.[A-Za-z_][\w.]*)?\s*,\s*(?<lit>{Number})\s*\){Tail}", RegexOptions.Compiled);

    private static readonly Regex Header = new(@"^--\s*(?<title>.*?)\s*\((?<id>[^()]*)\),\s*added by Factorio Mod Studio", RegexOptions.Compiled);
    private static readonly Regex HelperRequire = new(@"require\(\s*[""'][^""']*fms-copy[^""']*[""']\s*\)", RegexOptions.Compiled);

    /// <summary>True for a Lua file written by <see cref="ModContent"/>: its header line and the require of the helpers.</summary>
    public static bool IsBlock(string source)
    {
        var head = source.Length > 2000 ? source[..2000] : source;
        return head.Contains("added by Factorio Mod Studio", StringComparison.Ordinal) && HelperRequire.IsMatch(head);
    }

    public static string VersionOf(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16].ToLowerInvariant();

    /// <summary>The block's title from its first line, or null.</summary>
    public static string? TitleOf(string source)
    {
        var first = source.Split('\n', 2)[0].TrimEnd('\r');
        var m = Header.Match(first);
        return m.Success ? m.Groups["title"].Value : null;
    }

    public static BlockFile Read(string file, string source)
    {
        var scan = PrototypeScanner.Scan(source);
        var lines = SplitLines(source);
        var properties = new List<BlockProperty>();
        var locals = new Dictionary<string, (int Line, Match Match)>(StringComparer.Ordinal);
        var localUsers = new Dictionary<string, List<(string Var, string Path)>>(StringComparer.Ordinal);

        RawPrototype? PrototypeOf(string variable) => scan.LocalPrototypes.GetValueOrDefault(variable);

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            var lineNo = i + 1;
            Match m;
            if ((m = Multiplier.Match(text)).Success)
                properties.Add(Make(lineNo, m, "multiplier", m.Groups["var"].Value, m.Groups["path"].Value));
            else if ((m = Assignment.Match(text)).Success)
                properties.Add(Make(lineNo, m, "value", m.Groups["var"].Value, m.Groups["path"].Value));
            else if ((m = Damage.Match(text)).Success)
                properties.Add(Make(lineNo, m, "damage", m.Groups["var"].Value, "damage"));
            else if ((m = LocalNumber.Match(text)).Success)
                locals[m.Groups["name"].Value] = (lineNo, m);
            else if ((m = FromLocal.Match(text)).Success && locals.ContainsKey(m.Groups["name"].Value) && PrototypeOf(m.Groups["var"].Value) is not null)
            {
                if (!localUsers.TryGetValue(m.Groups["name"].Value, out var users)) localUsers[m.Groups["name"].Value] = users = new();
                users.Add((m.Groups["var"].Value, m.Groups["path"].Value));
            }
        }

        // A number kept in a local is a property of the first prototype that uses it, and names the others.
        foreach (var (name, users) in localUsers)
        {
            var (lineNo, m) = locals[name];
            var (variable, path) = users[0];
            var others = users.Skip(1).Select(u => PrototypeOf(u.Var)!.Name).Distinct(StringComparer.Ordinal).ToList();
            properties.Add(Make(lineNo, m, "value", variable, path) with { AlsoFor = others });
        }

        // Only properties of prototypes the file makes; a later assignment of the same field replaces an earlier one.
        var result = properties
            .Where(p => p.PrototypeName is not null)
            .GroupBy(p => (p.Variable, p.Path, p.Kind))
            .Select(g => g.OrderBy(p => p.Line).Last())
            .OrderBy(p => p.Line)
            .ToList();
        return new BlockFile(file, TitleOf(source), VersionOf(source), result);

        BlockProperty Make(int lineNo, Match m, string kind, string variable, string path)
        {
            var proto = PrototypeOf(variable);
            var str = m.Groups["str"];
            var comment = m.Groups["comment"].Success && m.Groups["comment"].Value.Length > 0 ? m.Groups["comment"].Value : null;
            return new BlockProperty($"{lineNo}:{variable}.{path}:{kind}", kind, variable, path,
                str.Success ? "string" : "number", str.Success ? str.Value : m.Groups["lit"].Value, lineNo,
                proto?.Type, proto?.Name, proto?.CopyOf, Array.Empty<string>(), comment);
        }
    }

    /// <summary>Adds the original and the in-game value of each property from a game data report of the mod.</summary>
    public static BlockFile WithGameData(BlockFile block, GameDataReport? report)
    {
        if (report is null) return block;
        var items = report.Items.Where(i => i.Change == "added").ToDictionary(i => $"{i.Type}/{i.Name}", StringComparer.Ordinal);
        var properties = block.Properties.Select(p =>
        {
            if (p.Kind == "damage" || p.PrototypeType is null || !items.TryGetValue($"{p.PrototypeType}/{p.PrototypeName}", out var item)) return p;
            var game = Lookup(item.Value, p.Path);
            JsonNode? original = null;
            if (item.CopyOf is not null)
                original = item.Fields.FirstOrDefault(f => f.Path == p.Path) is { } changed ? changed.Before : game; // not listed: as the original
            return p with { Original = original?.DeepClone(), Game = game?.DeepClone() };
        }).ToList();
        return block with { Properties = properties };
    }

    private static JsonNode? Lookup(JsonNode? value, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(part, out value)) return null;
        }
        return value;
    }

    /// <summary>
    /// Writes new values into the block's text. <paramref name="values"/> maps <see cref="BlockProperty.Key"/> to the new value
    /// (a number, or the text of a string without quotes). Throws <see cref="FmsException"/> for an unknown key or a bad value.
    /// </summary>
    public static string Apply(string source, IReadOnlyDictionary<string, string> values)
    {
        var block = Read("", source);
        var byKey = block.Properties.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var lines = SplitLines(source);
        var edits = new List<(int Start, int Length, string Text)>();

        foreach (var (key, raw) in values)
        {
            if (!byKey.TryGetValue(key, out var property)) throw new FmsException($"Unknown property \"{key}\" (the file changed?).");
            var text = property.ValueType == "number" ? NormaliseNumber(raw, property.Path) : QuoteString(raw, property.Path);

            var line = lines[property.Line - 1];
            var m = (property.Kind, property.Variable) switch
            {
                ("multiplier", _) => Multiplier.Match(line.Text),
                ("damage", _) => Damage.Match(line.Text),
                _ when LocalNumber.Match(line.Text) is { Success: true } local => local,
                _ => Assignment.Match(line.Text)
            };
            if (!m.Success) throw new FmsException($"Line {property.Line} no longer holds \"{property.Path}\".");
            var lit = m.Groups["lit"];
            edits.Add((line.Start + lit.Index, lit.Length, text));
        }

        var builder = new StringBuilder(source);
        foreach (var (start, length, text) in edits.OrderByDescending(e => e.Start))
            builder.Remove(start, length).Insert(start, text);
        return builder.ToString();
    }

    private static string NormaliseNumber(string raw, string path)
    {
        var text = raw.Trim();
        if (!Regex.IsMatch(text, $"^{Number}$") || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value))
            throw new FmsException($"\"{raw}\" is not a number (for {path}).");
        return text;
    }

    private static string QuoteString(string raw, string path)
    {
        if (raw.IndexOfAny(['\n', '\r', '"', '\\']) >= 0) throw new FmsException($"The text for {path} cannot contain quotes, backslashes or line breaks.");
        return $"\"{raw}\"";
    }

    private readonly record struct SourceLine(int Start, string Text);

    private static List<SourceLine> SplitLines(string source)
    {
        var lines = new List<SourceLine>();
        var start = 0;
        for (var i = 0; i <= source.Length; i++)
        {
            if (i < source.Length && source[i] != '\n') continue;
            var end = i > start && source[i - 1] == '\r' ? i - 1 : i;
            lines.Add(new SourceLine(start, source[start..end]));
            start = i + 1;
        }
        return lines;
    }
}
