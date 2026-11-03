using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FactorioModStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FactorioModStudio.Server;

/// <summary>
/// <c>GET /api/mods/{id}/search</c>: plain-text or regular-expression search across the text files of one mod.
/// Read-only and bounded: query length, file size, number of files, number of matches, per-match regex timeout and an overall
/// time budget are limited, hidden folders and symbolic links / junctions are never followed, and every file is read through
/// a path that is checked to stay inside the mod folder.
/// </summary>
internal static class ModSearch
{
    internal const int MaxQueryLength = 200;
    internal const int MaxMatches = 500;
    internal const int MaxFilesWithMatches = 100;
    internal const int MaxFilesScanned = 2000;
    internal const int MaxEnumerated = 50000;
    internal const long MaxFileBytes = 2 * 1024 * 1024;
    internal const int MaxLineText = 300;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private sealed record Match(int Line, int Column, int Length, string Text);

    public static void Map(WebApplication app, ModCatalog catalog)
    {
        app.MapGet("/api/mods/{id}/search", (HttpContext http, string id) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            var entry = catalog.Find(id);
            if (entry is null) return Fail(StatusCodes.Status404NotFound, "Unknown mod.");

            var query = http.Request.Query["q"].ToString();
            if (query.Length == 0) return Fail(StatusCodes.Status400BadRequest, "The search text must not be empty.");
            if (query.Length > MaxQueryLength) return Fail(StatusCodes.Status400BadRequest, $"The search text is too long (limit {MaxQueryLength} characters).");

            var useRegex = Flag(http.Request.Query["regex"].ToString());
            var caseSensitive = Flag(http.Request.Query["case"].ToString());

            Regex? regex = null;
            if (useRegex)
            {
                try { regex = new Regex(query, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, RegexTimeout); }
                catch (ArgumentException ex) { return Fail(StatusCodes.Status400BadRequest, "Invalid regular expression: " + ex.Message); }
            }

            string root;
            try { root = Path.GetFullPath(catalog.Materialize(entry)); }
            catch (FmsException ex) { return Fail(StatusCodes.Status422UnprocessableEntity, ex.Message); }

            return Results.Json(Run(root, query, regex, caseSensitive, http.RequestAborted));
        });
    }

    private static bool Flag(string value) => value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static IResult Fail(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    internal static object Run(string root, string query, Regex? regex, bool caseSensitive, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var truncated = false;
        var skipped = 0;

        var candidates = Enumerate(root, ref truncated);
        candidates.Sort(StringComparer.Ordinal);

        var files = new List<object>();
        var total = 0;
        var scanned = 0;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var relative in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            if (clock.Elapsed > Budget) { truncated = true; break; }
            if (scanned >= MaxFilesScanned) { truncated = true; break; }
            scanned++;

            var lines = ReadLines(root, relative);
            if (lines is null) { skipped++; continue; }

            var matches = new List<Match>();
            var stop = false;
            for (var i = 0; i < lines.Length && !stop; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                if (clock.Elapsed > Budget) { truncated = true; stop = true; break; }

                try
                {
                    foreach (var (column, length) in regex is null ? Plain(line, query, comparison) : Pattern(line, regex))
                    {
                        if (total + matches.Count >= MaxMatches) { truncated = true; stop = true; break; }
                        matches.Add(new Match(i + 1, column + 1, length, Shorten(line)));
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    truncated = true;
                    stop = true;
                }
            }

            if (matches.Count > 0)
            {
                if (files.Count >= MaxFilesWithMatches) { truncated = true; break; }
                files.Add(new { path = relative, matches = matches.Select(m => new { line = m.Line, column = m.Column, length = m.Length, text = m.Text }).ToList() });
                total += matches.Count;
            }

            if (stop) break;
        }

        return new { query, files, totalMatches = total, truncated, skippedFiles = skipped };
    }

    private static IEnumerable<(int Column, int Length)> Plain(string line, string query, StringComparison comparison)
    {
        for (var at = line.IndexOf(query, 0, comparison); at >= 0; at = at + query.Length >= line.Length ? -1 : line.IndexOf(query, at + query.Length, comparison))
            yield return (at, query.Length);
    }

    private static IEnumerable<(int Column, int Length)> Pattern(string line, Regex regex)
    {
        for (var m = regex.Match(line); m.Success; m = m.NextMatch())
            if (m.Length > 0) yield return (m.Index, m.Length); // empty matches (e.g. "x*") carry no information
    }

    private static string Shorten(string line)
    {
        if (line.Length <= MaxLineText) return line;
        var end = char.IsHighSurrogate(line[MaxLineText - 1]) ? MaxLineText - 1 : MaxLineText;
        return line[..end];
    }

    /// <summary>Relative '/' paths of searchable text files: no hidden segments, no reparse points, bounded enumeration.</summary>
    private static List<string> Enumerate(string root, ref bool truncated)
    {
        var result = new List<string>();
        var seen = 0;
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((root, ""));

        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Length == 0 || name.StartsWith('.')) continue;
                if (++seen > MaxEnumerated) { truncated = true; return result; }

                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                var relative = prefix.Length == 0 ? name : prefix + "/" + name;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((child, relative));
                else if (UiServer.TextExtensions.Contains(Path.GetExtension(name))) result.Add(relative);
            }
        }

        return result;
    }

    /// <summary>The lines of a valid UTF-8 text file without NUL bytes and within the size limit; otherwise null (counted as skipped).</summary>
    private static string[]? ReadLines(string root, string relative)
    {
        var full = UiServer.ResolveInside(root, relative);
        if (full is null) return null;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxFileBytes) return null;

            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }
}
