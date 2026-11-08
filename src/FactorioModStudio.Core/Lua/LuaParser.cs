namespace FactorioModStudio.Core.Lua;

internal abstract record LuaValue(int Line);

internal sealed record LuaString(string Value, int Line) : LuaValue(Line);

internal sealed record LuaNumber(double Value, int Line) : LuaValue(Line);

internal sealed record LuaBool(bool Value, int Line) : LuaValue(Line);

internal sealed record LuaNil(int Line) : LuaValue(Line);

/// <summary>An expression the parser deliberately does not evaluate (calls, concatenation, variables, functions...).</summary>
internal sealed record LuaUnknown(int Line) : LuaValue(Line);

/// <summary>A reference to an existing prototype, <c>data.raw.&lt;type&gt;["name"]</c>; only read with a <see cref="LuaScope"/>.</summary>
internal sealed record LuaRawRef(string Type, string Name, int Line) : LuaValue(Line);

/// <summary>What <c>require("path")</c> returned; only read with a <see cref="LuaScope"/>.</summary>
internal sealed record LuaModule(string Path, int Line) : LuaValue(Line);

/// <summary>
/// What the parser may know besides literals while reading one file: local variables (strings, tables, modules) and the
/// result of calls it understands. Without a scope the parser reads literals only.
/// </summary>
internal sealed class LuaScope
{
    public Dictionary<string, LuaValue> Locals { get; } = new(StringComparer.Ordinal);

    /// <summary>Evaluates a call such as <c>copy.item("x", name)</c>: callee path, arguments, line; null when unknown.</summary>
    public Func<string, IReadOnlyList<LuaValue>, int, LuaValue?>? Call { get; init; }

    /// <summary>Told about every <c>data.raw.&lt;type&gt;.&lt;name&gt;</c> the parser reads inside an expression.</summary>
    public Action<LuaRawRef>? RawRead { get; init; }
}

internal sealed record LuaTable(int Line) : LuaValue(Line)
{
    /// <summary>Positional entries: <c>{ a, b, c }</c>.</summary>
    public List<LuaValue> Items { get; } = new();

    /// <summary>Named entries: <c>{ name = x, ["other-name"] = y }</c>. Later duplicates win.</summary>
    public Dictionary<string, LuaValue> Fields { get; } = new(StringComparer.Ordinal);

    public string? String(string field) => Fields.TryGetValue(field, out var v) && v is LuaString s ? s.Value : null;
    public double? Number(string field) => Fields.TryGetValue(field, out var v) && v is LuaNumber n ? n.Value : null;
    public LuaTable? Table(string field) => Fields.TryGetValue(field, out var v) ? v as LuaTable : null;
}

