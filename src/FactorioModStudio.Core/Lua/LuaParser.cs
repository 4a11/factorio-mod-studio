namespace FactorioModStudio.Core.Lua;

internal abstract record LuaValue(int Line);

internal sealed record LuaString(string Value, int Line) : LuaValue(Line);

internal sealed record LuaNumber(double Value, int Line) : LuaValue(Line);

internal sealed record LuaBool(bool Value, int Line) : LuaValue(Line);

internal sealed record LuaNil(int Line) : LuaValue(Line);

/// <summary>An expression the parser deliberately does not evaluate (calls, concatenation, variables, functions...).</summary>
internal sealed record LuaUnknown(int Line) : LuaValue(Line);

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
    private readonly List<Token> _tokens;
    private int _position;

    public LuaParser(List<Token> tokens, int start = 0)
    {
        _tokens = tokens;
        _position = start;
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
        }

        // A literal followed by an operator (e.g. "a" .. b, 2 * x) is not a constant.
        // A table literal may also end a statement, so only an operator (not any other token) makes it an expression.
        if (value is LuaTable)
        {
            if (!AtBinaryOperator()) return value;
        }
        else if (value is not null && AtDelimiter())
        {
            return value;
        }

        SkipExpression();
        return new LuaUnknown(token.Line);
    }

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
