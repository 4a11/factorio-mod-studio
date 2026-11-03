using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

public enum DiagnosticSeverity { Warning, Error }

public sealed class LogDiagnostic
{
    public DiagnosticSeverity Severity { get; init; }

    /// <summary>First line of the message (for runtime errors it already includes the Lua error text).</summary>
    public string Message { get; init; } = "";

    /// <summary>Continuation lines, typically a Lua stack traceback.</summary>
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    /// <summary>Mod that the first Lua location in the message or traceback points into, if any.</summary>
    public string? Mod { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }

    /// <summary>"load" (mod failed to load), "runtime" (script error while playing), "fatal" (game aborted) or null.</summary>
    public string? Category { get; init; }

    /// <summary>A plain-language suggestion. Heuristic, matched on common message patterns.</summary>
    public string? Hint { get; init; }

    /// <summary>How many times an identical message appeared.</summary>
    public int Count { get; set; } = 1;
}

public sealed class LogReport
{
    public IReadOnlyList<LogDiagnostic> Diagnostics { get; init; } = Array.Empty<LogDiagnostic>();

    public int ErrorCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
    public bool HasErrors => ErrorCount > 0;
}

/// <summary>
/// Extracts errors and warnings from Factorio's log output (stdout or <c>factorio-current.log</c>).
/// The parser is line-based and tolerant: unknown lines are ignored, identical messages are merged.
/// </summary>
public static class LogParser
{
    // "   0.351 Error ModManager.cpp:1744: message"  /  "  12.0 Warning Foo.cpp:1: message"
    private static readonly Regex Stamped = new(
        @"^\s*(?<t>\d+(?:\.\d+)?)\s+(?<level>Error|Warning|Script|Info|Verbose)\b\s*(?:(?<src>[\w.\-]+\.(?:cpp|hpp|cc|c|h)):(?<srcLine>\d+):\s*)?(?<msg>.*)$",
        RegexOptions.Compiled);

    // Lines that start an error without a timestamp (script errors are printed this way).
    private static readonly Regex UnstampedError = new(
        @"^(?:Error:|Error while running|Error when running|Error in |The mod .+ caused a non-recoverable error)",
        RegexOptions.Compiled);

    // __mod-name__/folder/file.lua:123
    private static readonly Regex Location = new(
        @"__(?<mod>[^\s/]+?)__/(?<file>[^\s:()'""]+?\.lua):(?<line>\d+)",
        RegexOptions.Compiled);

    private const int MaxDetailLines = 40;

    private sealed class Draft
    {
        public DiagnosticSeverity Severity;
        public string Message = "";
        public readonly List<string> Details = new();
    }

    public static LogReport Parse(IEnumerable<string> lines)
    {
        var drafts = new List<Draft>();
        Draft? current = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r', '\n');
            var stamped = Stamped.Match(line);

            if (stamped.Success)
            {
                current = null;
                var level = stamped.Groups["level"].Value;
                if (level is "Error" or "Warning")
                {
                    current = new Draft
                    {
                        Severity = level == "Error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                        Message = stamped.Groups["msg"].Value.Trim()
                    };
                    drafts.Add(current);
                }
                continue;
            }

            if (line.Trim().Length == 0)
            {
                current = null;
                continue;
            }

            if (UnstampedError.IsMatch(line))
            {
                current = new Draft { Severity = DiagnosticSeverity.Error, Message = line.Trim() };
                drafts.Add(current);
                continue;
            }

            if (current is not null && current.Details.Count < MaxDetailLines)
                current.Details.Add(line.TrimEnd());
        }

        return new LogReport { Diagnostics = Merge(drafts) };
    }

    public static LogReport ParseFile(string path) => Parse(File.ReadLines(path));

    private static List<LogDiagnostic> Merge(List<Draft> drafts)
    {
        var byKey = new Dictionary<string, LogDiagnostic>(StringComparer.Ordinal);
        var ordered = new List<LogDiagnostic>();

        foreach (var draft in drafts)
        {
            var message = draft.Message;
            var details = draft.Details;

            // "Error while running event x::on_tick (ID 0)" is followed by the real Lua error on the next line.
            if (message.StartsWith("Error while running", StringComparison.Ordinal) && details.Count > 0)
            {
                message = $"{message}: {details[0].Trim()}";
                details = details.Skip(1).ToList();
            }

            var key = $"{draft.Severity}|{message}";
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.Count++;
                continue;
            }

            var fullText = message + "\n" + string.Join("\n", details);
            var location = Location.Match(message);
            if (!location.Success) location = Location.Match(fullText);

            var diagnostic = new LogDiagnostic
            {
                Severity = draft.Severity,
                Message = message,
                Details = details,
                Mod = location.Success ? location.Groups["mod"].Value : null,
                File = location.Success ? location.Groups["file"].Value : null,
                Line = location.Success ? int.Parse(location.Groups["line"].Value) : null,
                Category = Categorize(message),
                Hint = LogHints.Find(fullText)
            };

            byKey[key] = diagnostic;
            ordered.Add(diagnostic);
        }

        return ordered;
    }

    private static string? Categorize(string message)
    {
        if (message.Contains("non-recoverable error", StringComparison.Ordinal)) return "fatal";
        if (message.Contains("Failed to load mod", StringComparison.Ordinal)) return "load";
        if (message.StartsWith("Error while running", StringComparison.Ordinal)) return "runtime";
        return null;
    }
}

/// <summary>Small table of "what this error usually means" explanations. Intentionally conservative.</summary>
internal static class LogHints
{
    private static readonly (Regex Pattern, string Hint)[] Table =
    {
        (new Regex(@"attempt to index (?:a nil value|field '[^']*' \(a nil value\)|local '[^']*' \(a nil value\)|upvalue '[^']*' \(a nil value\))", RegexOptions.Compiled),
            "Something is nil where a table was expected. In the data stage check that the prototype exists (data.raw[type][name]) and that your mod loads after the mod that defines it (add it to dependencies). At runtime check that the entity, player or surface is valid before using it."),

        (new Regex(@"attempt to call (?:a nil value|global '[^']*'|field '[^']*'|method '[^']*')", RegexOptions.Compiled),
            "A function that does not exist is being called. Check the spelling, that the module was required, and that the API function exists in your target Factorio version."),

        (new Regex(@"Key ""?'?[\w\-]+'?""? not found in property tree", RegexOptions.Compiled),
            "A required prototype property is missing or misspelled. Compare the prototype with the documentation for its type for the Factorio version you target."),

        (new Regex(@"Missing required dependency|missing dependency|requires .+ (?:but|which) ", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "A dependency is missing or has the wrong version. Check the \"dependencies\" in info.json and the mods enabled in the profile.")
    };

    public static string? Find(string text)
    {
        foreach (var (pattern, hint) in Table)
            if (pattern.IsMatch(text)) return hint;
        return null;
    }
}
