using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

public enum LocaleRowStatus { Translated, Untranslated, Extra }

/// <param name="Source">Value in the source language ("" for Extra rows).</param>
/// <param name="Target">Value in the target language, null if the key is absent.</param>
/// <param name="Identical">Translated, equal to the source text, not empty, and the language is not the source language.</param>
/// <param name="SourceFile">Path relative to the mod root with '/' separators; null for Extra rows.</param>
/// <param name="TargetFile">File of the first definition of the key in the target language; null if absent.</param>
/// <param name="Suggestion">Only for untranslated rows: a translation taken from the local translation memory.</param>
/// <param name="Marked">Untranslated although the key is in the file: the studio wrote the source text there with the
/// <see cref="LocaleEditor.UntranslatedMark"/> line above it (a new building block), so the game shows it until it is translated.
/// <see cref="Target"/> is null for such a row; saving a translation removes the mark.</param>
public sealed record LocaleRow(
    string Section, string Key, string Source, string? Target, LocaleRowStatus Status, bool Identical,
    string? SourceFile, string? TargetFile, int? TargetLine,
    IReadOnlyList<string> PlaceholderMissing, IReadOnlyList<string> PlaceholderUnexpected, string? Suggestion, bool Marked = false);

public sealed record LocaleLanguageSummary(string Code, int Total, int Translated, int Untranslated, int Identical, int Extra, int PlaceholderProblems);

public sealed record LocaleTable(string SourceLanguage, string Language, string Revision, IReadOnlyList<LocaleRow> Rows, LocaleLanguageSummary Summary);

/// <param name="Value">null removes the key.</param>
public sealed record LocaleChange(string Section, string Key, string? Value);

public sealed record LocaleApplyResult(string Revision, int Added, int Updated, int Removed, IReadOnlyList<string> TouchedFiles);

public sealed record LocaleMemoryEntry(string Source, string Target);

/// <summary>The language changed on disk since the revision the caller based its edit on.</summary>
public sealed class LocaleConflictException : FmsException
{
    public LocaleConflictException(string message, string currentRevision) : base(message) => CurrentRevision = currentRevision;

    public string CurrentRevision { get; }
}

/// <summary>
/// Reads locale files as a translation table (source language vs. one target language) and writes edits back line by line,
/// leaving every untouched byte of the .cfg files as it was.
/// </summary>
public static class LocaleEditor
{
    public const string DefaultSourceLanguage = "en";

    /// <summary>
    /// A comment line the studio puts above a key it wrote in the source language's words into another language: the key is there
    /// (the game shows that text), but it is not translated yet. The Localization tab lists it as untranslated; saving a translation
    /// of the key removes the line.
    /// </summary>
    public const string UntranslatedMark = "; fms: untranslated";

    /// <summary>The whole line written above such a key, saying what to do for people who edit the file by hand.</summary>
    public const string UntranslatedLine = UntranslatedMark + " (English for now): translate the line below, then delete this one";

    internal static bool IsUntranslatedMark(string line) => line.TrimStart().StartsWith(UntranslatedMark, StringComparison.Ordinal);

    private const long MaxCfgBytes = 16 * 1024 * 1024;
    private const int MaxChanges = 5000;
    private const int MaxValueLength = 65536;

