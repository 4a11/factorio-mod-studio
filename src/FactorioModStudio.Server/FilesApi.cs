using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactorioModStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace FactorioModStudio.Server;

/// <summary>Starts the OS file manager. Tests replace it so no real Explorer / Finder window opens.</summary>
/// <param name="executable">"explorer.exe", "open" or "xdg-open".</param>
/// <param name="arguments">Passed one by one (never through a shell or a single command line).</param>
public delegate void FileManagerLauncher(string executable, IReadOnlyList<string> arguments);

/// <summary>
/// File management endpoints of the Files tab: folders, move / rename, copy, delete into a per-mod trash with undo, binary asset
/// upload and "show in file manager".
/// Every write passes the session-token and same-origin check in <c>UiServer.MapSecurity</c>, works only on editable mods and sends
/// each client path through <see cref="UiServer.ResolveInside"/> and <see cref="UiServer.IsWritablePath"/>.
/// </summary>
internal static class FilesApi
{
    private const long MaxBodyBytes = 16 * 1024;
    private const long MaxAssetBytes = 32 * 1024 * 1024;
    private const int TrashKeep = 20;
    private static readonly TimeSpan TrashMaxAge = TimeSpan.FromDays(7);
    private const string ManifestName = "manifest.json";
    private const string ContentFolder = "content";
    private const string ReadOnlyMessage = "This mod is read-only. Only unpacked mods in the workspace folder and the project mod can be edited.";

    private static readonly Regex TrashIdPattern = new(@"^[0-9]{17}-[0-9a-f]{8}$", RegexOptions.Compiled);
    private static readonly Regex UnsafeIdChars = new(@"[^A-Za-z0-9_-]", RegexOptions.Compiled);

