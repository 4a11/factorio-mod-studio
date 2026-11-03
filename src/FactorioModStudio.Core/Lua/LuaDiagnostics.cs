namespace FactorioModStudio.Core.Lua;

/// <param name="Line">1-based.</param>
/// <param name="Column">1-based, in UTF-16 code units.</param>
/// <param name="Severity">"error" or "warning".</param>
public sealed record LuaDiagnostic(int Line, int Column, string Severity, string Code, string Message);

/// <summary>
/// A structural check of Lua source: unterminated strings and long brackets, unbalanced brackets and unbalanced
/// blocks (function/if/for/while/do/repeat). This is deliberately not a full parser; it finds the mistakes that make a
/// file obviously unloadable and keeps going after each one so a single typo does not flood the list.
/// Pure, never throws, linear time.
/// </summary>
public static class LuaDiagnostics
{
    public const int MaxDiagnostics = 50;
    private const int MaxCollected = 2000;

    private const byte Paren = 0, Square = 1, Curly = 2, Function = 3, If = 4, Do = 5, For = 6, While = 7, Repeat = 8;
    private const int KindCount = 9;

    private struct Entry
    {
        public byte Kind;
        public int Pos;
        public bool DoSeen;
    }

    private sealed class Scanner
    {
        public readonly string S;
        public readonly List<(int Pos, string Code, string Message)> Found = new();
        public Entry[] Stack = new Entry[64];
        public int Sp;
        public readonly int[] Counts = new int[KindCount];
        public int Endable; // function, if, do, for, while currently open

        public Scanner(string s) { S = s; }

        public void Report(int pos, string code, string message)
        {
            if (Found.Count < MaxCollected) Found.Add((pos, code, message));
        }

        public void Push(byte kind, int pos)
        {
            if (Sp == Stack.Length) Array.Resize(ref Stack, Stack.Length * 2);
            Stack[Sp++] = new Entry { Kind = kind, Pos = pos };
            Counts[kind]++;
            if (kind is >= Function and <= While) Endable++;
        }

        public Entry Pop()
        {
            var e = Stack[--Sp];
            Counts[e.Kind]--;
            if (e.Kind is >= Function and <= While) Endable--;
            return e;
        }

        public void ReportUnclosed(Entry e, string before)
        {
            if (e.Kind <= Curly)
                Report(e.Pos, "LUA003", $"Unclosed '{Open(e.Kind)}'{before}.");
            else
                Report(e.Pos, "LUA005", $"'{Word(e.Kind)}' block is not closed: missing '{(e.Kind == Repeat ? "until" : "end")}'{before}.");
        }
    }

    private static char Open(byte kind) => kind switch { Paren => '(', Square => '[', _ => '{' };
    private static char Close(byte kind) => kind switch { Paren => ')', Square => ']', _ => '}' };

    private static string Word(byte kind) => kind switch
    {
        Function => "function", If => "if", Do => "do", For => "for", While => "while", Repeat => "repeat", _ => "?"
    };

    public static IReadOnlyList<LuaDiagnostic> Check(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<LuaDiagnostic>();

        var scanner = new Scanner(text);
        try { Scan(scanner); }
        catch (Exception) { /* a checker must never take the editor down; report what was found so far */ }

        return ToDiagnostics(text, scanner.Found);
    }

    private static IReadOnlyList<LuaDiagnostic> ToDiagnostics(string text, List<(int Pos, string Code, string Message)> found)
    {
        if (found.Count == 0) return Array.Empty<LuaDiagnostic>();

        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') lineStarts.Add(i + 1);

        var ordered = found.Select((f, index) => (f, index)).OrderBy(x => x.f.Pos).ThenBy(x => x.index).Take(MaxDiagnostics);
        var result = new List<LuaDiagnostic>();
        foreach (var (f, _) in ordered)
        {
            var idx = lineStarts.BinarySearch(f.Pos);
            if (idx < 0) idx = ~idx - 1;
            result.Add(new LuaDiagnostic(idx + 1, f.Pos - lineStarts[idx] + 1, "error", f.Code, f.Message));
        }
        return result;
    }

