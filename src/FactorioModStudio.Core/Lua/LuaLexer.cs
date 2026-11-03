using System.Globalization;
using System.Text;

namespace FactorioModStudio.Core.Lua;

internal enum TokenKind { Name, String, Number, Symbol }

internal readonly record struct Token(TokenKind Kind, string Text, int Line);

/// <summary>
/// A tolerant Lua tokenizer: enough to read table literals and spot call patterns in mod sources.
/// It never throws; malformed input just produces fewer or odd tokens.
/// </summary>
internal static class LuaLexer
{
    private static readonly string[] TwoCharSymbols = { "..", "==", "~=", "<=", ">=", "::", "//", "<<", ">>" };

    public static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, n = s.Length;

        while (i < n)
        {
            var c = s[i];

            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Comments: --  and  --[[ ... ]]  /  --[==[ ... ]==]
            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                i += 2;
                if (i < n && s[i] == '[' && TryOpenLongBracket(s, i, out var level, out var contentStart))
                {
                    var end = FindLongClose(s, contentStart, level);
                    var stop = end < 0 ? n : end + level + 2;
                    line += CountNewlines(s, i, stop);
                    i = stop;
                }
                else
                {
                    while (i < n && s[i] != '\n') i++;
                }
                continue;
            }

            // Long strings: [[ ... ]]
            if (c == '[' && TryOpenLongBracket(s, i, out var strLevel, out var strStart))
            {
                var end = FindLongClose(s, strStart, strLevel);
                var contentEnd = end < 0 ? n : end;
                var text = s[strStart..contentEnd];
                if (text.StartsWith("\r\n", StringComparison.Ordinal)) text = text[2..];
                else if (text.StartsWith('\n')) text = text[1..];

                tokens.Add(new Token(TokenKind.String, text, line));
                var stop = end < 0 ? n : end + strLevel + 2;
                line += CountNewlines(s, i, stop);
                i = stop;
                continue;
            }

            // Quoted strings
            if (c == '"' || c == '\'')
            {
                var startLine = line;
                var sb = new StringBuilder();
                i++;
                while (i < n && s[i] != c && s[i] != '\n')
                {
                    if (s[i] != '\\' || i + 1 >= n)
                    {
                        sb.Append(s[i++]);
                        continue;
                    }

                    i++; // at the escaped character
                    var e = s[i];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case '\n': sb.Append('\n'); line++; i++; break;
                        case 'z':
                            i++;
                            while (i < n && char.IsWhiteSpace(s[i])) { if (s[i] == '\n') line++; i++; }
                            break;
                        default:
                            if (char.IsDigit(e))
                            {
                                int value = 0, digits = 0;
                                while (digits < 3 && i < n && char.IsDigit(s[i])) { value = value * 10 + (s[i] - '0'); i++; digits++; }
                                sb.Append((char)Math.Min(value, char.MaxValue));
                            }
                            else
                            {
                                sb.Append(e);
                                i++;
                            }
                            break;
                    }
                }
                if (i < n && s[i] == c) i++;
                tokens.Add(new Token(TokenKind.String, sb.ToString(), startLine));
                continue;
            }

            // Numbers
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                var start = i;
                if (c == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < n && Uri.IsHexDigit(s[i])) i++;
                }
                else
                {
                    while (i < n && (char.IsDigit(s[i]) || (s[i] == '.' && !(i + 1 < n && s[i + 1] == '.')))) i++;
                    if (i < n && (s[i] == 'e' || s[i] == 'E'))
                    {
                        i++;
                        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
                        while (i < n && char.IsDigit(s[i])) i++;
                    }
                }
                tokens.Add(new Token(TokenKind.Number, s[start..i], line));
                continue;
            }

            // Names
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Name, s[start..i], line));
                continue;
            }

            // Symbols
            if (i + 2 < n && s[i] == '.' && s[i + 1] == '.' && s[i + 2] == '.')
            {
                tokens.Add(new Token(TokenKind.Symbol, "...", line));
                i += 3;
                continue;
            }
            if (i + 1 < n)
            {
                var two = s.Substring(i, 2);
                if (Array.IndexOf(TwoCharSymbols, two) >= 0)
                {
                    tokens.Add(new Token(TokenKind.Symbol, two, line));
                    i += 2;
                    continue;
                }
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), line));
            i++;
        }

        return tokens;
    }

    public static bool TryParseNumber(string text, out double value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                value = hex;
                return true;
            }
            value = 0;
            return false;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // "[" "="* "["  -> level = number of '='
    private static bool TryOpenLongBracket(string s, int i, out int level, out int contentStart)
    {
        level = 0;
        contentStart = 0;
        var p = i + 1;
        while (p < s.Length && s[p] == '=') { level++; p++; }
        if (p < s.Length && s[p] == '[')
        {
            contentStart = p + 1;
            return true;
        }
        return false;
    }

    private static int FindLongClose(string s, int from, int level) =>
        s.IndexOf("]" + new string('=', level) + "]", from, StringComparison.Ordinal);

    private static int CountNewlines(string s, int from, int to)
    {
        var count = 0;
        for (var k = from; k < to && k < s.Length; k++)
            if (s[k] == '\n') count++;
        return count;
    }
}
