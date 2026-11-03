namespace FactorioModStudio.Core;

/// <summary>Placeholders missing from / unexpected in a translation, both as normalised tokens.</summary>
public sealed record LocalePlaceholderDiff(IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected)
{
    public bool IsEmpty => Missing.Count == 0 && Unexpected.Count == 0;
}

/// <summary>
/// Finds the tokens of a Factorio locale string that must survive translation: parameter references (<c>__1__</c>),
/// game references (<c>__ITEM__iron-plate__</c>), plurals, rich text tags (<c>[color=red]</c>) and literal <c>\n</c>.
/// The browser keeps a port of these rules in <c>wwwroot/locale-placeholders.js</c>; change both together.
/// </summary>
public static class LocalePlaceholders
{
    private static readonly string[] BareKeywords = { "CONTROL_STYLE_BEGIN", "CONTROL_STYLE_END", "REMARK_COLOR_BEGIN", "REMARK_COLOR_END" };
    private static readonly string[] ArgumentKeywords = { "ENTITY", "ITEM", "FLUID", "RECIPE", "TECHNOLOGY", "TILE", "CONTROL" };
    private const string PluralPrefix = "__plural_for_parameter_";

    // The grammar is scanned by hand instead of with a regular expression: unterminated input such as thousands of "[a=" or
    // "__ITEM__" must stay linear, which a backtracking engine cannot promise. The browser port (locale-placeholders.js)
    // implements exactly the same scanner.

