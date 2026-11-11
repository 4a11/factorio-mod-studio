using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioModStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FactorioModStudio.Server;

/// <summary>
/// <c>/api/factorio/installations</c> (the installations the studio can use and the ones added in the UI) and <c>/api/fs/list</c> (the built-in folder browser).
/// Writes pass the token + same-origin check in <c>UiServer.MapSecurity</c>; the listing is a GET but exposes directory names, so it
/// checks the session token itself (the Host check is global).
/// What the listing may reveal: names of non-hidden sub-directories and files named factorio / factorio.exe. Nothing else, ever.
/// </summary>
internal static class FactorioSettingsApi
{
    private const long MaxBodyBytes = 4096;
    private const int MaxEntries = 2000;
    private const int MaxScanned = 50000;
    private const string TokenHeader = "X-FMS-Token";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, Options, statusCode: status);
    private static IResult Error(int status, string message) => Json(new { error = message }, status);

    public static void Map(WebApplication app, BuildJobs jobs, string token)
    {
        var expected = Encoding.UTF8.GetBytes(token);

        app.MapGet("/api/factorio/installations", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Json(Describe(jobs, refresh: false));
        });

        app.MapPost("/api/factorio/installations/rescan", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try { return Json(Describe(jobs, refresh: true)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not scan for installations: " + ex.Message); }
        });

        app.MapPost("/api/factorio/installations", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.ContentLength > MaxBodyBytes) return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 4 KB).");
            byte[] body;
            try { body = await BuildApi.ReadLimited(http.Request.Body, MaxBodyBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 4 KB)."); }

            string? requested;
            string? label = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String)
                    return Error(StatusCodes.Status400BadRequest, "The body must be an object with a \"path\" string.");
                requested = path.GetString();
                if (root.TryGetProperty("label", out var labelValue) && labelValue.ValueKind != JsonValueKind.Null)
                {
                    if (labelValue.ValueKind != JsonValueKind.String) return Error(StatusCodes.Status400BadRequest, "\"label\" must be a string or null.");
                    label = labelValue.GetString();
                }
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
            }

            var problem = FactorioSettings.Validate(requested?.Trim(), out var executable) ?? FactorioSettings.CheckLabel(label);
            if (problem is not null) return Error(StatusCodes.Status400BadRequest, problem);

            bool created;
            try { created = jobs.Settings.Upsert(executable!, FactorioSettings.CleanLabel(label)); }
            catch (InvalidOperationException ex) { return Error(StatusCodes.Status400BadRequest, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not save the setting: " + ex.Message); }

            jobs.InvalidateEnvironment();
            return Json(Describe(jobs, refresh: true), created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        app.MapDelete("/api/factorio/installations/{id}", (HttpContext http, string id) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            bool removed;
            try { removed = jobs.Settings.Remove(id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "Could not save the setting: " + ex.Message); }
            if (!removed) return Error(StatusCodes.Status404NotFound, "Unknown installation, or it was not added in this UI.");

            jobs.InvalidateEnvironment();
            return Json(Describe(jobs, refresh: true));
        });

        app.MapGet("/api/fs/list", (HttpContext http, string? path) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var supplied = Encoding.UTF8.GetBytes(http.Request.Headers[TokenHeader].ToString());
            if (!CryptographicOperations.FixedTimeEquals(supplied, expected)) return Error(StatusCodes.Status403Forbidden, "Missing or invalid session token.");
            return List(path);
        });
    }

    private static object Describe(BuildJobs jobs, bool refresh)
    {
        var candidates = jobs.GetCandidates(refresh);
        return new
        {
            installations = candidates.Select(c => new
            {
                id = c.Id, path = c.Path, version = c.Version, label = c.Label, sources = c.Sources,
                valid = true, removable = c.Sources.Contains("ui"),
                headless = FactorioDownloads.IsHeadlessInstall(c.Path), // installed by the studio; removed through /api/factorio/install/{version}
                managedGame = FactorioDownloads.IsGameInstall(c.Path) // the full game installed by the studio; removed with ?game=true
            }).ToList(),
            invalid = jobs.GetInvalid(candidates).Select(i => new { id = FactorioInstallations.IdOf(i.Path), path = i.Path, label = i.Label, error = i.Error }).ToList()
        };
    }

    private static IResult List(string? path)
    {
        var separator = Path.DirectorySeparatorChar.ToString();
        if (string.IsNullOrEmpty(path)) return Json(new { path = "", parent = (string?)null, separator, entries = Roots(), truncated = false });

        if (path.Length > FactorioSettings.MaxPathLength) return Error(StatusCodes.Status400BadRequest, "The path is too long.");
        if (path.Any(char.IsControl)) return Error(StatusCodes.Status400BadRequest, "The path contains control characters.");

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return Error(StatusCodes.Status400BadRequest, "The path must be absolute.");
            full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            if (full.Length > root.Length) full = Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error(StatusCodes.Status400BadRequest, "The path is not valid.");
        }

        try
        {
            if (!Directory.Exists(full)) return Error(StatusCodes.Status404NotFound, "That folder does not exist.");
            if (ContainsReparsePoint(full)) return Error(StatusCodes.Status403Forbidden, "Symbolic links and junctions are not followed.");

            var dirs = new List<string>();
            var files = new List<string>();
            var scanned = 0;
            var truncated = false;
            // Not IgnoreInaccessible: without recursion the only folder opened is this one, and a folder that cannot be read
            // has to be a 403, not an empty listing (on Linux and macOS the flag silently swallows that).
            var enumeration = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", enumeration))
            {
                if (++scanned > MaxScanned) { truncated = true; break; }
                var name = entry.Name;
                if (name.StartsWith('.')) continue;
                if (entry is DirectoryInfo) dirs.Add(name);
                else if (FactorioSettings.IsFactorioName(name) && (entry.Attributes & FileAttributes.ReparsePoint) == 0) files.Add(name);
            }

            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            var entries = dirs.Select(n => new { name = n, type = "dir", factorio = false })
                .Concat(files.Select(n => new { name = n, type = "file", factorio = true })).ToList();
            if (entries.Count > MaxEntries) { entries = entries.Take(MaxEntries).ToList(); truncated = true; }

            return Json(new { path = full, parent = Path.GetDirectoryName(full), separator, entries, truncated });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Error(StatusCodes.Status403Forbidden, "That folder cannot be read.");
        }
    }

    /// <summary>True when the directory or any ancestor is a symlink / junction, so a listing never reads through a link.</summary>
    private static bool ContainsReparsePoint(string full)
    {
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static List<object> Roots()
    {
        var names = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try { if (drive.DriveType == DriveType.Fixed && drive.IsReady) names.Add(drive.Name); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        else names.Add("/");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && Directory.Exists(home) && !names.Contains(home, StringComparer.OrdinalIgnoreCase)) names.Add(home);
        return names.Select(n => (object)new { name = n, type = "dir", factorio = false }).ToList();
    }
}
