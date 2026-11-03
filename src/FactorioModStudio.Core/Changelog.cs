using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <summary>Minimal reader for Factorio's changelog.txt format (only what validation needs).</summary>
public static class Changelog
{
    private static readonly Regex Separator = new(@"^-{10,}\s*$", RegexOptions.Compiled);
    private static readonly Regex VersionLine = new(@"^Version:\s*(?<v>\S+)\s*$", RegexOptions.Compiled);

    /// <summary>The standard separator line is exactly 99 dashes.</summary>
    public const int StandardSeparatorLength = 99;

    public sealed record Entry(int Line, string Version);

    public sealed record ParseResult(IReadOnlyList<Entry> Entries, IReadOnlyList<int> NonStandardSeparatorLines, IReadOnlyList<int> SeparatorsWithoutVersion);

    public static ParseResult Parse(IReadOnlyList<string> lines)
    {
        var entries = new List<Entry>();
        var badSeparators = new List<int>();
        var orphanSeparators = new List<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd();
            if (!Separator.IsMatch(line)) continue;

            if (line.Length != StandardSeparatorLength) badSeparators.Add(i + 1);

            var next = i + 1 < lines.Count ? VersionLine.Match(lines[i + 1].TrimEnd()) : Match.Empty;
            if (next.Success) entries.Add(new Entry(i + 2, next.Groups["v"].Value));
            else orphanSeparators.Add(i + 1);
        }

        return new ParseResult(entries, badSeparators, orphanSeparators);
    }
}