    private readonly record struct Found(int Start, int Length, string Normalised);

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsArgument(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' or '/';

    /// <summary>Index just past the first "__" at or after <paramref name="from"/> that is preceded by at least one argument character, or -1.</summary>
    private static int ArgumentEnd(string text, int from)
    {
        var k = from;
        if (k >= text.Length || !IsArgument(text[k])) return -1;
        k++; // at least one argument character
        while (true)
        {
            if (k + 1 < text.Length && text[k] == '_' && text[k + 1] == '_') return k + 2;
            if (k >= text.Length || !IsArgument(text[k])) return -1;
            k++;
        }
    }

    /// <summary>Position of the first match at <paramref name="i"/>: length of the raw token, or 0 when none starts there.</summary>
    private static List<Found> Scan(string text)
    {
        var result = new List<Found>();
        if (string.IsNullOrEmpty(text)) return result;

        // Searches are monotone (start positions only grow), so remembering the last result keeps the scan linear.
        var braceFrom = -1;
        var brace = -1;
        var closeFrom = -1;
        var close = -1;

        int NextBrace(int from)
        {
            if (braceFrom >= 0 && from >= braceFrom && (brace < 0 || brace >= from)) return brace;
            braceFrom = from;
            return brace = text.IndexOf('}', from);
        }

        int NextClose(int from)
        {
            if (closeFrom >= 0 && from >= closeFrom && (close < 0 || close >= from)) return close;
            closeFrom = from;
            return close = text.AsSpan(from).IndexOfAny(']', '\r', '\n') is var at and >= 0 ? from + at : -1;
        }

        var i = 0;
        while (i < text.Length)
        {
            var length = 0;
            string? normalised = null;
            var c = text[i];

            if (c == '_' && i + 1 < text.Length && text[i + 1] == '_')
            {
                length = MatchUnderscore(text, i, NextBrace, out normalised);
            }
            else if (c == '[')
            {
                length = MatchTag(text, i, NextClose);
            }
            else if (c == '\\' && i + 1 < text.Length && text[i + 1] == 'n')
            {
                length = 2;
            }

            if (length > 0)
            {
                result.Add(new Found(i, length, normalised ?? text.Substring(i, length)));
                i += length;
            }
            else
            {
                i++;
            }
        }
        return result;
    }

    private static int MatchUnderscore(string text, int i, Func<int, int> nextBrace, out string? normalised)
    {
        normalised = null;
        var rest = text.AsSpan(i);

        if (rest.StartsWith(PluralPrefix, StringComparison.Ordinal))
        {
            var p = i + PluralPrefix.Length;
            var digits = p;
            while (p < text.Length && IsDigit(text[p])) p++;
            if (p > digits && p + 1 < text.Length && text[p] == '_' && text[p + 1] == '{')
            {
                var close = nextBrace(p + 2);
                if (close >= 0 && close + 2 < text.Length && text[close + 1] == '_' && text[close + 2] == '_')
                {
                    normalised = $"{PluralPrefix}{text.Substring(digits, p - digits)}__";
                    return close + 3 - i;
                }
            }
        }

        var a = i + 2;

        // __12__
        var d = a;
        while (d < text.Length && IsDigit(text[d])) d++;
        if (d > a && d + 1 < text.Length && text[d] == '_' && text[d + 1] == '_') return d + 2 - i;

        foreach (var keyword in BareKeywords)
            if (text.AsSpan(a).StartsWith(keyword, StringComparison.Ordinal) && HasUnderscores(text, a + keyword.Length))
                return a + keyword.Length + 2 - i;

        foreach (var keyword in ArgumentKeywords)
        {
            if (!text.AsSpan(a).StartsWith(keyword, StringComparison.Ordinal) || !HasUnderscores(text, a + keyword.Length)) continue;
            var end = ArgumentEnd(text, a + keyword.Length + 2);
            if (end >= 0) return end - i;
        }

        const string Alt = "ALT_CONTROL";
        if (text.AsSpan(a).StartsWith(Alt, StringComparison.Ordinal) && HasUnderscores(text, a + Alt.Length))
        {
            var b = a + Alt.Length + 2;
            var n = b;
            while (n < text.Length && IsDigit(text[n])) n++;
            if (n > b && n + 1 < text.Length && text[n] == '_' && text[n + 1] == '_')
            {
                var end = ArgumentEnd(text, n + 2);
                if (end >= 0) return end - i;
            }

            var single = ArgumentEnd(text, b);
            if (single >= 0) return single - i;
        }

        return 0;
    }

    private static bool HasUnderscores(string text, int at) => at + 1 < text.Length && text[at] == '_' && text[at + 1] == '_';

    private static int MatchTag(string text, int i, Func<int, int> nextClose)
    {
        var p = i + 1;
        if (p < text.Length && text[p] == '/')
        {
            p++;
            if (!ReadName(text, ref p)) return 0;
            return p < text.Length && text[p] == ']' ? p + 1 - i : 0;
        }

        if (!ReadName(text, ref p)) return 0;
        if (p < text.Length && text[p] == ']') return p + 1 - i;
        if (p < text.Length && text[p] == '=')
        {
            var close = nextClose(p + 1);
            return close >= 0 && text[close] == ']' ? close + 1 - i : 0;
        }
        return 0;
    }

    /// <summary>name = [a-z][a-z-]*</summary>
    private static bool ReadName(string text, ref int p)
    {
        if (p >= text.Length || text[p] < 'a' || text[p] > 'z') return false;
        p++;
        while (p < text.Length && (text[p] is (>= 'a' and <= 'z') or '-')) p++;
        return true;
    }

    /// <summary>Tokens in order of appearance (duplicates kept), exactly as written in the text.</summary>
    public static IReadOnlyList<string> Extract(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return Scan(text).Select(f => text.Substring(f.Start, f.Length)).ToList();
    }

    /// <summary>Compares the tokens of the source string with those of its translation (multiset, normalised).</summary>
    public static LocalePlaceholderDiff Compare(string source, string target)
    {
        var from = Normalised(source);
        var to = Normalised(target);
        return new LocalePlaceholderDiff(Deficit(from, to), Deficit(to, from));
    }

    private static List<string> Normalised(string? text) =>
        string.IsNullOrEmpty(text) ? new List<string>() : Scan(text).Select(f => f.Normalised).ToList();

    /// <summary>Tokens of <paramref name="have"/> that <paramref name="other"/> contains fewer times, grouped in order of first appearance.</summary>
    private static List<string> Deficit(List<string> have, List<string> other)
    {
        var haveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var token in have)
        {
            if (!haveCounts.TryAdd(token, 1)) haveCounts[token]++;
            else order.Add(token);
        }

        var otherCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in other) otherCounts[token] = otherCounts.GetValueOrDefault(token) + 1;

        var result = new List<string>();
        foreach (var token in order)
        {
            var missing = haveCounts[token] - otherCounts.GetValueOrDefault(token);
            for (var i = 0; i < missing; i++) result.Add(token);
        }
        return result;
    }
}
