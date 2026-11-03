using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FactorioModStudio.Core;

namespace FactorioModStudio.Server;

/// <param name="Source">"project" (the mod in the project folder), "workspace" (a mod in the workspace folder),
/// "installed" (a mods folder) or "game" (the Factorio data folder).</param>
/// <param name="Kind">"directory" or "zip".</param>
/// <param name="Editable">Only unpacked mods in the workspace and the project mod can be edited; everything else is read-only.</param>
public sealed record CatalogEntry(string Id, string Name, string Version, string Source, string Kind, string Path, bool Editable);

/// <summary>
/// The set of mods the UI may show. Mods are addressed by opaque ids so the HTTP API never accepts arbitrary file system paths.
/// </summary>
internal sealed class ModCatalog
{
    private const string ProjectId = "project";
    private static readonly TimeSpan ReadOnlyCacheLifetime = TimeSpan.FromSeconds(15);

    private readonly ProjectContext _context;
    private readonly IReadOnlyList<string> _modFolders;
    private readonly string? _gameDataDirectory;
    private readonly string _cacheRoot;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ModOverview> _overviews = new();
    private Dictionary<string, CatalogEntry> _entries = new();
    private (DateTime At, List<CatalogEntry> Entries)? _readOnly;

    public ModCatalog(ProjectContext context, IReadOnlyList<string> extraModFolders, string? gameDataDirectory, string? workspaceDirectory)
    {
        _context = context;
        _gameDataDirectory = gameDataDirectory;
        WorkspaceDirectory = workspaceDirectory is null ? null : Path.GetFullPath(workspaceDirectory);
        _cacheRoot = Path.Combine(Path.GetTempPath(), "fmstudio", "cache");

        var folders = new List<string>();
        if (context.ModsSourceDirectory is { } configured) folders.Add(configured);
        folders.AddRange(extraModFolders.Select(Path.GetFullPath));
        _modFolders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> ModFolders => _modFolders;
    public string? GameDataDirectory => _gameDataDirectory;
    public string? WorkspaceDirectory { get; }
    public string ProjectModDirectory => _context.ModDirectory;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Folders that can change while the UI is open and are worth watching.</summary>
    public IReadOnlyList<string> WatchRoots()
    {
        var roots = new List<string>();
        if (WorkspaceDirectory is not null && Directory.Exists(WorkspaceDirectory)) roots.Add(WorkspaceDirectory);
        if (Directory.Exists(_context.ModDirectory) &&
            !roots.Any(r => _context.ModDirectory.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, PathComparison)))
            roots.Add(_context.ModDirectory);
        return roots;
    }

    public IReadOnlyList<CatalogEntry> List()
    {
        var entries = new List<CatalogEntry>();

        var projectInfo = Path.Combine(_context.ModDirectory, "info.json");
        if (File.Exists(projectInfo))
        {
            try
            {
                var info = ModInfo.Load(projectInfo);
                entries.Add(new CatalogEntry(ProjectId, info.Name ?? "(unnamed mod)", info.Version ?? "", "project", "directory", _context.ModDirectory, Editable: true));
            }
            catch (System.Text.Json.JsonException)
            {
                entries.Add(new CatalogEntry(ProjectId, "(info.json is invalid)", "", "project", "directory", _context.ModDirectory, Editable: true));
            }
        }

        if (WorkspaceDirectory is not null && Directory.Exists(WorkspaceDirectory))
        {
            foreach (var mod in ModSource.Scan(WorkspaceDirectory).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(m => m.Version))
            {
                if (string.Equals(Path.GetFullPath(mod.Path), Path.GetFullPath(_context.ModDirectory), PathComparison)) continue; // already listed as the project mod
                entries.Add(new CatalogEntry(IdFor(mod.Path), mod.Name, mod.Version.ToString(), "workspace", mod.IsDirectory ? "directory" : "zip", mod.Path, Editable: mod.IsDirectory));
            }
        }

        entries.AddRange(ReadOnlyEntries());

        // Keep the first entry for an id (the same folder can be reachable through two sources).
        var map = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in entries) map.TryAdd(entry.Id, entry);