    private static readonly Regex CodePattern = new(@"\A[a-z]{2,3}(-[A-Za-z0-9]{2,8})?\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static bool IsValidLanguageCode(string? code) => code is not null && CodePattern.IsMatch(code);

    /// <summary>"en" if <c>locale/en</c> exists, else the first existing language (ordinal order); null without any language.</summary>
    public static string? SourceLanguage(string modDir)
    {
        IReadOnlyList<string> languages;
        try { languages = LocaleData.Languages(modDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        if (languages.Count == 0) return null;
        return languages.Contains(DefaultSourceLanguage, StringComparer.Ordinal) ? DefaultSourceLanguage : languages[0];
    }

    public static IReadOnlyList<LocaleLanguageSummary> Summaries(string modDir)
    {
        var source = SourceLanguage(modDir);
        if (source is null) return Array.Empty<LocaleLanguageSummary>();

        var sourceData = Read(modDir, source);
        var result = new List<LocaleLanguageSummary>();
        foreach (var language in LocaleData.Languages(modDir))
            result.Add(BuildCore(modDir, source, sourceData, language, null).Summary);
        return result;
    }

    public static LocaleTable Build(string modDir, string language, IEnumerable<LocaleMemoryEntry>? memory = null)
    {
        if (!IsValidLanguageCode(language)) throw new FmsException($"\"{language}\" is not a valid language code.");
        if (!Directory.Exists(Path.Combine(modDir, "locale", language))) throw new FmsException($"The mod has no locale/{language} folder.");
        var source = SourceLanguage(modDir) ?? throw new FmsException("The mod has no locale folder to translate from.");
        return BuildCore(modDir, source, Read(modDir, source), language, memory);
    }

    // ------------------------------------------------------------------ reading

    private sealed class LanguageData
    {
        public List<LocaleEntry> Entries { get; } = new();
        public List<LocaleEntry> Firsts { get; } = new();
        public Dictionary<string, LocaleEntry> First { get; } = new(StringComparer.Ordinal);
        /// <summary>Entries ("file:line") right below an <see cref="UntranslatedMark"/> line.</summary>
        public HashSet<string> Marked { get; } = new(StringComparer.Ordinal);
        public string Revision { get; set; } = "";
    }

    private static List<string> CfgFiles(string languageDir)
    {
        var relative = new List<string>();
        if (!Directory.Exists(languageDir)) return relative;
        try
        {
            foreach (var file in Directory.EnumerateFiles(languageDir, "*", SearchOption.AllDirectories))
            {
                if (!Path.GetExtension(file).Equals(".cfg", StringComparison.OrdinalIgnoreCase)) continue;
                relative.Add(Path.GetRelativePath(languageDir, file).Replace('\\', '/'));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial list */ }

        relative.Sort(StringComparer.Ordinal);
        return relative;
    }

    private static string RevisionOf(IEnumerable<(string Relative, byte[] Bytes)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var zero = new byte[] { 0 };
        foreach (var (relative, bytes) in files)
        {
            hash.AppendData(Utf8.GetBytes(relative));
            hash.AppendData(zero);
            hash.AppendData(bytes);
            hash.AppendData(zero);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static LanguageData Read(string modDir, string language)
    {
        var data = new LanguageData();
        var languageDir = Path.Combine(modDir, "locale", language);
        var files = new List<(string, byte[])>();

        foreach (var relative in CfgFiles(languageDir))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path.Combine(languageDir, relative.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            files.Add((relative, bytes));
            if (bytes.Length <= MaxCfgBytes)
            {
                var text = Utf8.GetString(bytes);
                LocaleChecker.ParseCfg(text, $"locale/{language}/{relative}", data.Entries, null);
                var lines = text.TrimStart('﻿').Split('\n');
                for (var i = 1; i < lines.Length; i++)
                    if (IsUntranslatedMark(lines[i - 1])) data.Marked.Add($"locale/{language}/{relative}:{i + 1}");
            }
        }

        data.Revision = RevisionOf(files);
        foreach (var entry in data.Entries)
        {
            if (data.First.TryAdd(LocaleLanguage.Id(entry.Section, entry.Key), entry)) data.Firsts.Add(entry);
        }
        return data;
    }

    private static LocaleTable BuildCore(string modDir, string source, LanguageData sourceData, string language, IEnumerable<LocaleMemoryEntry>? memory)
    {
        var isSource = string.Equals(language, source, StringComparison.Ordinal);
        var target = isSource ? sourceData : Read(modDir, language);
        var rows = new List<LocaleRow>();
        var empty = Array.Empty<string>();

        foreach (var entry in sourceData.Firsts)
        {
            if (isSource)
            {
                rows.Add(new LocaleRow(entry.Section, entry.Key, entry.Value, entry.Value, LocaleRowStatus.Translated, false,
                    entry.File, entry.File, entry.Line, empty, empty, null));
                continue;
            }

            if (target.First.TryGetValue(LocaleLanguage.Id(entry.Section, entry.Key), out var found))
            {
                if (target.Marked.Contains($"{found.File}:{found.Line}"))
                {
                    rows.Add(new LocaleRow(entry.Section, entry.Key, entry.Value, null, LocaleRowStatus.Untranslated, false,
                        entry.File, found.File, found.Line, empty, empty, null, Marked: true));
                }
                else if (!string.IsNullOrWhiteSpace(found.Value))
                {
                    var diff = LocalePlaceholders.Compare(entry.Value, found.Value);
                    rows.Add(new LocaleRow(entry.Section, entry.Key, entry.Value, found.Value, LocaleRowStatus.Translated,
                        entry.Value.Length > 0 && string.Equals(entry.Value, found.Value, StringComparison.Ordinal),
                        entry.File, found.File, found.Line, diff.Missing, diff.Unexpected, null));
                }
                else
                {
                    rows.Add(new LocaleRow(entry.Section, entry.Key, entry.Value, found.Value, LocaleRowStatus.Untranslated, false,
                        entry.File, found.File, found.Line, empty, empty, null));
                }
            }
            else
            {
                rows.Add(new LocaleRow(entry.Section, entry.Key, entry.Value, null, LocaleRowStatus.Untranslated, false,
                    entry.File, null, null, empty, empty, null));
            }
        }

        if (!isSource)
        {
            foreach (var entry in target.Firsts)
            {
                if (sourceData.First.ContainsKey(LocaleLanguage.Id(entry.Section, entry.Key))) continue;
                rows.Add(new LocaleRow(entry.Section, entry.Key, "", entry.Value, LocaleRowStatus.Extra, false,
                    null, entry.File, entry.Line, empty, empty, null));
            }

            rows = AddSuggestions(rows, memory);
        }

        var summary = new LocaleLanguageSummary(
            language,
            Total: rows.Count(r => r.Status != LocaleRowStatus.Extra),
            Translated: rows.Count(r => r.Status == LocaleRowStatus.Translated),
            Untranslated: rows.Count(r => r.Status == LocaleRowStatus.Untranslated),
            Identical: rows.Count(r => r.Identical),
            Extra: rows.Count(r => r.Status == LocaleRowStatus.Extra),
            PlaceholderProblems: rows.Count(r => r.PlaceholderMissing.Count > 0 || r.PlaceholderUnexpected.Count > 0));

        return new LocaleTable(source, language, target.Revision, rows, summary);
    }

    /// <summary>Fills <see cref="LocaleRow.Suggestion"/> from the memory plus the language's own translated rows.</summary>
    private static List<LocaleRow> AddSuggestions(List<LocaleRow> rows, IEnumerable<LocaleMemoryEntry>? memory)
    {
        if (!rows.Any(r => r.Status == LocaleRowStatus.Untranslated && r.Source.Trim().Length > 0)) return rows;

        // source text (trimmed) -> candidate translation -> number of occurrences
        var candidates = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        void Add(string? source, string? translation)
        {
            if (source is null || string.IsNullOrWhiteSpace(translation)) return;
            var key = source.Trim();
            if (key.Length == 0) return;
            if (!candidates.TryGetValue(key, out var counts)) candidates[key] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
            counts[translation] = counts.GetValueOrDefault(translation) + 1;
        }

        if (memory is not null)
            foreach (var entry in memory) Add(entry?.Source, entry?.Target);
        foreach (var row in rows)
            if (row.Status == LocaleRowStatus.Translated) Add(row.Source, row.Target);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Status != LocaleRowStatus.Untranslated) continue;
            var key = row.Source.Trim();
            if (key.Length == 0 || !candidates.TryGetValue(key, out var counts)) continue;

            string? best = null;
            var bestCount = 0;
            foreach (var (text, count) in counts)
            {
                if (!LocalePlaceholders.Compare(row.Source, text).IsEmpty) continue;
                if (count > bestCount || (count == bestCount && string.CompareOrdinal(text, best) < 0))
                {
                    best = text;
                    bestCount = count;
                }
            }

            if (best is not null) rows[i] = row with { Suggestion = best };
        }

        return rows;
    }

    // ------------------------------------------------------------------ editing

    private sealed class CfgLine
    {
        public CfgLine(byte[] content, byte[] terminator) { Content = content; Terminator = terminator; }
        public byte[] Content { get; set; }
        /// <summary>Empty for an unterminated last line.</summary>
        public byte[] Terminator { get; set; }
    }

    private enum LineKind { Other, Header, Entry }

    private sealed class CfgFile
    {
        public CfgFile(string relative) => Relative = relative;
        public string Relative { get; }
        public bool Exists { get; init; }
        public bool HasBom { get; init; }
        public byte[] OriginalBytes { get; init; } = Array.Empty<byte>();
        public List<CfgLine> Lines { get; } = new();
        public bool Touched { get; set; }

        public bool CrLf => Lines.FirstOrDefault(l => l.Terminator.Length > 0)?.Terminator.Length == 2;

        public byte[] Serialize()
        {
            using var memory = new MemoryStream();
            if (HasBom) memory.Write(Bom);
            foreach (var line in Lines)
            {
                memory.Write(line.Content);
                memory.Write(line.Terminator);
            }
            return memory.ToArray();
        }

        /// <summary>Classifies every line with the same rules as the reader (<see cref="LocaleChecker.ParseCfg"/>).</summary>
        public List<(LineKind Kind, string? Section, string? Key)> Analyze()
        {
            var result = new List<(LineKind, string?, string?)>(Lines.Count);
            string? section = null;
            foreach (var line in Lines)
            {
                var text = Utf8.GetString(line.Content).Trim();
                if (text.Length == 0 || text[0] is ';' or '#') { result.Add((LineKind.Other, null, null)); continue; }

                if (text[0] == '[')
                {
                    var name = text.Length >= 2 && text[^1] == ']' ? text[1..^1].Trim() : "";
                    if (name.Length == 0) { result.Add((LineKind.Other, null, null)); continue; }
                    section = name;
                    result.Add((LineKind.Header, name, null));
                    continue;
                }

                var eq = text.IndexOf('=');
                var key = eq > 0 ? text[..eq].Trim() : "";
                if (key.Length == 0 || section is null) { result.Add((LineKind.Other, null, null)); continue; }
                result.Add((LineKind.Entry, section, key));
            }
            return result;
        }

        public bool ContainsSection(string section) => Analyze().Any(a => a.Kind == LineKind.Header && a.Section == section);

        private bool LastLineBlank => Lines.Count > 0 && Utf8.GetString(Lines[^1].Content).Trim().Length == 0;

        private void InsertLine(int index, byte[] content)
        {
            var terminator = Encoding.ASCII.GetBytes(CrLf ? "\r\n" : "\n");
            var atEnd = index == Lines.Count;
            if (atEnd && Lines.Count > 0 && Lines[^1].Terminator.Length == 0)
            {
                // The file had no final newline: keep it that way, the new line becomes the unterminated one.
                Lines[^1].Terminator = terminator;
                Lines.Add(new CfgLine(content, Array.Empty<byte>()));
            }
            else
            {
                Lines.Insert(index, new CfgLine(content, terminator));
            }
            Touched = true;
        }

        public void AddEntry(string section, string key, string value)
        {
            var entry = Utf8.GetBytes($"{key}={value}");
            var analysis = Analyze();

            var header = -1;
            for (var i = 0; i < analysis.Count; i++)
                if (analysis[i].Kind == LineKind.Header && analysis[i].Section == section) header = i;

            if (header >= 0)
            {
                var last = header;
                for (var i = header + 1; i < analysis.Count && analysis[i].Kind != LineKind.Header; i++)
                    if (analysis[i].Kind == LineKind.Entry) last = i;
                InsertLine(last + 1, entry);
                return;
            }

            if (Lines.Count > 0 && !LastLineBlank) InsertLine(Lines.Count, Array.Empty<byte>());
            InsertLine(Lines.Count, Utf8.GetBytes($"[{section}]"));
            InsertLine(Lines.Count, entry);
        }
    }

    private static void ValidateChanges(IReadOnlyList<LocaleChange> changes)
    {
        if (changes.Count > MaxChanges) throw new FmsException($"Too many changes in one request (limit {MaxChanges}).");

        foreach (var change in changes)
        {
            if (change is null) throw new FmsException("A change is missing.");
            var section = change.Section;
            var key = change.Key;

            if (string.IsNullOrEmpty(section) || section.IndexOfAny(['[', ']', '=', '\r', '\n']) >= 0 || section[0] is ';' or '#')
                throw new FmsException($"Invalid section name \"{section}\".");
            if (string.IsNullOrEmpty(key) || key.IndexOfAny(['=', '\r', '\n']) >= 0 || key[0] is ';' or '#' or '[' || key.Trim() != key)
                throw new FmsException($"Invalid key \"{key}\" in section [{section}].");
            if (change.Value is { } value)
            {
                if (value.IndexOfAny(['\r', '\n']) >= 0)
                    throw new FmsException($"The value of {section}.{key} contains a line break; write \\n for a new line.");
                if (value.Length > MaxValueLength)
                    throw new FmsException($"The value of {section}.{key} is longer than {MaxValueLength} characters.");
            }
        }
    }

    private static void RequireEditableLanguage(string modDir, string language)
    {
        if (!IsValidLanguageCode(language)) throw new FmsException($"\"{language}\" is not a valid language code.");
        var localeDir = Path.Combine(modDir, "locale");
        var languageDir = Path.Combine(localeDir, language);
        if (!Directory.Exists(languageDir)) throw new FmsException($"The mod has no locale/{language} folder.");
        if ((File.GetAttributes(languageDir) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(localeDir) & FileAttributes.ReparsePoint) != 0)
            throw new FmsException($"locale/{language} is a link and cannot be edited.");
    }

    private static List<CfgFile> LoadFiles(string modDir, string language)
    {
        var languageDir = Path.Combine(modDir, "locale", language);
        var files = new List<CfgFile>();
        foreach (var relative in CfgFiles(languageDir))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path.Combine(languageDir, relative.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FmsException($"Could not read locale/{language}/{relative}: {ex.Message}", ex);
            }

            var hasBom = bytes.Length >= 3 && bytes[0] == Bom[0] && bytes[1] == Bom[1] && bytes[2] == Bom[2];
            var file = new CfgFile(relative) { Exists = true, HasBom = hasBom, OriginalBytes = bytes };

            var start = hasBom ? 3 : 0;
            var position = start;
            while (position < bytes.Length)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n', position);
                if (newline < 0)
                {
                    file.Lines.Add(new CfgLine(bytes[position..], Array.Empty<byte>()));
                    break;
                }

                var crlf = newline > position && bytes[newline - 1] == (byte)'\r';
                var contentEnd = crlf ? newline - 1 : newline;
                file.Lines.Add(new CfgLine(bytes[position..contentEnd], bytes[contentEnd..(newline + 1)]));
                position = newline + 1;
            }

            files.Add(file);
        }
        return files;
    }

    // Edits of one language are applied one at a time (in this process): read, revision check and write form one step.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> ApplyLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static LocaleApplyResult Apply(string modDir, string language, IReadOnlyList<LocaleChange> changes, string expectedRevision)
    {
        RequireEditableLanguage(modDir, language);
        if (changes is null) throw new FmsException("No changes given.");
        ValidateChanges(changes);

        var gate = ApplyLocks.GetOrAdd(Path.GetFullPath(Path.Combine(modDir, "locale", language)), _ => new object());
        lock (gate) return ApplyLocked(modDir, language, changes, expectedRevision);
    }

    /// <summary>No file written by an edit may be reached through a link: a symlinked sub-folder would let a write leave the mod.</summary>
    private static void RequireNoLinks(string languageDir, string relative)
    {
        var current = languageDir;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new FmsException($"locale/{Path.GetFileName(languageDir)}/{relative} is reached through a link and cannot be edited.");
        }
    }

    private static LocaleApplyResult ApplyLocked(string modDir, string language, IReadOnlyList<LocaleChange> changes, string expectedRevision)
    {
        var files = LoadFiles(modDir, language);
        var currentRevision = RevisionOf(files.Select(f => (f.Relative, f.OriginalBytes)));
        if (changes.Count == 0) return new LocaleApplyResult(currentRevision, 0, 0, 0, Array.Empty<string>());

        if (!string.Equals(expectedRevision, currentRevision, StringComparison.OrdinalIgnoreCase))
            throw new LocaleConflictException("The translation files changed on disk since they were loaded.", currentRevision);

        // Same section+key twice: the last one wins.
        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < changes.Count; i++) lastIndex[LocaleLanguage.Id(changes[i].Section, changes[i].Key)] = i;
        var effective = changes.Where((c, i) => lastIndex[LocaleLanguage.Id(c.Section, c.Key)] == i).ToList();

        // First definition of every key: (file, line) in file order.
        var definitions = new Dictionary<string, (CfgFile File, CfgLine Line)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var analysis = file.Analyze();
            for (var i = 0; i < analysis.Count; i++)
                if (analysis[i].Kind == LineKind.Entry) definitions.TryAdd(LocaleLanguage.Id(analysis[i].Section!, analysis[i].Key!), (file, file.Lines[i]));
        }

