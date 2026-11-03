namespace FactorioModStudio.Core.Lua;

internal sealed record RawPrototype(string Type, string Name, int Line, LuaTable Table);

internal sealed record RawTouch(string Type, string Name, int Line);

internal sealed class ScanResult
{
    public List<RawPrototype> Prototypes { get; } = new();

    /// <summary>References like <c>data.raw.recipe["iron-plate"]</c>: existing prototypes the file reads or modifies.</summary>
    public List<RawTouch> Touched { get; } = new();

    /// <summary>Entries passed to <c>data:extend</c> that could not be resolved statically (variables, function calls).</summary>
    public int DynamicEntries { get; set; }
}

/// <summary>
/// Finds prototype definitions in a Lua source file without running it.
/// Recognized: tables passed to <c>data:extend</c>, tables assigned to a variable or returned from a module.
/// Anything computed at load time (loops, helper functions) is invisible; those are counted in <see cref="ScanResult.DynamicEntries"/>.
/// </summary>
internal static class PrototypeScanner
{
    // Fields that appear on recipe ingredient/result entries but never on a top-level prototype.
    private static readonly string[] EntryOnlyFields = { "amount", "amount_min", "amount_max", "probability" };

    public static ScanResult Scan(string source) => Scan(LuaLexer.Tokenize(source));

    public static ScanResult Scan(List<Token> tokens)
    {
        var result = new ScanResult();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // data.raw.<type>.<name>  /  data.raw["type"]["name"]
            if (token is { Kind: TokenKind.Name, Text: "data" } && IsSymbol(tokens, i + 1, ".") && IsName(tokens, i + 2, "raw"))
            {
                var next = ReadRawChain(tokens, i + 3, out var type, out var name);
                if (type is not null && name is not null) result.Touched.Add(new RawTouch(type, name, token.Line));
                i = next - 1;
                continue;
            }

            // data:extend({ ... })  /  data:extend{ ... }
            if (token is { Kind: TokenKind.Name, Text: "extend" } && i > 0 && (IsSymbol(tokens, i - 1, ":") || IsSymbol(tokens, i - 1, ".")))
            {
                var j = i + 1;
                if (IsSymbol(tokens, j, "(")) j++;

                if (IsSymbol(tokens, j, "{"))
                {
                    var parser = new LuaParser(tokens, j);
                    if (parser.ParseExpression() is LuaTable list) AddFromList(list, result, countDynamic: true);
                    i = Math.Max(i, parser.Position - 1);
                }
                else if (j < tokens.Count && tokens[j].Kind == TokenKind.Name)
                {
                    result.DynamicEntries++;
                }
                continue;
            }

            // local x = { type = "...", name = "..." }   /   return { ... }
            var startsLiteral = (token is { Kind: TokenKind.Symbol, Text: "=" } || token is { Kind: TokenKind.Name, Text: "return" })
                                && IsSymbol(tokens, i + 1, "{");
            if (startsLiteral)
            {
                var parser = new LuaParser(tokens, i + 1);
                if (parser.ParseExpression() is LuaTable table)
                {
                    if (TryPrototype(table, out var proto)) result.Prototypes.Add(proto);
                    else if (table.Fields.Count == 0 && table.Items.OfType<LuaTable>().Any(t => TryPrototype(t, out _)))
                        AddFromList(table, result, countDynamic: false);
                }
                i = Math.Max(i, parser.Position - 1);
            }
        }

        return result;
    }

    private static void AddFromList(LuaTable list, ScanResult result, bool countDynamic)
    {
        foreach (var item in list.Items)
        {
            if (item is LuaTable table)
            {
                if (TryPrototype(table, out var proto)) result.Prototypes.Add(proto);
                else if (table.Fields.Count == 0 && table.Items.Count > 0) AddFromList(table, result, countDynamic); // nested list
            }
            else if (countDynamic)
            {
                result.DynamicEntries++;
            }
        }
    }

    internal static bool TryPrototype(LuaTable table, out RawPrototype prototype)
    {
        prototype = null!;
        var type = table.String("type");
        var name = table.String("name");
        if (type is null || name is null) return false;
        if (EntryOnlyFields.Any(table.Fields.ContainsKey)) return false;

        prototype = new RawPrototype(type, name, table.Line, table);
        return true;
    }

    private static int ReadRawChain(List<Token> tokens, int start, out string? type, out string? name)
    {
        var p = start;
        type = ReadAccessor(tokens, ref p);
        name = type is null ? null : ReadAccessor(tokens, ref p);
        return p;
    }

    private static string? ReadAccessor(List<Token> tokens, ref int p)
    {
        if (IsSymbol(tokens, p, ".") && p + 1 < tokens.Count && tokens[p + 1].Kind == TokenKind.Name)
        {
            var text = tokens[p + 1].Text;
            p += 2;
            return text;
        }

        if (IsSymbol(tokens, p, "[") && p + 2 < tokens.Count && tokens[p + 1].Kind == TokenKind.String && IsSymbol(tokens, p + 2, "]"))
        {
            var text = tokens[p + 1].Text;
            p += 3;
            return text;
        }

        return null;
    }

    private static bool IsSymbol(List<Token> tokens, int index, string text) =>
        index >= 0 && index < tokens.Count && tokens[index].Kind == TokenKind.Symbol && tokens[index].Text == text;

    private static bool IsName(List<Token> tokens, int index, string text) =>
        index >= 0 && index < tokens.Count && tokens[index].Kind == TokenKind.Name && tokens[index].Text == text;
}