    /// <summary>Uploadable binary assets and the leading bytes each must start with.</summary>
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".ogg", ".wav" };

    private static bool CaseInsensitiveFs => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private static StringComparison PathComparison => CaseInsensitiveFs ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record TrashManifest(string TrashId, string Path, string Kind, DateTime DeletedUtc);

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static string Relative(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    public static void Map(WebApplication app, ModCatalog catalog, ProjectContext context, FileManagerLauncher? launcher)
    {
        launcher ??= StartProcess;
        var trashRoot = Path.Combine(catalog.WorkspaceDirectory ?? context.Root, ".fmstudio", "trash");

        // Resolves the mod for a write: unknown id -> 404, read-only -> 403.
        IResult? Editable(string id, out CatalogEntry entry, out string root)
        {
            entry = catalog.Find(id)!;
            root = "";
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (!entry.Editable) return Error(StatusCodes.Status403Forbidden, ReadOnlyMessage);
            root = Path.GetFullPath(entry.Path);
            return null;
        }

        static string? Writable(string root, string? relative)
        {
            if (relative is null) return null;
            var full = UiServer.ResolveInside(root, relative);
            return full is not null && UiServer.IsWritablePath(root, full) ? full : null;
        }

        string TrashDirFor(string id) => Path.Combine(trashRoot, UnsafeIdChars.Replace(id, "_"));

        // ------------------------------------------------------------ folders, move, copy

        app.MapPost("/api/mods/{id}/fs/mkdir", (string id, string? path) =>
        {
            if (Editable(id, out _, out var root) is { } denied) return denied;
            var full = Writable(root, path);
            if (full is null) return Error(StatusCodes.Status400BadRequest, "Invalid path.");
            if (File.Exists(full) || Directory.Exists(full)) return Error(StatusCodes.Status409Conflict, "A file or folder with this name already exists.");

            try { Directory.CreateDirectory(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not create the folder: {ex.Message}");
            }
            return Results.Json(new { path = Relative(root, full) });
        });

        app.MapPost("/api/mods/{id}/fs/move", async (HttpContext http, string id) =>
        {
            if (Editable(id, out _, out var root) is { } denied) return denied;
            var (body, bad) = await ReadFromTo(http);
            if (bad is not null) return bad;

            var from = Writable(root, body!.Value.From);
            var to = Writable(root, body.Value.To);
            if (from is null || to is null) return Error(StatusCodes.Status400BadRequest, "Invalid path.");

            var caseOnly = false;
            if (string.Equals(from, to, StringComparison.Ordinal)) return Error(StatusCodes.Status400BadRequest, "Source and target are the same.");
            if (string.Equals(from, to, PathComparison)) caseOnly = true; // a case-only rename on a case-insensitive file system
            else if (to.StartsWith(Path.TrimEndingDirectorySeparator(from) + Path.DirectorySeparatorChar, PathComparison))
                return Error(StatusCodes.Status400BadRequest, "A folder cannot be moved into itself.");

            var isFile = File.Exists(from);
            if (!isFile && !Directory.Exists(from)) return Error(StatusCodes.Status404NotFound, "File or folder not found.");
            if (!caseOnly && (File.Exists(to) || Directory.Exists(to))) return Error(StatusCodes.Status409Conflict, "The target already exists.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (caseOnly)
                {
                    // Some file systems refuse "a" -> "A" in one step; go through a temporary name next to it.
                    var temp = Path.Combine(Path.GetDirectoryName(from)!, $".fms-rename-{Guid.NewGuid():N}");
                    MoveEntry(from, temp, isFile);
                    MoveEntry(temp, to, isFile);
                }
                else
                {
                    MoveEntry(from, to, isFile);
                }
            }
            catch (FolderLinkException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not move: {ex.Message}");
            }
            return Results.Json(new { from = Relative(root, from), to = Relative(root, to) });
        });

        app.MapPost("/api/mods/{id}/fs/copy", async (HttpContext http, string id) =>
        {
            if (Editable(id, out _, out var root) is { } denied) return denied;
            var (body, bad) = await ReadFromTo(http);
            if (bad is not null) return bad;

            var from = Writable(root, body!.Value.From);
            var to = Writable(root, body.Value.To);
            if (from is null || to is null) return Error(StatusCodes.Status400BadRequest, "Invalid path.");
            if (Directory.Exists(from)) return Error(StatusCodes.Status400BadRequest, "Only files can be copied.");
            if (!File.Exists(from)) return Error(StatusCodes.Status404NotFound, "File not found.");
            if (File.Exists(to) || Directory.Exists(to)) return Error(StatusCodes.Status409Conflict, "The target already exists.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not copy: {ex.Message}");
            }
            return Results.Json(new { from = Relative(root, from), to = Relative(root, to) });
        });

        // ------------------------------------------------------------ delete into the trash, list, restore

        app.MapDelete("/api/mods/{id}/file", (string id, string? path) =>
        {
            if (Editable(id, out _, out var root) is { } denied) return denied;
            var full = Writable(root, path);
            if (full is null) return Error(StatusCodes.Status400BadRequest, "Invalid path.");

            var isFile = File.Exists(full);
            if (!isFile && !Directory.Exists(full)) return Error(StatusCodes.Status404NotFound, "File or folder not found.");

            // The trash lives outside the mod; never write through a link that someone put on the way to it.
            if (!IsPlainTrashPath(trashRoot, TrashDirFor(id)))
                return Error(StatusCodes.Status500InternalServerError, "The trash folder is a link; refusing to delete.");

            var trashId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
            var entryDir = Path.Combine(TrashDirFor(id), trashId);
            try
            {
                Directory.CreateDirectory(Path.Combine(entryDir, ContentFolder));
                var manifest = new TrashManifest(trashId, Relative(root, full), isFile ? "file" : "folder", DateTime.UtcNow);
                File.WriteAllText(Path.Combine(entryDir, ManifestName), JsonSerializer.Serialize(manifest, ManifestJson));
                MoveEntry(full, Path.Combine(entryDir, ContentFolder, Path.GetFileName(full)), isFile);
            }
            catch (FolderLinkException ex)
            {
                TryDeleteDirectory(entryDir);
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteDirectory(entryDir);
                return Error(StatusCodes.Status500InternalServerError, $"Could not delete: {ex.Message}");
            }

            PurgeTrash(TrashDirFor(id));
            return Results.Json(new { trashId });
        });

        app.MapGet("/api/mods/{id}/trash", (HttpContext http, string id) =>
        {
            if (catalog.Find(id) is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(ReadTrash(TrashDirFor(id)).Select(e => new { trashId = e.TrashId, path = e.Path, kind = e.Kind, deletedUtc = e.DeletedUtc }));
        });

        app.MapPost("/api/mods/{id}/trash/{trashId}/restore", (string id, string trashId) =>
        {
            if (Editable(id, out _, out var root) is { } denied) return denied;
            if (!TrashIdPattern.IsMatch(trashId)) return Error(StatusCodes.Status404NotFound, "Unknown trash entry.");

            var entryDir = Path.Combine(TrashDirFor(id), trashId);
            var manifest = ReadManifest(entryDir);
            if (manifest is null || manifest.TrashId != trashId) return Error(StatusCodes.Status404NotFound, "Unknown trash entry.");

            var full = Writable(root, manifest.Path);
            if (full is null) return Error(StatusCodes.Status400BadRequest, "Invalid path.");
            if (File.Exists(full) || Directory.Exists(full)) return Error(StatusCodes.Status409Conflict, "Something already exists at the original location.");

            var isFile = manifest.Kind == "file";
            var stored = Path.Combine(entryDir, ContentFolder, Path.GetFileName(full));
            if (isFile ? !File.Exists(stored) : !Directory.Exists(stored)) return Error(StatusCodes.Status404NotFound, "The trash entry is incomplete.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                MoveEntry(stored, full, isFile);
            }
            catch (FolderLinkException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not restore: {ex.Message}");
            }

            TryDeleteDirectory(entryDir);
            return Results.Json(new { path = Relative(root, full) });
        });

        // ------------------------------------------------------------ binary assets

        app.MapPut("/api/mods/{id}/asset", async (HttpContext http, string id, string? path) =>
        {
            if (Editable(id, out _, out var root) is { } denied) return denied;
            var full = Writable(root, path);
            if (full is null) return Error(StatusCodes.Status400BadRequest, "Invalid path.");
            var extension = Path.GetExtension(full);
            if (!AssetExtensions.Contains(extension))
                return Error(StatusCodes.Status415UnsupportedMediaType, "Only .png, .jpg, .jpeg, .ogg and .wav files can be uploaded.");
            if (http.Request.ContentLength > MaxAssetBytes) return Error(StatusCodes.Status413PayloadTooLarge, "The file is larger than 32 MB.");

            // Kestrel's default body limit (about 28.6 MB) is below ours; ReadLimited enforces the real one.
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxAssetBytes + 1;
            byte[] body;
            try { body = await UiServer.ReadLimited(http.Request.Body, MaxAssetBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The file is larger than 32 MB."); }
            catch (BadHttpRequestException) { return Error(StatusCodes.Status413PayloadTooLarge, "The file is larger than 32 MB."); }

            if (!MagicMatches(extension, body))
                return Error(StatusCodes.Status400BadRequest, $"The content is not a {extension.TrimStart('.').ToUpperInvariant()} file.");

            // Same optimistic concurrency as PUT /file: overwrite only the known version, create only if nothing is there.
            var exists = File.Exists(full);
            if (Directory.Exists(full)) return Error(StatusCodes.Status409Conflict, "A folder with this name exists.");
            if (exists)
            {
                var ifMatch = http.Request.Headers.IfMatch.ToString();
                if (ifMatch.Length == 0) return Error(StatusCodes.Status428PreconditionRequired, "If-Match header required to overwrite a file.");
                var current = UiServer.ETagOf(full);
                if (ifMatch != current)
                    return Results.Json(new { error = "The file changed on disk since you opened it.", etag = current }, statusCode: StatusCodes.Status409Conflict);
            }
            else if (http.Request.Headers.IfNoneMatch.ToString() != "*")
            {
                return Error(StatusCodes.Status428PreconditionRequired, "The file does not exist; send If-None-Match: * to create it.");
            }

            var directory = Path.GetDirectoryName(full)!;
            var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.fms-tmp");
            try
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(temp, body, http.RequestAborted);
                File.Move(temp, full, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not write the file: {ex.Message}");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            var etag = UiServer.ETagOf(full);
            http.Response.Headers.ETag = etag;
            return Results.Json(new { etag, size = new FileInfo(full).Length });
        });

        // ------------------------------------------------------------ show in the OS file manager (read-only mods too)

        app.MapPost("/api/mods/{id}/reveal", (string id, string? path) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");

            string target;
            bool isFile;
            if (entry.Kind == "zip")
            {
                target = Path.GetFullPath(entry.Path); // the files only exist inside the archive
                isFile = true;
            }
            else
            {
                var root = Path.GetFullPath(entry.Path);
                if (string.IsNullOrEmpty(path)) target = root;
                else if (UiServer.ResolveInside(root, path) is { } full) target = full;
                else return Error(StatusCodes.Status400BadRequest, "Invalid path.");
                isFile = File.Exists(target);
                if (!isFile && !Directory.Exists(target)) return Error(StatusCodes.Status404NotFound, "File or folder not found.");
            }

            try
            {
                if (OperatingSystem.IsWindows()) launcher("explorer.exe", isFile ? new[] { $"/select,{target}" } : new[] { target });
                else if (OperatingSystem.IsMacOS()) launcher("open", new[] { "-R", target });
                else launcher("xdg-open", new[] { isFile ? Path.GetDirectoryName(target)! : target });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not open the file manager: {ex.Message}");
            }
            return Results.NoContent();
        });

        // ------------------------------------------------------------ delete a whole mod of the workspace, with undo

        // Deleted mods wait in a trash of their own next to the per-mod ones ("~" never appears in a sanitized mod id), with the
        // same limits: the newest 20, for 7 days. Only mods in the workspace folder: the project mod is the project itself, and
        // mods of the game or of the Factorio mods folder are not the studio's to remove.
        var modsTrash = Path.Combine(trashRoot, "~mods");

        string? WorkspaceRoot() =>
            catalog.WorkspaceDirectory is { } workspace ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) : null;

        app.MapDelete("/api/mods/{id}", (string id) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            var workspace = WorkspaceRoot();
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Path));
            if (entry.Source != "workspace" || workspace is null
                || !string.Equals(Path.GetDirectoryName(full), workspace, PathComparison))
                return Error(StatusCodes.Status403Forbidden, "Only mods in the workspace folder can be deleted.");

            var isFile = entry.Kind == "zip";
            if (isFile ? !File.Exists(full) : !Directory.Exists(full)) return Error(StatusCodes.Status404NotFound, "The mod is no longer there.");
            if (!IsPlainTrashPath(trashRoot, modsTrash))
                return Error(StatusCodes.Status500InternalServerError, "The trash folder is a link; refusing to delete.");

            var trashId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
            var entryDir = Path.Combine(modsTrash, trashId);
            try
            {
                Directory.CreateDirectory(Path.Combine(entryDir, ContentFolder));
                var manifest = new TrashManifest(trashId, Path.GetFileName(full), isFile ? "file" : "folder", DateTime.UtcNow);
                File.WriteAllText(Path.Combine(entryDir, ManifestName), JsonSerializer.Serialize(manifest, ManifestJson));
                MoveEntry(full, Path.Combine(entryDir, ContentFolder, Path.GetFileName(full)), isFile);
            }
            catch (FolderLinkException ex)
            {
                TryDeleteDirectory(entryDir);
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteDirectory(entryDir);
                return Error(StatusCodes.Status500InternalServerError, $"Could not delete: {ex.Message}");
            }

            PurgeTrash(modsTrash);
            return Results.Json(new { trashId });
        });

        app.MapPost("/api/workspace/trash/{trashId}/restore", (string trashId) =>
        {
            var workspace = WorkspaceRoot();
            if (workspace is null) return Error(StatusCodes.Status404NotFound, "There is no workspace folder.");
            if (!TrashIdPattern.IsMatch(trashId)) return Error(StatusCodes.Status404NotFound, "Unknown trash entry.");

            var entryDir = Path.Combine(modsTrash, trashId);
            var manifest = ReadManifest(entryDir);
            // The manifest names one entry of the workspace folder, nothing deeper.
            if (manifest is null || manifest.TrashId != trashId || manifest.Path is "" or "." or ".."
                || manifest.Path != Path.GetFileName(manifest.Path) || manifest.Path.IndexOfAny(['/', '\\']) >= 0)
                return Error(StatusCodes.Status404NotFound, "Unknown trash entry.");

            var full = Path.Combine(workspace, manifest.Path);
            if (File.Exists(full) || Directory.Exists(full)) return Error(StatusCodes.Status409Conflict, "Something already exists at the original location.");

            var isFile = manifest.Kind == "file";
            var stored = Path.Combine(entryDir, ContentFolder, manifest.Path);
            if (isFile ? !File.Exists(stored) : !Directory.Exists(stored)) return Error(StatusCodes.Status404NotFound, "The trash entry is incomplete.");

            try { MoveEntry(stored, full, isFile); }
            catch (FolderLinkException ex) { return Error(StatusCodes.Status400BadRequest, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not restore: {ex.Message}");
            }

            TryDeleteDirectory(entryDir);
            return Results.Json(new { name = manifest.Path });
        });
    }

    /// <summary>False when .fmstudio, trash or the mod's trash folder (whichever exist) is a symbolic link or junction.</summary>
    private static bool IsPlainTrashPath(string trashRoot, string modTrash)
    {
        foreach (var path in new[] { Path.GetDirectoryName(trashRoot)!, trashRoot, modTrash })
        {
            try
            {
                if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        return true;
    }

    private static void StartProcess(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var _ = Process.Start(info);
    }

    private static async Task<((string? From, string? To)? Body, IResult? Error)> ReadFromTo(HttpContext http)
    {
        byte[] body;
        try { body = await UiServer.ReadLimited(http.Request.Body, MaxBodyBytes, http.RequestAborted); }
        catch (InvalidDataException) { return (null, Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 16 KB).")); }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.String)
                return (null, Error(StatusCodes.Status400BadRequest, "The body must be an object with string \"from\" and \"to\"."));
            return ((from.GetString(), to.GetString()), null);
        }
        catch (JsonException) { return (null, Error(StatusCodes.Status400BadRequest, "The body is not valid JSON.")); }
    }

    private static bool MagicMatches(string extension, byte[] b)
    {
        static bool StartsWith(byte[] data, int offset, params byte[] expected) =>
            data.Length >= offset + expected.Length && data.AsSpan(offset, expected.Length).SequenceEqual(expected);

        return extension.ToLowerInvariant() switch
        {
            ".png" => StartsWith(b, 0, 0x89, 0x50, 0x4E, 0x47),
            ".jpg" or ".jpeg" => StartsWith(b, 0, 0xFF, 0xD8, 0xFF),
            ".ogg" => StartsWith(b, 0, (byte)'O', (byte)'g', (byte)'g', (byte)'S'),
            ".wav" => StartsWith(b, 0, (byte)'R', (byte)'I', (byte)'F', (byte)'F') && StartsWith(b, 8, (byte)'W', (byte)'A', (byte)'V', (byte)'E'),
            _ => false
        };
    }

    /// <summary>A folder that would have to be copied (cross-volume move) contains a symbolic link or junction.</summary>
    internal sealed class FolderLinkException : Exception
    {
        public FolderLinkException() : base("The folder contains a symbolic link or junction.") { }
    }

    /// <summary>
    /// Moves a file or folder; across volumes (project mod and workspace on different drives) a folder is copied and then deleted.
    /// The copy never follows links: a folder containing a symbolic link or junction is refused with <see cref="FolderLinkException"/>
    /// before anything is touched. <paramref name="crossVolume"/> forces the copy path (tests); null decides by the path roots.
    /// </summary>
    internal static void MoveEntry(string from, string to, bool isFile, bool? crossVolume = null)
    {
        if (isFile)
        {
            File.Move(from, to); // File.Move already copies across volumes
            return;
        }

        var differentRoots = crossVolume ?? !string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
        if (!differentRoots)
        {
            Directory.Move(from, to);
            return;
        }

        if (ContainsLink(from)) throw new FolderLinkException();
        if (crossVolume is null)
        {
            try { Directory.Move(from, to); return; }
            catch (IOException) { /* really another volume: copy below */ }
        }
        CopyDirectory(from, to);
        Directory.Delete(from, recursive: true);
    }

    /// <summary>True when the folder itself or anything below it is a reparse point (symbolic link, junction). Never follows links.</summary>
    internal static bool ContainsLink(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (entry is DirectoryInfo sub && ContainsLink(sub.FullName)) return true;
        }
        return false;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static TrashManifest? ReadManifest(string entryDir)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<TrashManifest>(File.ReadAllText(Path.Combine(entryDir, ManifestName)), ManifestJson);
            return manifest is { TrashId: not null, Path: not null, Kind: "file" or "folder" } ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Entries of one mod's trash, newest first. Unreadable entries are skipped.</summary>
    private static List<TrashManifest> ReadTrash(string modTrash)
    {
        if (!Directory.Exists(modTrash)) return new List<TrashManifest>();
        return Directory.EnumerateDirectories(modTrash)
            .Where(d => TrashIdPattern.IsMatch(Path.GetFileName(d)))
            .Select(d => (Folder: Path.GetFileName(d), Manifest: ReadManifest(d)))
            .Where(x => x.Manifest is not null && x.Manifest.TrashId == x.Folder) // the folder name is what purge and restore act on
            .Select(x => x.Manifest!)
            .OrderByDescending(m => m.DeletedUtc)
            .ThenByDescending(m => m.TrashId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Keeps the newest <see cref="TrashKeep"/> entries that are younger than <see cref="TrashMaxAge"/>.</summary>
    private static void PurgeTrash(string modTrash)
    {
        var cutoff = DateTime.UtcNow - TrashMaxAge;
        var entries = ReadTrash(modTrash);
        for (var i = 0; i < entries.Count; i++)
            if (i >= TrashKeep || entries[i].DeletedUtc.ToUniversalTime() < cutoff)
                TryDeleteDirectory(Path.Combine(modTrash, entries[i].TrashId));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; the next purge retries */ }
    }
}