    private static bool IsIdentStart(char c) => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c > 127;
    private static bool IsIdentPart(char c) => IsIdentStart(c) || (c >= '0' && c <= '9');
    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    /// <summary>At s[i] == '[': returns the level when a long bracket opens here, else -1. <paramref name="after"/> is the index after the opener.</summary>
    private static int LongOpen(string s, int i, out int after)
    {
        var j = i + 1;
        while (j < s.Length && s[j] == '=') j++;
        if (j < s.Length && s[j] == '[') { after = j + 1; return j - i - 1; }
        after = i;
        return -1;
    }

    /// <summary>Index just past the closing bracket of the given level, or -1.</summary>
    private static int LongClose(string s, int from, int level)
    {
        var closer = "]" + new string('=', level) + "]";
        var at = s.IndexOf(closer, from, StringComparison.Ordinal);
        return at < 0 ? -1 : at + closer.Length;
    }

    private static void Scan(Scanner sc)
    {
        var s = sc.S;
        var n = s.Length;
        var i = n > 0 && s[0] == '\uFEFF' ? 1 : 0; // a UTF-8 byte order mark is not part of the code

        if (i < n && s[i] == '#') while (i < n && s[i] != '\n') i++; // shebang

        while (i < n)
        {
            var c = s[i];

            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }

            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                var start = i;
                i += 2;
                if (i < n && s[i] == '[' && LongOpen(s, i, out var after) is var level and >= 0)
                {
                    var end = LongClose(s, after, level);
                    if (end < 0) { sc.Report(start, "LUA002", "Unterminated long comment."); i = n; }
                    else i = end;
                }
                else
                {
                    while (i < n && s[i] != '\n') i++;
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                var start = i++;
                var closed = false;
                while (i < n)
                {
                    var d = s[i];
                    if (d == '\\')
                    {
                        if (i + 1 < n && s[i + 1] == 'z')
                        {
                            i += 2; // \z skips the following whitespace, including line breaks
                            while (i < n && (s[i] is ' ' or '\t' or '\r' or '\n' or '\f' or '\v')) i++;
                            continue;
                        }
                        i += (i + 2 < n && s[i + 1] == '\r' && s[i + 2] == '\n') ? 3 : 2; // backslash-newline continues the string
                        continue;
                    }
                    if (d == c) { i++; closed = true; break; }
                    if (d == '\n' || d == '\r') break;
                    i++;
                }
                if (i > n) i = n;
                if (!closed) sc.Report(start, "LUA001", "Unterminated string.");
                continue;
            }

            if (c == '[')
            {
                var level = LongOpen(s, i, out var after);
                if (level >= 0)
                {
                    var end = LongClose(s, after, level);
                    if (end < 0) { sc.Report(i, "LUA002", "Unterminated long string."); i = n; }
                    else i = end;
                    continue;
                }
                OpenBracket(sc, Square, i);
                i++;
                continue;
            }

            if (c == '(') { OpenBracket(sc, Paren, i); i++; continue; }
            if (c == '{') { OpenBracket(sc, Curly, i); i++; continue; }
            if (c == ')') { CloseBracket(sc, Paren, i); i++; continue; }
            if (c == ']') { CloseBracket(sc, Square, i); i++; continue; }
            if (c == '}') { CloseBracket(sc, Curly, i); i++; continue; }

            if (IsDigit(c) || (c == '.' && i + 1 < n && IsDigit(s[i + 1])))
            {
                i = SkipNumber(s, i);
                continue;
            }

            if (IsIdentStart(c))
            {
                var start = i++;
                while (i < n && IsIdentPart(s[i])) i++;
                Keyword(sc, s.AsSpan(start, i - start), start);
                continue;
            }

            i++;
        }