        LanguageData? sourceData = null;
        string? sourceLanguage = null;
        var byPath = files.ToDictionary(f => f.Relative, StringComparer.Ordinal);
        int added = 0, updated = 0, removed = 0;

        foreach (var change in effective)
        {
            var id = LocaleLanguage.Id(change.Section, change.Key);

            if (definitions.TryGetValue(id, out var existing))
            {
                // A key the studio marked as untranslated: whatever is saved for it now is its translation, so the mark goes.
                var at = existing.File.Lines.IndexOf(existing.Line);
                var unmarked = at > 0 && IsUntranslatedMark(Utf8.GetString(existing.File.Lines[at - 1].Content));
                if (unmarked)
                {
                    existing.File.Lines.RemoveAt(at - 1);
                    existing.File.Touched = true;
                }

                if (change.Value is null)
                {
                    existing.File.Lines.Remove(existing.Line);
                    existing.File.Touched = true;
                    removed++;
                    continue;
                }

                var content = existing.Line.Content;
                var eq = Array.IndexOf(content, (byte)'=');
                var replaced = content[..(eq + 1)].Concat(Utf8.GetBytes(change.Value)).ToArray();
                if (replaced.AsSpan().SequenceEqual(content))
                {
                    if (unmarked) updated++;
                    continue;
                }
                existing.Line.Content = replaced;
                existing.File.Touched = true;
                updated++;
                continue;
            }

            if (change.Value is null) continue;

            if (sourceData is null)
            {
                sourceLanguage = SourceLanguage(modDir)!;
                sourceData = Read(modDir, sourceLanguage);
            }

            string relative;
            if (sourceData.First.TryGetValue(id, out var sourceEntry))
            {
                relative = sourceEntry.File[$"locale/{sourceLanguage}/".Length..];
            }
            else
            {
                relative = files.OrderBy(f => f.Relative, StringComparer.Ordinal).FirstOrDefault(f => f.ContainsSection(change.Section))?.Relative ?? "extra.cfg";
            }

            if (!byPath.TryGetValue(relative, out var target))
            {
                target = new CfgFile(relative) { Exists = false };
                byPath[relative] = target;
                files.Add(target);
            }

            target.AddEntry(change.Section, change.Key, change.Value);
            added++;
        }

