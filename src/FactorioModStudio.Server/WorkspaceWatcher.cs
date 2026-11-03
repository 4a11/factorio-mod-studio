namespace FactorioModStudio.Server;

/// <summary>
/// Watches the workspace (and the project mod) and publishes coalesced change events:
/// <c>{"type":"changed","modId":…,"path":…,"kind":…}</c> for files, <c>{"type":"mods"}</c> when the list of mods may have changed,
/// and <c>{"type":"resync"}</c> when the operating system dropped events (clients should reload everything).
/// </summary>
internal sealed class WorkspaceWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".fmstudio", ".vs", ".vscode", ".idea", "node_modules" };

    private readonly ModCatalog _catalog;
    private readonly ChangeFeed _feed;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _watchedRoots = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private bool _resync;
    private bool _disposed;

    public WorkspaceWatcher(ModCatalog catalog, ChangeFeed feed)
    {
        _catalog = catalog;
        _feed = feed;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => EnsureWatching();

    /// <summary>Attaches watchers to roots that exist now but are not watched yet (e.g. a workspace folder created after startup).</summary>
    public void EnsureWatching()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var root in _catalog.WatchRoots())
            {
                if (_watchedRoots.Contains(root)) continue;
                if (TryWatch(root)) _watchedRoots.Add(root);
            }
        }
    }

    private bool TryWatch(string root)
    {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024
                };
                watcher.Created += (_, e) => Enqueue(e.FullPath, "created");
                watcher.Changed += (_, e) => Enqueue(e.FullPath, "changed");
                watcher.Deleted += (_, e) => Enqueue(e.FullPath, "deleted");
                watcher.Renamed += (_, e) => { Enqueue(e.OldFullPath, "deleted"); Enqueue(e.FullPath, "created"); };
                watcher.Error += (_, _) => RequestResync();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // The folder vanished or cannot be watched (e.g. inotify limit reached). The UI still works, just without live updates.
                return false;
            }
    }

    public int WatcherCount { get { lock (_gate) return _watchers.Count; } }

    private static bool IsIgnored(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".fms-tmp", StringComparison.Ordinal)) return true; // our own atomic-write temp files
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IgnoredSegments.Contains);
    }

    private void Enqueue(string path, string kind)
    {
        if (IsIgnored(path)) return;

        lock (_gate)
        {
            if (_disposed) return;

            // Keep the most informative kind: a file that was created and then written is still "created"; deletion wins over anything.
            _pending[path] = _pending.TryGetValue(path, out var existing) ? Merge(existing, kind) : kind;
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private static string Merge(string existing, string next) =>
        next == "deleted" ? "deleted" : existing is "created" && next == "changed" ? "created" : next;

    private void RequestResync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _resync = true;
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        Dictionary<string, string> batch;
        bool resync;

        lock (_gate)
        {
            if (_disposed) return;
            batch = new Dictionary<string, string>(_pending);
            _pending.Clear();
            resync = _resync;
            _resync = false;
        }

        try
        {
            if (resync)
            {
                _catalog.List();
                _feed.Publish(new { type = "resync" });
                return;
            }

            var structural = batch.Keys.Any(_catalog.IsStructuralChange);
            if (structural) _catalog.List(); // pick up new/removed mods before mapping paths to them

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, kind) in batch)
            {
                if (_catalog.ResolveChange(path) is not { } hit) continue;
                if (!seen.Add($"{hit.Entry.Id}|{hit.Relative}")) continue;
                _feed.Publish(new { type = "changed", modId = hit.Entry.Id, path = hit.Relative, kind });
            }

            if (structural) _feed.Publish(new { type = "mods" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder disappeared while scanning; the next change will trigger another flush.
            _feed.Publish(new { type = "resync" });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var watcher in _watchers) watcher.Dispose();
        }
        _timer.Dispose();
    }
}