        // End of input: everything still open is a problem.
        while (sc.Sp > 0) sc.ReportUnclosed(sc.Pop(), string.Empty);
    }

    private static int SkipNumber(string s, int i)
    {
        var n = s.Length;
        var hex = s[i] == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X');
        if (hex) i += 2;

        while (i < n)
        {
            var c = s[i];
            if (IsIdentPart(c)) // digits, hex digits, exponent letters, suffixes
            {
                var exponent = hex ? (c == 'p' || c == 'P') : (c == 'e' || c == 'E');
                i++;
                if (exponent && i < n && (s[i] == '+' || s[i] == '-')) i++;
            }
            else if (c == '.' && !(i + 1 < n && s[i + 1] == '.')) i++; // "3..2" is a number, a concat and a number
            else break;
        }
        return i;
    }

    private static void OpenBracket(Scanner sc, byte kind, int pos) => sc.Push(kind, pos);

    private static void CloseBracket(Scanner sc, byte kind, int pos)
    {
        if (sc.Sp > 0 && sc.Stack[sc.Sp - 1].Kind == kind) { sc.Pop(); return; }

        if (sc.Counts[kind] > 0)
        {
            // The matching opener is further down: whatever sits above it was never closed.
            while (sc.Stack[sc.Sp - 1].Kind != kind) sc.ReportUnclosed(sc.Pop(), $" before '{Close(kind)}'");
            sc.Pop();
            return;
        }

        if (sc.Sp > 0 && sc.Stack[sc.Sp - 1].Kind <= Curly)
        {
            var open = sc.Pop();
            sc.Report(pos, "LUA003", $"Mismatched bracket: '{Close(kind)}' does not close '{Open(open.Kind)}' (expected '{Close(open.Kind)}').");
            return;
        }

        sc.Report(pos, "LUA003", $"Unmatched '{Close(kind)}'.");
    }

    private static void Keyword(Scanner sc, ReadOnlySpan<char> word, int pos)
    {
        switch (word.Length)
        {
            case 2:
                if (word.SequenceEqual("if")) sc.Push(If, pos);
                else if (word.SequenceEqual("do")) DoKeyword(sc, pos);
                return;
            case 3:
                if (word.SequenceEqual("end")) EndKeyword(sc, pos);
                else if (word.SequenceEqual("for")) sc.Push(For, pos);
                return;
            case 4:
                if (word.SequenceEqual("else")) ElseKeyword(sc, "else", pos);
                return;
            case 5:
                if (word.SequenceEqual("while")) sc.Push(While, pos);
                else if (word.SequenceEqual("until")) UntilKeyword(sc, pos);
                return;
            case 6:
                if (word.SequenceEqual("elseif")) ElseKeyword(sc, "elseif", pos);
                else if (word.SequenceEqual("repeat")) sc.Push(Repeat, pos);
                return;
            case 8:
                if (word.SequenceEqual("function")) sc.Push(Function, pos);
                return;
        }
    }

    private static void DoKeyword(Scanner sc, int pos)
    {
        if (sc.Sp > 0)
        {
            ref var top = ref sc.Stack[sc.Sp - 1];
            if ((top.Kind == For || top.Kind == While) && !top.DoSeen) { top.DoSeen = true; return; }
        }
        sc.Push(Do, pos);
    }

    private static void EndKeyword(Scanner sc, int pos)
    {
        if (sc.Endable == 0)
        {
            sc.Report(pos, "LUA004", "Unexpected 'end': there is no open block to close.");
            return;
        }

        // Anything above the nearest endable block was left open.
        while (sc.Stack[sc.Sp - 1].Kind is Paren or Square or Curly or Repeat)
            sc.ReportUnclosed(sc.Pop(), " before 'end'");
        sc.Pop();
    }

    private static void UntilKeyword(Scanner sc, int pos)
    {
        if (sc.Counts[Repeat] == 0)
        {
            sc.Report(pos, "LUA004", "Unexpected 'until': there is no open 'repeat'.");
            return;
        }

        while (sc.Stack[sc.Sp - 1].Kind != Repeat) sc.ReportUnclosed(sc.Pop(), " before 'until'");
        sc.Pop();
    }

    private static void ElseKeyword(Scanner sc, string word, int pos)
    {
        if (sc.Counts[If] == 0)
        {
            sc.Report(pos, "LUA004", $"Unexpected '{word}': there is no open 'if'.");
            return;
        }

        while (sc.Stack[sc.Sp - 1].Kind != If) sc.ReportUnclosed(sc.Pop(), $" before '{word}'");
    }
}