        var touched = files.Where(f => f.Touched).OrderBy(f => f.Relative, StringComparer.Ordinal).ToList();
        foreach (var file in touched) RequireNoLinks(Path.Combine(modDir, "locale", language), file.Relative); // before the first write
        foreach (var file in touched)
        {
            var full = Path.Combine(modDir, "locale", language, file.Relative.Replace('/', Path.DirectorySeparatorChar));
            try { WriteAtomically(full, file.Serialize()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FmsException($"Could not write locale/{language}/{file.Relative}: {ex.Message}", ex);
            }
        }

        var revision = RevisionOf(CfgFiles(Path.Combine(modDir, "locale", language))
            .Select(r => (r, File.ReadAllBytes(Path.Combine(modDir, "locale", language, r.Replace('/', Path.DirectorySeparatorChar))))));
        return new LocaleApplyResult(revision, added, updated, removed, touched.Select(f => $"locale/{language}/{f.Relative}").ToList());
    }

    private static void WriteAtomically(string full, byte[] payload)
    {
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.fms-tmp");
        try
        {
            File.WriteAllBytes(temp, payload);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ------------------------------------------------------------------ new languages

    private static readonly object CreateGate = new();

    public static void CreateLanguage(string modDir, string code)
    {
        lock (CreateGate) CreateLanguageLocked(modDir, code);
    }

    private static void CreateLanguageLocked(string modDir, string code)
    {
        if (!IsValidLanguageCode(code)) throw new FmsException($"\"{code}\" is not a valid language code.");
        var source = SourceLanguage(modDir) ?? throw new FmsException("The mod has no locale folder to copy the structure from.");

        var localeDir = Path.Combine(modDir, "locale");
        if (LocaleData.Languages(modDir).Any(l => string.Equals(l, code, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(Path.Combine(localeDir, code)))
            throw new FmsException($"The language \"{code}\" already exists.");

        var stub = Utf8.GetBytes($"; Translation: {code}\n");
        var languageDir = Path.Combine(localeDir, code);
        try
        {
            Directory.CreateDirectory(languageDir);
            foreach (var relative in CfgFiles(Path.Combine(localeDir, source)))
            {
                var full = Path.Combine(languageDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, stub);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(languageDir, recursive: true); } catch (Exception) { /* best effort */ }
            throw new FmsException($"Could not create the language: {ex.Message}", ex);
        }
    }
}
