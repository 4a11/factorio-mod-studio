using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

public enum ModImportError { Invalid, Conflict, TooLarge, UnsupportedType, NotFound }

/// <summary>An import was refused. <see cref="Kind"/> says why; the host maps it to a response.</summary>
public sealed class ModImportException : Exception
{
    public ModImportException(ModImportError kind, string message, string? existingPath = null) : base(message)
    {
        Kind = kind;
        ExistingPath = existingPath;
    }

    public ModImportError Kind { get; }

    /// <summary>For <see cref="ModImportError.Conflict"/>: full path of the folder that is already there.</summary>
    public string? ExistingPath { get; }
}

/// <param name="Folder">Full path of the new mod folder inside the workspace.</param>
public sealed record ModImportResult(string Name, string Version, string Folder, int Files, int Skipped);

/// <summary>
/// Brings a mod into the workspace folder, either from a zip or from files uploaded one by one (a "session").
/// Everything is validated while it is staged below <c>&lt;workspace&gt;/.fmstudio/imports</c>; only a complete, valid mod is
/// moved (one rename) to <c>&lt;workspace&gt;/&lt;mod name&gt;</c>. Nothing is ever overwritten or merged, and a failed import
/// leaves nothing behind.
/// </summary>
public sealed class ModImporter
{
    public const long MaxZipBytes = 200L * 1024 * 1024;
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const long MaxUncompressedBytes = 500L * 1024 * 1024;
    public const int MaxFiles = 20000;
    private const long MaxInfoBytes = 1024 * 1024;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

