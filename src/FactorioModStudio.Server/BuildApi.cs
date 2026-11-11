using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioModStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace FactorioModStudio.Server;

/// <summary>
/// HTTP endpoints of the "Build" tab: environment check, background jobs (validate / pack / smoke / run / dump) and downloads of packed
/// archives, and the game data a dump job saved (read by the Prototypes tab).
/// All POSTs pass the session-token and same-origin check in <c>UiServer.MapSecurity</c> before they get here.
/// </summary>
internal static class BuildApi
{
    private const long MaxJobBodyBytes = 4096;
    private const string DownloadPrefix = "/api/downloads/";

    /// <summary>Null properties are written (a running job has <c>"finishedAt": null</c>), unlike the other endpoints.</summary>
    private static readonly JsonSerializerOptions BuildJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, BuildJson, statusCode: status);

    private static IResult Error(int status, string message) => Json(new { error = message }, status);

    public static void Map(WebApplication app, ModCatalog catalog, BuildJobs jobs)
    {
        // `installation` (optional, an installation id) describes a manual choice instead of the automatic one.
        app.MapGet("/api/environment", (HttpContext http, string? modId, string? installation) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            CatalogEntry? entry = null;
            if (!string.IsNullOrEmpty(modId))
            {
                entry = catalog.Find(modId);
                if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            }

            var report = jobs.GetEnvironment(entry, string.IsNullOrEmpty(installation) ? null : installation);
            var status = report.Factorio;
            var resolution = report.Resolution;
            return Json(new
            {
                factorio = new { found = status.Found, path = status.Path, error = status.Error, source = status.Source, version = status.Version },
                resolution = new
                {
                    modVersion = resolution.ModVersion, mode = resolution.Mode, chosenId = resolution.ChosenId, reason = resolution.Reason,
                    baseRequirement = resolution.BaseRequirement, installSpec = resolution.InstallSpec
                }
            });
        });

        app.MapPost("/api/mods/{id}/jobs", async (HttpContext http, string id) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");

            if (http.Request.ContentLength > MaxJobBodyBytes) return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 4 KB).");
            byte[] body;
            try { body = await ReadLimited(http.Request.Body, MaxJobBodyBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 4 KB)."); }

            var parsed = Parse(body, out var request);
            if (parsed is not null) return Error(StatusCodes.Status400BadRequest, parsed);

            if (request!.Kind != JobKind.Validate && !entry.Editable)
                return Error(StatusCodes.Status403Forbidden, "This mod is read-only. Only unpacked mods in the workspace folder and the project mod can be packed or run.");

            if (request.Installation is not null && !jobs.GetCandidates(refresh: true).Any(c => string.Equals(c.Id, request.Installation, StringComparison.OrdinalIgnoreCase)))
                return Error(StatusCodes.Status400BadRequest, "\"installation\" is not a known Factorio installation.");

            var (started, busy) = jobs.TryStart(entry, request);
            if (busy is not null)
                return Json(new { error = "Another job is still running.", jobId = busy.Id }, StatusCodes.Status409Conflict);

            return Json(jobs.Snapshot(started!, 0), StatusCodes.Status202Accepted);
        });

        app.MapGet("/api/jobs", (HttpContext http, string? modId) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Json(jobs.List(string.IsNullOrEmpty(modId) ? null : modId));
        });

        app.MapGet("/api/jobs/{jobId}", (HttpContext http, string jobId, string? after) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var job = jobs.Find(jobId);
            if (job is null) return Error(StatusCodes.Status404NotFound, "Unknown job.");

            var from = 0;
            if (!string.IsNullOrEmpty(after) && (!int.TryParse(after, out from) || from < 0))
                return Error(StatusCodes.Status400BadRequest, "\"after\" must be a non-negative integer.");

            return Json(jobs.Snapshot(job, from));
        });

        app.MapPost("/api/jobs/{jobId}/cancel", async (HttpContext http, string jobId) =>
        {
            var job = jobs.Find(jobId);
            if (job is null) return Error(StatusCodes.Status404NotFound, "Unknown job.");

            if (!await jobs.CancelAsync(job)) return Json(new { error = "The job has already finished.", job = jobs.Snapshot(job, null) }, StatusCodes.Status409Conflict);
            return Json(jobs.Snapshot(job, 0));
        });

        // ------------------------------------------------------------ the game's own data (a "dump" job writes it)

        // The list without values: what the mod adds, changes and removes, and whether its files changed since.
        app.MapGet("/api/mods/{id}/gamedata", (HttpContext http, string id) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            var report = jobs.LoadReport(entry);
            if (report is null) return Json(new { exists = false });

            bool stale;
            try { stale = report.Fingerprint != GameDataDump.Fingerprint(Path.GetFullPath(entry.Path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stale = true; }

            return Json(new
            {
                exists = true,
                createdUtc = report.CreatedUtc,
                gameVersion = report.GameVersion,
                modVersion = report.ModVersion,
                mods = report.Mods,
                stale,
                counts = new
                {
                    added = report.Items.Count(i => i.Change == "added"),
                    changed = report.Items.Count(i => i.Change == "changed"),
                    removed = report.Items.Count(i => i.Change == "removed")
                },
                items = report.Items.Select(i => new { type = i.Type, name = i.Name, change = i.Change, copyOf = i.CopyOf, fields = i.Fields.Count, truncated = i.FieldsTruncated })
            });
        });

        // One prototype with every value the game has, and the fields that differ (from the original, or what the mod changed).
        app.MapGet("/api/mods/{id}/gamedata/item", (HttpContext http, string id, string? type, string? name) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name)) return Error(StatusCodes.Status400BadRequest, "\"type\" and \"name\" are required.");
            var item = jobs.LoadReport(entry)?.Items.FirstOrDefault(i => i.Type == type && i.Name == name);
            if (item is null) return Error(StatusCodes.Status404NotFound, "Not in the game data of this mod.");
            return Json(new
            {
                type = item.Type, name = item.Name, change = item.Change, copyOf = item.CopyOf, value = item.Value, truncated = item.FieldsTruncated,
                fields = item.Fields.Select(f => new { path = f.Path, before = f.Before, after = f.After })
            });
        });

        // Only archives directly inside <project>/dist with a plain file name. The raw request target is judged, because routing
        // decodes escapes: anything with a separator, a dot segment or an escape never reaches the file system.
        app.MapGet("/api/downloads/{file}", (HttpContext http, string file) =>
        {
            var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget ?? http.Request.Path.Value ?? "";
            var query = raw.IndexOf('?', StringComparison.Ordinal);
            var path = query >= 0 ? raw[..query] : raw;
            if (!path.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return Error(StatusCodes.Status404NotFound, "File not found.");

            var name = path[DownloadPrefix.Length..];
            if (!BuildJobs.IsDownloadName(name) || name != file) return Error(StatusCodes.Status404NotFound, "File not found.");

            var dist = Path.GetFullPath(jobs.DistDirectory);
            var full = Path.GetFullPath(Path.Combine(dist, name));
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(dist), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return Error(StatusCodes.Status404NotFound, "File not found.");

            var info = new FileInfo(full);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return Error(StatusCodes.Status404NotFound, "File not found.");

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(full, "application/zip", fileDownloadName: name);
        });
    }

    /// <returns>An error message, or null when <paramref name="request"/> is valid.</returns>
    private static string? Parse(byte[] body, out JobRequest? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "The body must be a JSON object.";

            if (!root.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String) return "\"kind\" must be one of validate, pack, smoke, run, dump.";
            JobKind kind;
            switch (kindValue.GetString())
            {
                case "validate": kind = JobKind.Validate; break;
                case "pack": kind = JobKind.Pack; break;
                case "smoke": kind = JobKind.Smoke; break;
                case "run": kind = JobKind.Run; break;
                case "dump": kind = JobKind.Dump; break;
                default: return "\"kind\" must be one of validate, pack, smoke, run, dump.";
            }

            string? Integer(string name, int min, int max, int fallback, out int value)
            {
                value = fallback;
                if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number)) return $"\"{name}\" must be an integer.";
                if (number < min || number > max) return $"\"{name}\" must be between {min} and {max}.";
                value = number;
                return null;
            }

            if (Integer("ticks", 1, 216000, 600, out var ticks) is { } e1) return e1;
            if (Integer("seed", int.MinValue, int.MaxValue, 1, out var seed) is { } e2) return e2;
            if (Integer("timeoutMinutes", 1, 60, 5, out var timeout) is { } e3) return e3;

            var force = false;
            if (root.TryGetProperty("force", out var forceValue) && forceValue.ValueKind != JsonValueKind.Null)
            {
                if (forceValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "\"force\" must be true or false.";
                force = forceValue.GetBoolean();
            }

            string? installation = null;
            if (root.TryGetProperty("installation", out var installValue) && installValue.ValueKind != JsonValueKind.Null)
            {
                if (installValue.ValueKind != JsonValueKind.String || installValue.GetString() is not { Length: > 0 and <= 64 } text) return "\"installation\" must be an installation id (string).";
                installation = text;
            }

            request = new JobRequest(kind, ticks, seed, timeout, force, installation);
            return null;
        }
        catch (JsonException)
        {
            return "The body is not valid JSON.";
        }
    }

    internal static async Task<byte[]> ReadLimited(Stream input, long limit, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[2048];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > limit) throw new InvalidDataException("Body too large.");
        }
        return memory.ToArray();
    }
}