        lock (_gate) _entries = map;
        return map.Values.ToList();
    }

    /// <summary>The game's data folder and installed-mod folders change rarely and can be large, so they are rescanned at most every few seconds.</summary>
    private List<CatalogEntry> ReadOnlyEntries()
    {
        lock (_gate)
            if (_readOnly is { } cached && DateTime.UtcNow - cached.At < ReadOnlyCacheLifetime) return cached.Entries;

        var entries = new List<CatalogEntry>();

        if (_gameDataDirectory is not null && Directory.Exists(_gameDataDirectory))
        {
            foreach (var dir in Directory.EnumerateDirectories(_gameDataDirectory).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(dir, "info.json"))) continue;
                try
                {
                    var info = ModInfo.Load(Path.Combine(dir, "info.json"));
                    entries.Add(new CatalogEntry(IdFor(dir), info.Name ?? Path.GetFileName(dir), info.Version ?? "", "game", "directory", dir, Editable: false));
                }
                catch (System.Text.Json.JsonException) { /* not a readable mod folder */ }
            }
        }

        foreach (var folder in _modFolders)
        {
            foreach (var mod in ModSource.Scan(folder).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(m => m.Version))
                entries.Add(new CatalogEntry(IdFor(mod.Path), mod.Name, mod.Version.ToString(), "installed", mod.IsDirectory ? "directory" : "zip", mod.Path, Editable: false));
        }

        lock (_gate) _readOnly = (DateTime.UtcNow, entries);
        return entries;
    }

    public CatalogEntry? Find(string id)
    {
        lock (_gate)
            if (_entries.TryGetValue(id, out var known)) return known;

        List();
        lock (_gate)
            return _entries.GetValueOrDefault(id);
    }

    /// <summary>Maps a changed file system path to the editable mod containing it, and the path relative to that mod.</summary>
    public (CatalogEntry Entry, string Relative)? ResolveChange(string fullPath)
    {
        CatalogEntry? best = null;
        var bestPrefix = "";

        lock (_gate)
        {
            foreach (var entry in _entries.Values.Where(e => e.Editable))
            {
                var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Path)) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(prefix, PathComparison) && (best is null || prefix.Length > bestPrefix.Length))
                {
                    best = entry;
                    bestPrefix = prefix;
                }
            }
        }

        return best is null ? null : (best, fullPath[bestPrefix.Length..].Replace('\\', '/'));
    }

    /// <summary>True when a change can alter the list of mods (a mod folder appearing, disappearing or its info.json changing).</summary>
    public bool IsStructuralChange(string fullPath)
    {
        if (string.Equals(fullPath, Path.Combine(_context.ModDirectory, "info.json"), PathComparison)) return true;
        if (WorkspaceDirectory is null) return false;

        var relative = Path.GetRelativePath(WorkspaceDirectory, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;

        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length == 1 || (parts.Length == 2 && parts[1].Equals("info.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns a directory containing the mod's files, extracting a zip into the cache first if needed.</summary>
    public string Materialize(CatalogEntry entry) =>
        entry.Kind == "zip" ? ModExtractor.ExtractToCache(entry.Path, _cacheRoot) : entry.Path;

    public ModOverview Overview(CatalogEntry entry)
    {
        var directory = Materialize(entry);

        // Mods being edited change while the UI is open: never cache them.
        if (entry.Editable) return ModInspector.Inspect(directory);

        // Read-only mods can still change on disk; the key includes the last write time of the entry point.
        var stamp = (entry.Kind == "zip" ? File.GetLastWriteTimeUtc(entry.Path) : Directory.GetLastWriteTimeUtc(entry.Path)).Ticks;
        return _overviews.GetOrAdd($"{entry.Id}:{stamp}", _ => ModInspector.Inspect(directory, validate: entry.Source != "game"));
    }

    internal static string IdFor(string path) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))).ToLowerInvariant()[..12];

    /// <summary>Finds Factorio's "data" folder relative to the executable (…/bin/x64/factorio → …/data).</summary>
    public static string? GameDataFor(string? factorioExecutable)
    {
        if (factorioExecutable is null) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(factorioExecutable));
        if (dir is null) return null;

        foreach (var relative in new[] { Path.Combine("..", "..", "data"), Path.Combine("..", "data"), Path.Combine("..", "..", "..", "data") })
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, relative));
            if (Directory.Exists(Path.Combine(candidate, "base"))) return candidate;
        }

        return null;
    }
}
