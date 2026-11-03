using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioModStudio.Core;

namespace FactorioModStudio.Cli;

internal static class Reporting
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void PrintValidation(TextWriter output, ValidationResult result)
    {
        foreach (var issue in result.Issues.OrderByDescending(i => i.Severity))
        {
            var location = issue.File is null ? "" : issue.Line is null ? $"{issue.File}: " : $"{issue.File}:{issue.Line}: ";
            output.WriteLine($"{issue.Severity.ToString().ToLowerInvariant(),-7} {issue.Code}  {location}{issue.Message}");
        }

        output.WriteLine(result.Issues.Count == 0
            ? "No problems found."
            : $"{result.ErrorCount} error(s), {result.WarningCount} warning(s).");
    }

    public static void PrintLog(TextWriter output, LogReport report, bool verbose = false)
    {
        foreach (var d in report.Diagnostics)
        {
            var label = d.Severity == DiagnosticSeverity.Error ? "ERROR  " : "WARNING";
            var count = d.Count > 1 ? $" (x{d.Count})" : "";
            output.WriteLine($"{label}{count} {d.Message}");

            if (d.File is not null)
                output.WriteLine($"  at {d.Mod}/{d.File}:{d.Line}");
            if (d.Hint is not null)
                output.WriteLine($"  hint: {d.Hint}");
            if (verbose)
                foreach (var line in d.Details) output.WriteLine($"  | {line.Trim()}");
        }

        output.WriteLine(report.Diagnostics.Count == 0
            ? "No errors or warnings found in the log."
            : $"{report.ErrorCount} error(s), {report.WarningCount} warning(s).");
    }
}
