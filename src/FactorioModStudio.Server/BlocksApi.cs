using System.Text;
using System.Text.Json;
using FactorioModStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FactorioModStudio.Server;

/// <summary>
/// The properties panel of building blocks: their editable numbers (<see cref="BlockProperties"/>) with the original and in-game
/// values from the mod's game data report, and writing new values back into the block's Lua file. Writes pass the session-token
/// check in <c>UiServer.MapSecurity</c>, work only on editable mods and must name the version of the file they were read from.
/// </summary>
internal static class BlocksApi
{
    private const long MaxBodyBytes = 16 * 1024;

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    public static void Map(WebApplication app, ModCatalog catalog, BuildJobs jobs)
    {
        app.MapGet("/api/mods/{id}/blocks", (HttpContext http, string id, string? file) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            var root = catalog.Materialize(entry);
            if (string.IsNullOrEmpty(file) || UiServer.ResolveInside(root, file) is not { } full || !full.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return Error(StatusCodes.Status404NotFound, "Block file not found.");

            var source = File.ReadAllText(full);
            if (!BlockProperties.IsBlock(source)) return Error(StatusCodes.Status400BadRequest, "This file is not a building block.");
            var report = jobs.LoadReport(entry);
            var block = BlockProperties.WithGameData(BlockProperties.Read(file, source), report);
            return Results.Json(new { block, gameData = report is not null, editable = entry.Editable },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never });
        });

        // { "file": "prototypes/x.lua", "version": "...", "values": { "<key>": "48", ... } }
        app.MapPost("/api/mods/{id}/blocks", async (HttpContext http, string id) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (!entry.Editable) return Error(StatusCodes.Status403Forbidden, "This mod is read-only.");

            byte[] body;
            try { body = await UiServer.ReadLimited(http.Request.Body, MaxBodyBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 16 KB)."); }

            string? file, version;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                file = r.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                version = r.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (!r.TryGetProperty("values", out var vals) || vals.ValueKind != JsonValueKind.Object) return Error(StatusCodes.Status400BadRequest, "\"values\" must be an object.");
                foreach (var p in vals.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String) return Error(StatusCodes.Status400BadRequest, "Every value must be a string.");
                    values[p.Name] = p.Value.GetString()!;
                }
            }
            catch (JsonException) { return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON."); }

            var root = Path.GetFullPath(entry.Path);
            if (file is null || UiServer.ResolveInside(root, file) is not { } full || !UiServer.IsWritablePath(root, full)
                || !full.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return Error(StatusCodes.Status400BadRequest, "Invalid block file.");

            var source = File.ReadAllText(full);
            if (!BlockProperties.IsBlock(source)) return Error(StatusCodes.Status400BadRequest, "This file is not a building block.");
            if (version != BlockProperties.VersionOf(source))
                return Error(StatusCodes.Status409Conflict, "The file changed since its properties were read.");

            string updated;
            try { updated = BlockProperties.Apply(source, values); }
            catch (FmsException ex) { return Error(StatusCodes.Status400BadRequest, ex.Message); }

            try { UiServer.WriteAtomically(full, updated, exists: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Error(StatusCodes.Status500InternalServerError, $"Could not save: {ex.Message}");
            }
            return Results.Json(new { version = BlockProperties.VersionOf(updated) });
        });
    }
}