    public static readonly IReadOnlySet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".lua", ".cfg", ".json", ".txt", ".md", ".csv", ".png", ".jpg", ".jpeg", ".ogg", ".wav", ".mp3", ".flac" };

    private static readonly Regex ModName = new(@"^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"^\d{1,9}(\.\d{1,9}){1,2}$", RegexOptions.Compiled);
    private static readonly Regex SessionId = new(@"^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // Moving into the workspace is serialized process-wide so two commits of one name cannot both pass the existence check.
    private static readonly object CommitGate = new();

    private sealed class Session
    {
        public required string Id { get; init; }
        public required string Dir { get; init; }
        public DateTime Created { get; } = DateTime.UtcNow;
        public readonly object Gate = new();
        public readonly Dictionary<string, (string Key, long Size)> Files = new(StringComparer.OrdinalIgnoreCase);
        public long Bytes;
        public int Active;
    }

    private readonly string _workspace;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeDirs = new(StringComparer.Ordinal);
    private bool _createdWorkspace;

    public ModImporter(string workspaceDirectory)
    {
        _workspace = Path.GetFullPath(workspaceDirectory);
    }

    private string FmsDir => Path.Combine(_workspace, ".fmstudio");
    private string StagingRoot => Path.Combine(FmsDir, "imports");

    // ------------------------------------------------------------------ validation helpers

    /// <summary>Splits a client-supplied relative path into safe segments, or returns null if anything about it is unsafe.</summary>
    public static string[]? NormalizePath(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 1024) return null;
        if (raw[0] == '/' || raw.Contains('\\')) return null;
        if (raw.Any(c => c < ' ' || c == '\u007f')) return null;

        var parts = raw.Split('/');
        foreach (var part in parts)
            if (!IsSafeSegment(part)) return null;
        return parts;
    }

    private static bool IsSafeSegment(string part)
    {
        if (part.Length == 0 || part.Length > 255 || part[0] == '.') return false; // empty, ".", "..", hidden
        if (part.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0) return false; // also rules out "C:"
        if (part[^1] is ' ' or '.') return false;
        var stem = part.Split('.')[0].TrimEnd(' ');
        return !Reserved.Contains(stem);
    }

    public static bool IsAllowedFile(string fileName) => AllowedExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>A zip entry inside a hidden file or folder (a segment like ".git"), but no "..", backslash or other escape attempt.</summary>
    private static bool IsHiddenEntry(string fullName)
    {
        if (fullName.Contains('\\')) return false;
        var segments = fullName.TrimEnd('/').Split('/');
        return !segments.Contains("..") && segments.Any(s => s.Length > 1 && s[0] == '.');
    }

    public static bool IsValidModName(string? name) =>
        name is not null && ModName.IsMatch(name) && !Reserved.Contains(name);

    private static ModImportException Invalid(string message) => new(ModImportError.Invalid, message);

    // ------------------------------------------------------------------ staging area

    private void EnsureStagingRoot()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_workspace))
            {
                Directory.CreateDirectory(_workspace);
                _createdWorkspace = true;
            }

            if (Directory.Exists(FmsDir) && (File.GetAttributes(FmsDir) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("The workspace's .fmstudio folder is a link; refusing to import.");
            Directory.CreateDirectory(StagingRoot);
        }
    }

    private string NewStagingDir(string name)
    {
        lock (_gate)
        {
            EnsureStagingRoot();
            var dir = Path.Combine(StagingRoot, name);
            Directory.CreateDirectory(dir);
            _activeDirs.Add(name);
            return dir;
        }
    }

    private void Discard(string name, string dir)
    {
        lock (_gate)
        {
            TryDelete(dir);
            _activeDirs.Remove(name);
            Tidy();
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cleaned up lazily next time */ }
    }

    /// <summary>Removes the hidden folders (and a workspace we created) again when nothing is staged any more.</summary>
    private void Tidy()
    {
        try
        {
            if (_activeDirs.Count > 0) return;
            if (Directory.Exists(StagingRoot) && !Directory.EnumerateFileSystemEntries(StagingRoot).Any()) Directory.Delete(StagingRoot);
            if (Directory.Exists(FmsDir) && !Directory.EnumerateFileSystemEntries(FmsDir).Any()) Directory.Delete(FmsDir);
            if (_createdWorkspace && Directory.Exists(_workspace) && !Directory.EnumerateFileSystemEntries(_workspace).Any())
            {
                Directory.Delete(_workspace);
                _createdWorkspace = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>Drops sessions older than an hour and staging folders nobody owns (left over from a crash).</summary>
    public void CleanStale()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values.Where(s => DateTime.UtcNow - s.Created > SessionLifetime).ToList())
            {
                _sessions.Remove(session.Id);
                _activeDirs.Remove(session.Id);
                TryDelete(session.Dir);
            }

            if (Directory.Exists(StagingRoot))
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
                        if (!_activeDirs.Contains(Path.GetFileName(dir))) TryDelete(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            }

            Tidy();
        }
    }

    // ------------------------------------------------------------------ zip

    /// <summary>Imports a mod from a zip (mod at the root or inside exactly one top-level folder).</summary>
    public ModImportResult ImportZip(Stream zipStream)
    {
        CleanStale();
        var name = "zip-" + Guid.NewGuid().ToString("N");
        var staging = NewStagingDir(name);
        try
        {
            var (files, skipped) = ExtractZip(zipStream, staging);
            return Finalize(staging, files, skipped);
        }
        finally
        {
            Discard(name, staging);
        }
    }

    private static (int Files, int Skipped) ExtractZip(Stream zipStream, string staging)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            throw Invalid("The file is not a valid zip archive.");
        }

        using (zip)
        {
            try
            {
                var entries = zip.Entries.ToList();
                if (entries.Count(e => !e.FullName.EndsWith('/')) > MaxFiles)
                    throw Invalid($"The archive has more than {MaxFiles} files.");

                var stagingPrefix = Path.TrimEndingDirectorySeparator(staging) + Path.DirectorySeparatorChar;
                long total = 0;
                int files = 0, skipped = 0;
                var buffer = new byte[81920];

                foreach (var entry in entries)
                {
                    var fullName = entry.FullName;
                    var isDirectory = fullName.EndsWith('/');
                    // Hidden files and folders (.gitignore, .DS_Store, .github/...) are skipped like in an uploaded folder;
                    // skipping writes nothing. Paths that try to leave the mod are still rejected below.
                    if (fullName.StartsWith("__MACOSX/", StringComparison.Ordinal) || IsHiddenEntry(fullName))
                    {
                        if (!isDirectory) skipped++;
                        continue;
                    }

                    var segments = NormalizePath(isDirectory ? fullName[..^1] : fullName)
                                   ?? throw Invalid($"Unsafe path in the archive: \"{Printable(fullName)}\".");
                    var destination = Path.GetFullPath(Path.Combine(new[] { staging }.Concat(segments).ToArray()));
                    if (!destination.StartsWith(stagingPrefix, StringComparison.Ordinal))
                        throw Invalid($"Unsafe path in the archive: \"{Printable(fullName)}\".");

                    if (isDirectory)
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    var attributes = entry.ExternalAttributes;
                    var isLink = ((attributes >> 16) & 0xF000) == 0xA000 || (attributes & 0x400) != 0; // unix symlink or Windows reparse point
                    if (isLink || !IsAllowedFile(segments[^1]))
                    {
                        skipped++;
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    try
                    {
                        using var source = entry.Open();
                        using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read; // counted while extracting; the sizes in the headers are not trusted
                            if (total > MaxUncompressedBytes)
                                throw Invalid($"The archive is larger than {MaxUncompressedBytes / 1024 / 1024} MB when unpacked.");
                            target.Write(buffer, 0, read);
                        }
                    }
                    catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
                    {
                        throw Invalid($"Conflicting entries in the archive at \"{Printable(fullName)}\".");
                    }

                    files++;
                }

                return (files, skipped);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or UnauthorizedAccessException or IOException)
            {
                throw Invalid("The archive is damaged or uses an unsupported feature.");
            }
        }
    }

    private static string Printable(string s) => new(s.Take(80).Select(c => c < ' ' ? '?' : c).ToArray());

    // ------------------------------------------------------------------ upload sessions

    public string CreateSession()
    {
        CleanStale();
        var id = Guid.NewGuid().ToString("N");
        var dir = NewStagingDir(id);
        lock (_gate) _sessions[id] = new Session { Id = id, Dir = dir };
        return id;
    }

    private Session GetSession(string id)
    {
        lock (_gate)
            return SessionId.IsMatch(id) && _sessions.TryGetValue(id, out var s)
                ? s
                : throw new ModImportException(ModImportError.NotFound, "Unknown import.");
    }

    /// <summary>Stores one uploaded file in the session (replacing an earlier upload of the same path).</summary>
    public async Task AddFileAsync(string id, string? relativePath, Stream body, CancellationToken ct = default)
    {
        var session = GetSession(id);
        var segments = NormalizePath(relativePath) ?? throw Invalid("Invalid file path.");
        if (!IsAllowedFile(segments[^1]))
            throw new ModImportException(ModImportError.UnsupportedType, $"Files of type \"{Path.GetExtension(segments[^1])}\" are not allowed in a mod.");

        var key = string.Join('/', segments);
        long? previous;
        lock (session.Gate)
        {
            previous = null;
            if (session.Files.TryGetValue(key, out var p))
            {
                if (p.Key != key) throw Invalid("Two files differ only in letter case.");
                previous = p.Size;
            }
            if (previous is null && session.Files.Count >= MaxFiles) throw Invalid($"More than {MaxFiles} files.");
            session.Active++;
        }

        var destination = Path.Combine(new[] { session.Dir }.Concat(segments).ToArray());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            long size = 0;
            try
            {
                await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await body.ReadAsync(buffer, ct)) > 0)
                    {
                        size += read;
                        if (size > MaxFileBytes)
                            throw new ModImportException(ModImportError.TooLarge, $"A file is larger than {MaxFileBytes / 1024 / 1024} MB.");
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }

                lock (session.Gate)
                {
                    var bytes = session.Bytes - (previous ?? 0) + size;
                    if (bytes > MaxUncompressedBytes) throw Invalid($"The mod is larger than {MaxUncompressedBytes / 1024 / 1024} MB.");
                    session.Bytes = bytes;
                    session.Files[key] = (key, size);
                }
            }
            catch
            {
                try { if (previous is null) File.Delete(destination); } catch (IOException) { /* staging is discarded anyway */ }
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Invalid("The file conflicts with another uploaded path.");
        }
        finally
        {
            lock (session.Gate) session.Active--;
        }
    }

    /// <summary>Validates the staged files and moves them into the workspace. The session ends either way.</summary>
    public ModImportResult Commit(string id)
    {
        var session = GetSession(id);
        lock (session.Gate)
            if (session.Active > 0) throw new ModImportException(ModImportError.Conflict, "Uploads are still in progress.");

        lock (_gate)
        {
            if (!_sessions.Remove(id)) throw new ModImportException(ModImportError.NotFound, "Unknown import.");
        }

        try
        {
            int count;
            lock (session.Gate) count = session.Files.Count;
            // Concurrent uploads can slip past the per-file check, so the limit is enforced here as well.
            if (count > MaxFiles) throw Invalid($"The mod has more than {MaxFiles} files.");
            return Finalize(session.Dir, count, 0);
        }
        finally
        {
            Discard(id, session.Dir);
        }
    }

    public void Cancel(string id)
    {
        var session = GetSession(id);
        lock (_gate) _sessions.Remove(id);
        Discard(id, session.Dir);
    }

    // ------------------------------------------------------------------ validation + move

    private ModImportResult Finalize(string staging, int files, int skipped)
    {
        if (files == 0) throw Invalid("The upload contains no usable files.");

        var root = LocateRoot(staging);
        var (name, version) = ReadInfo(Path.Combine(root, "info.json"));
        var target = Path.Combine(_workspace, name);

        lock (CommitGate)
        {
            if (FindExisting(name) is { } existing)
                throw new ModImportException(ModImportError.Conflict, $"A mod folder named \"{name}\" already exists in the workspace.", existing);

            try
            {
                Directory.Move(root, target);
            }
            catch (IOException) when (Directory.Exists(target))
            {
                throw new ModImportException(ModImportError.Conflict, $"A mod folder named \"{name}\" already exists in the workspace.", target);
            }
        }

        return new ModImportResult(name, version, target, files, skipped);
    }

    private string? FindExisting(string name)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_workspace))
            if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) return path;
        return null;
    }

    private static string LocateRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "info.json"))) return staging;

        if (!Directory.EnumerateFiles(staging).Any())
        {
            var dirs = Directory.GetDirectories(staging);
            if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "info.json"))) return dirs[0];
            if (dirs.Length > 1)
                throw Invalid("The mod must be at the top level or inside exactly one top-level folder, with an info.json.");
        }

        throw Invalid("No info.json found at the top level of the mod.");
    }

    private static (string Name, string Version) ReadInfo(string path)
    {
        if (new FileInfo(path).Length > MaxInfoBytes) throw Invalid("info.json is too large.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("info.json must contain a JSON object.");

            var name = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var version = doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            if (!IsValidModName(name))
                throw Invalid("info.json needs a \"name\" of up to 100 letters, digits, \"_\" or \"-\" (no spaces or dots).");
            if (version is null || !VersionPattern.IsMatch(version))
                throw Invalid("info.json needs a \"version\" such as \"1.0.0\".");
            return (name!, version);
        }
        catch (JsonException)
        {
            throw Invalid("info.json is not valid JSON.");
        }
    }
}
