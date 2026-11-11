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

    public static void Map(WebApplication app, ModCatalog catalog, BuildJobs jobs, ProjectContext context)
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
            var kind = BlockOperations.KindOf(source, BlockOperations.IdOf(source, file));
            return Results.Json(new
                {
                    block, gameData = report is not null, editable = entry.Editable, needsUpgrade = BlockOperations.NeedsUpgrade(source),
                    // What the colour row needs: the block it was made from (for the icon preview) and its colour.
                    kind = kind?.Id,
                    iconSuffixes = kind?.Icons ?? Array.Empty<string>(),
                    color = BlockOperations.ColorOf(source)?.Hex,
                    icons = catalog.GameDataDirectory is not null
                },
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

        // ------------------------------------------------------------ rename / remove / upgrade a block, with undo

        // What the operation changed is kept like the file trash keeps deleted files (newest 20, 7 days): one folder per operation
        // with the earlier content of every path it touched, so Undo puts the mod back as it was.
        var undoRoot = Path.Combine(catalog.WorkspaceDirectory ?? context.Root, ".fmstudio", "trash", "~blocks");

        // { "file": "prototypes/x.lua", "name": "new-name" (rename), "migration": true (rename), "color": "#rrggbb" (recolor), "dryRun": true }
        app.MapPost("/api/mods/{id}/blocks/{operation}", async (HttpContext http, string id, string operation) =>
        {
            if (operation is not ("rename" or "remove" or "upgrade" or "recolor")) return Error(StatusCodes.Status404NotFound, "Unknown block operation.");
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (!entry.Editable) return Error(StatusCodes.Status403Forbidden, "This mod is read-only.");

            byte[] body;
            try { body = await UiServer.ReadLimited(http.Request.Body, MaxBodyBytes, http.RequestAborted); }
            catch (InvalidDataException) { return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large (limit 16 KB)."); }

            string? file, name, color;
            bool migration, dryRun;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                string? Text(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Flag(string p, bool fallback) => r.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
                file = Text("file");
                name = Text("name");
                color = Text("color");
                migration = Flag("migration", true);
                dryRun = Flag("dryRun", false);
            }
            catch (JsonException) { return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON."); }
            if (string.IsNullOrEmpty(file)) return Error(StatusCodes.Status400BadRequest, "\"file\" is required.");

            var root = Path.GetFullPath(entry.Path);
            if (UiServer.ResolveInside(root, file) is not { } full || !UiServer.IsWritablePath(root, full))
                return Error(StatusCodes.Status400BadRequest, "Invalid block file.");

            BlockPlan plan;
            try
            {
                plan = operation switch
                {
                    "rename" => BlockOperations.PlanRename(root, file, name ?? "", migration),
                    "remove" => BlockOperations.PlanRemove(root, file),
                    "recolor" => BlockOperations.PlanRecolor(root, file,
                        BlockColor.FromHex(color) ?? throw new FmsException("\"color\" must be #rrggbb."), catalog.GameDataDirectory),
                    _ => BlockOperations.PlanUpgrade(root, file, catalog.GameDataDirectory)
                };
            }
            catch (FmsException ex) { return Error(StatusCodes.Status400BadRequest, ex.Message); }

            var summary = new
            {
                operation = plan.Operation,
                file = plan.File,
                changes = plan.Changes.Select(c => new { path = c.Path, delete = c.Delete }),
                renamed = plan.Renamed,
                warnings = plan.Warnings
            };
            if (dryRun) return Results.Json(summary);

            // Every path must be one the studio may write (no hidden files, nothing outside the mod).
            foreach (var change in plan.Changes)
                if (UiServer.ResolveInside(root, change.Path) is not { } target || !UiServer.IsWritablePath(root, target))
                    return Error(StatusCodes.Status400BadRequest, $"The operation would change {change.Path}, which the studio may not write.");

            IReadOnlyDictionary<string, byte[]?> before;
            try { before = BlockOperations.Apply(root, plan); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return FilesApi.CannotChange(ex, operation); }

            var undoId = SaveUndo(undoRoot, entry.Id, plan, before);
            return Results.Json(new { summary.operation, summary.file, summary.changes, summary.renamed, summary.warnings, undoId });
        });

        app.MapPost("/api/mods/{id}/blocks/undo/{undoId}", (string id, string undoId) =>
        {
            var entry = catalog.Find(id);
            if (entry is null) return Error(StatusCodes.Status404NotFound, "Unknown mod.");
            if (!entry.Editable) return Error(StatusCodes.Status403Forbidden, "This mod is read-only.");
            if (!UndoIdPattern.IsMatch(undoId)) return Error(StatusCodes.Status404NotFound, "Unknown operation.");

            var dir = Path.Combine(undoRoot, undoId);
            var before = LoadUndo(dir, entry.Id);
            if (before is null) return Error(StatusCodes.Status404NotFound, "Unknown operation.");
            var root = Path.GetFullPath(entry.Path);
            foreach (var path in before.Keys)
                if (UiServer.ResolveInside(root, path) is not { } target || !UiServer.IsWritablePath(root, target))
                    return Error(StatusCodes.Status400BadRequest, "The saved operation names a path outside the mod.");

            try { BlockOperations.Restore(root, before); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return FilesApi.CannotChange(ex, "undo"); }
            TryDelete(dir);
            return Results.NoContent();
        });
    }

    private static readonly System.Text.RegularExpressions.Regex UndoIdPattern = new(@"^[0-9]{17}-[0-9a-f]{8}$");
    private const int UndoKeep = 20;

    private sealed record UndoFile(string Path, bool Existed, int Index);
    private sealed record UndoManifest(string UndoId, string ModId, string Operation, string File, DateTime CreatedUtc, List<UndoFile> Files);

    private static string SaveUndo(string undoRoot, string modId, BlockPlan plan, IReadOnlyDictionary<string, byte[]?> before)
    {
        var undoId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
        var dir = Path.Combine(undoRoot, undoId);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        var files = new List<UndoFile>();
        var i = 0;
        foreach (var (path, content) in before)
        {
            if (content is not null) File.WriteAllBytes(Path.Combine(dir, "content", $"{i}.bin"), content);
            files.Add(new UndoFile(path, content is not null, i++));
        }
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(new UndoManifest(undoId, modId, plan.Operation, plan.File, DateTime.UtcNow, files), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        // The newest entries that are younger than a week are kept.
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var old in new DirectoryInfo(undoRoot).EnumerateDirectories().Where(d => UndoIdPattern.IsMatch(d.Name))
                         .OrderByDescending(d => d.Name, StringComparer.Ordinal).Select((d, n) => (d, n))
                         .Where(x => x.n >= UndoKeep || x.d.CreationTimeUtc < cutoff))
                TryDelete(old.d.FullName);
        }
        catch (IOException) { /* best effort */ }
        return undoId;
    }

    private static Dictionary<string, byte[]?>? LoadUndo(string dir, string modId)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UndoManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is null || manifest.ModId != modId || manifest.Files is null) return null;
            var before = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            foreach (var f in manifest.Files)
                before[f.Path] = f.Existed ? File.ReadAllBytes(Path.Combine(dir, "content", $"{f.Index}.bin")) : null;
            return before;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