/// <summary>
/// Reads Lua table literals from a token list. Anything it cannot interpret becomes <see cref="LuaUnknown"/>
/// and is skipped up to the next delimiter, so one dynamic value never prevents reading the rest of the table.
/// </summary>
internal sealed class LuaParser
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not",
        "or", "repeat", "return", "then", "true", "until", "while"
    };

    private readonly List<Token> _tokens;
    private readonly LuaScope? _scope;
    private int _position;

    public LuaParser(List<Token> tokens, int start = 0, LuaScope? scope = null)
    {
        _tokens = tokens;
        _position = start;
        _scope = scope;
    }

    /// <summary>Index of the next unread token.</summary>
    public int Position => _position;

    private bool AtEnd => _position >= _tokens.Count;
    private Token Current => _tokens[_position];

    private bool IsSymbol(string text) => !AtEnd && Current.Kind == TokenKind.Symbol && Current.Text == text;

    private bool AtDelimiter() =>
        AtEnd || (Current.Kind == TokenKind.Symbol && Current.Text is "," or ";" or "}" or ")" or "]");

    public LuaValue ParseExpression()
    {
        if (AtEnd) return new LuaUnknown(0);

        var token = Current;
        LuaValue? value = null;

        switch (token.Kind)
        {
            case TokenKind.String:
                _position++;
                value = new LuaString(token.Text, token.Line);
                break;

            case TokenKind.Number:
                _position++;
                value = LuaLexer.TryParseNumber(token.Text, out var number) ? new LuaNumber(number, token.Line) : null;
                break;

            case TokenKind.Name when token.Text is "true" or "false":
                _position++;
                value = new LuaBool(token.Text == "true", token.Line);
                break;

            case TokenKind.Name when token.Text == "nil":
                _position++;
                value = new LuaNil(token.Line);
                break;

            case TokenKind.Symbol when token.Text == "{":
                value = ParseTable();
                break;

            case TokenKind.Symbol when token.Text == "-" && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.Number:
                _position += 2;
                value = LuaLexer.TryParseNumber(_tokens[_position - 1].Text, out var negative) ? new LuaNumber(-negative, token.Line) : null;
                break;

            case TokenKind.Name when _scope is not null && !Keywords.Contains(token.Text):
                value = ParseName(_scope);
                if (value is null && EndsHere()) return new LuaUnknown(token.Line); // an unknown variable or call, read to its end
                break;
        }

        // With a scope, "a" .. name .. "-b" is a string too when every part is known.
        if (_scope is not null && value is LuaString head && IsSymbol(".."))
        {
            _position++;
            return ParseExpression() is LuaString tail ? new LuaString(head.Value + tail.Value, token.Line) : new LuaUnknown(token.Line);
        }

        // A literal followed by an operator (e.g. "a" .. b, 2 * x) is not a constant.
        // A table literal may also end a statement, so only an operator (not any other token) makes it an expression.
        if (value is LuaTable)
        {
            if (!AtBinaryOperator()) return value;
        }
        else if (value is not null && (AtDelimiter() || (_scope is not null && EndsHere())))
        {
            return value;
        }

        SkipExpression();
        return new LuaUnknown(token.Line);
    }

    /// <summary>
    /// A variable, a field of a known table (<c>example.name</c>), <c>data.raw.type["name"]</c> or a call the scope evaluates.
    /// Null when the value is not known; the tokens read so far stay consumed and the caller skips the rest.
    /// </summary>
    private LuaValue? ParseName(LuaScope scope)
    {
        var line = Current.Line;
        var path = new List<string> { Current.Text };
        _position++;

        while (true)
        {
            if (IsSymbol(".") && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.Name)
            {
                path.Add(_tokens[_position + 1].Text);
                _position += 2;
            }
            else if (IsSymbol("[") && _position + 2 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.String
                     && _tokens[_position + 2] is { Kind: TokenKind.Symbol, Text: "]" })
            {
                path.Add(_tokens[_position + 1].Text);
                _position += 3;
            }
            else break;
        }

        if (IsSymbol("("))
        {
            _position++;
            var args = new List<LuaValue>();
            while (!AtEnd && !IsSymbol(")"))
            {
                args.Add(ParseExpression());
                if (IsSymbol(",")) _position++;
                else if (!IsSymbol(")")) return null;
            }
            if (!IsSymbol(")")) return null;
            _position++;
            return scope.Call?.Invoke(string.Join('.', path), args, line);
        }

        if (path.Count >= 4 && path[0] == "data" && path[1] == "raw")
        {
            var reference = new LuaRawRef(path[2], path[3], line);
            scope.RawRead?.Invoke(reference);
            return path.Count == 4 ? reference : null;
        }

        LuaValue? value = scope.Locals.GetValueOrDefault(path[0]);
        for (var k = 1; k < path.Count && value is not null; k++)
            value = value is LuaTable table ? table.Fields.GetValueOrDefault(path[k]) : null;
        return value;
    }

    // With a scope the parser also reads statements (local x = "a"), where no delimiter follows: the next statement starts.
    // An expression goes on only with an operator, a field or index, or a call.
    private bool EndsHere() =>
        AtDelimiter() || (!AtBinaryOperator() && Current.Kind != TokenKind.String
                          && !(Current.Kind == TokenKind.Symbol && Current.Text is "." or ":" or "(" or "[" or "{"));

    private bool AtBinaryOperator() =>
        !AtEnd && (Current.Kind == TokenKind.Symbol
            ? Current.Text is ".." or "==" or "~=" or "<=" or ">=" or "<" or ">" or "+" or "-" or "*" or "/" or "//" or "%" or "^"
            : Current is { Kind: TokenKind.Name, Text: "and" or "or" });

    public LuaTable ParseTable()
    {
        var table = new LuaTable(Current.Line);
        _position++; // '{'

        while (!AtEnd && !IsSymbol("}"))
        {
            if (Current.Kind == TokenKind.Name && _position + 1 < _tokens.Count
                && _tokens[_position + 1] is { Kind: TokenKind.Symbol, Text: "=" })
            {
                var name = Current.Text;
                _position += 2;
                table.Fields[name] = ParseExpression();
            }
            else if (IsSymbol("["))
            {
                _position++;
                var key = ParseExpression();
                if (IsSymbol("]")) _position++;

                if (IsSymbol("="))
                {
                    _position++;
                    var value = ParseExpression();
                    if (key is LuaString keyString) table.Fields[keyString.Value] = value;
                }
            }
            else
            {
                table.Items.Add(ParseExpression());
            }

            if (IsSymbol(",") || IsSymbol(";")) _position++;
            else if (!IsSymbol("}") && !AtEnd) _position++; // garbage: make progress
        }

        if (IsSymbol("}")) _position++;
        return table;
    }

    /// <summary>Skips tokens up to (not including) the next delimiter at nesting depth zero.</summary>
    private void SkipExpression()
    {
        var depth = 0;
        while (!AtEnd)
        {
            var t = Current;

            if (t.Kind == TokenKind.Name && t.Text == "function")
            {
                SkipFunction();
                continue;
            }

            if (t.Kind == TokenKind.Symbol)
            {
                if (t.Text is "(" or "[" or "{")
                {
                    depth++;
                }
                else if (t.Text is ")" or "]" or "}")
                {
                    if (depth == 0) return;
                    depth--;
                }
                else if (depth == 0 && t.Text is "," or ";")
                {
                    return;
                }
            }

            _position++;
        }
    }

    /// <summary>Skips a "function ... end" body, counting nested blocks.</summary>
    private void SkipFunction()
    {
        var blocks = 0;
        while (!AtEnd)
        {
            var t = Current;
            _position++;
            if (t.Kind != TokenKind.Name) continue;

            switch (t.Text)
            {
                case "function":
                case "if":
                case "do":
                case "repeat":
                    blocks++;
                    break;
                case "end":
                case "until":
                    blocks--;
                    break;
            }

            if (blocks <= 0) return;
        }
    }
}
