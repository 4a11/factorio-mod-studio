using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactorioModStudio.Core;

namespace FactorioModStudio.Server;

internal enum JobKind { Validate, Pack, Smoke, Run }

internal enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>Validated options of a job request. The browser can only choose these; never an executable or arguments.</summary>
internal sealed record JobRequest(JobKind Kind, int Ticks = 600, int Seed = 1, int TimeoutMinutes = 5, bool Force = false, string? Installation = null);

internal sealed record JobLine(int N, string Text);

internal sealed record ValidateJobResult(int Errors, int Warnings, int Infos, IReadOnlyList<ValidationIssue> Issues);

internal sealed record PackJobResult(bool Success, string? Archive, string? DownloadUrl, int FileCount, long Bytes, IReadOnlyList<ValidationIssue> Issues);

internal sealed record SmokeJobResult(bool Passed, string? FailureReason, int ErrorCount, int WarningCount, IReadOnlyList<LogDiagnostic> Diagnostics);

internal sealed record RunJobResult(int ExitCode);

internal sealed record FactorioStatus(bool Found, string? Path, string? Error, string Source = "none", string? Version = null);

/// <summary>How the installation for a mod was (or could not be) chosen.</summary>
internal sealed record FactorioResolution(string? ModVersion, string Mode, string? ChosenId, string Reason);

internal sealed record EnvironmentReport(FactorioStatus Factorio, FactorioResolution Resolution);

/// <summary>The installation a smoke / run job uses, with the mod's target version at the time it started.</summary>
internal sealed record JobFactorio(FactorioInstallation? Installation, string Mode, string Reason, string? ModVersion);

internal sealed class BuildJob
{
    private readonly Queue<JobLine> _lines = new();
    private int _lastN;

    public BuildJob(string id, string modId, JobKind kind)
    {
        Id = id;
        ModId = modId;
        Kind = kind;
        State = JobState.Running;
        StartedAt = DateTime.UtcNow;
    }

    public string Id { get; }
    public string ModId { get; }
    public JobKind Kind { get; }
    public JobState State { get; set; }
    public DateTime StartedAt { get; }
    public DateTime? FinishedAt { get; set; }
    public bool Truncated { get; private set; }
    public string? Error { get; set; }
    public object? Result { get; set; }
    public JobFactorio? Factorio { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsTerminal => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

    /// <summary>True while the worker still runs (a cancelled job stays active until its process tree is gone).</summary>
    public bool IsActive => !Done.Task.IsCompleted;

    /// <summary>Call with the manager lock held.</summary>
    public void AddLine(string text)
    {
        if (text.Length > BuildJobs.MaxLineLength) text = text[..(BuildJobs.MaxLineLength - 1)] + "…";
        _lines.Enqueue(new JobLine(++_lastN, text));
        while (_lines.Count > BuildJobs.MaxLines)
        {
            _lines.Dequeue();
            Truncated = true;
        }
    }

    /// <summary>Call with the manager lock held.</summary>
    public List<JobLine> LinesAfter(int after) => _lines.Where(l => l.N > after).ToList();
}

/// <summary>
/// Runs validate / pack / smoke / run for a catalog mod in the background and keeps a short in-memory history with live logs.
/// Safety: the executable is located exactly like the CLI does (never from a request) and the arguments are built here.
/// </summary>
internal sealed class BuildJobs : IDisposable
{
    public const int MaxJobs = 20;
    public const int MaxLines = 5000;
    public const int MaxLineLength = 4000;

    private static readonly TimeSpan EnvironmentCacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly Regex DownloadName = new(@"^[A-Za-z0-9._\-]+\.zip$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ProjectContext _project;
    private readonly ModCatalog _catalog;
    private readonly ICancellableProcessRunner _runner;
    private readonly object _gate = new();
    private readonly List<BuildJob> _jobs = new(); // oldest first
    private (DateTime At, IReadOnlyList<FactorioInstallation> List)? _candidates;
    private bool _disposed;

    public BuildJobs(ProjectContext project, ModCatalog catalog, ICancellableProcessRunner? runner = null)
    {
        _project = project;
        _catalog = catalog;
        _runner = runner ?? new ProcessRunner();
        Settings = new FactorioSettings(project.StateDirectory);
    }

    /// <summary>The installations registered in the UI (<c>.fmstudio/settings.json</c>).</summary>
    public FactorioSettings Settings { get; }

    public void InvalidateEnvironment()
    {
        lock (_gate) _candidates = null;
    }

    public string DistDirectory => Path.Combine(_project.Root, "dist");

    public static bool IsDownloadName(string name) => DownloadName.IsMatch(name);

    // ------------------------------------------------------------ environment

    /// <summary>All usable installations; recomputed at most every 5 seconds unless <paramref name="refresh"/> is set.</summary>
    public IReadOnlyList<FactorioInstallation> GetCandidates(bool refresh = false)
    {
        if (!refresh)
            lock (_gate)
                if (_candidates is { } cached && DateTime.UtcNow - cached.At < EnvironmentCacheLifetime) return cached.List;

        var list = FactorioInstallations.Candidates(_project, Settings.Read());
        lock (_gate) _candidates = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>Registered installations that no longer work (executable gone, renamed or replaced by a link), with the reason.</summary>
    public List<(string Path, string? Label, string Error)> GetInvalid(IReadOnlyList<FactorioInstallation> candidates)
    {
        var invalid = new List<(string, string?, string)>();
        foreach (var item in Settings.Read())
        {
            var problem = FactorioInstallations.CheckExecutable(item.Path, out var exe);
            if (problem is not null) invalid.Add((item.Path, item.Label, problem));
            else if (!candidates.Any(c => c.Id == FactorioInstallations.IdOf(exe!))) invalid.Add((item.Path, item.Label, "The Factorio executable was not found."));
        }
        return invalid;
    }

    /// <summary>The target version declared by the mod's info.json (null when unreadable or absent).</summary>
    public string? ModFactorioVersion(CatalogEntry? entry)
    {
        if (entry is null) return null;
        try { return ModInfo.Load(Path.Combine(_catalog.Materialize(entry), "info.json")).FactorioVersion; }
        catch (Exception ex) when (ex is FmsException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }

    public FactorioPick Pick(IReadOnlyList<FactorioInstallation> candidates, string? modVersion, string? manualId)
    {
        var pick = FactorioInstallations.Pick(candidates, modVersion, manualId);
        if (pick.Chosen is null && manualId is null)
        {
            var unusable = FactorioInstallations.UnusableConfiguredSources(_project);
            if (unusable.Count > 0) pick = pick with { Reason = pick.Reason + " Not usable: " + string.Join(", ", unusable) + "." };
        }
        return pick;
    }

    /// <summary>The environment as the Build tab shows it: the chosen installation and why. <paramref name="entry"/> null = "no mod".</summary>
    public EnvironmentReport GetEnvironment(CatalogEntry? entry, string? manualId = null)
    {
        var candidates = GetCandidates();
        var modVersion = ModFactorioVersion(entry);
        var pick = Pick(candidates, modVersion, manualId);
        var chosen = pick.Chosen;
        var status = chosen is null
            ? new FactorioStatus(false, null, pick.Reason, "none")
            : new FactorioStatus(true, chosen.Path, null, chosen.Sources.FirstOrDefault() ?? "none", chosen.Version);
        return new EnvironmentReport(status, new FactorioResolution(modVersion, pick.Mode, chosen?.Id, pick.Reason));
    }

    // ------------------------------------------------------------ queries

    public BuildJob? Find(string id)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.Id == id);
    }

    /// <summary>A consistent copy of the job. <paramref name="after"/> null leaves the <c>lines</c> property out.</summary>
    public object Snapshot(BuildJob job, int? after)
    {
        lock (_gate)
        {
            var fields = new Dictionary<string, object?>
            {
                ["id"] = job.Id,
                ["modId"] = job.ModId,
                ["kind"] = job.Kind,
                ["state"] = job.State,
                ["startedAt"] = job.StartedAt,
                ["finishedAt"] = job.FinishedAt
            };
            if (after is { } a) fields["lines"] = job.LinesAfter(a);
            fields["truncated"] = job.Truncated;
            fields["error"] = job.Error;
            fields["result"] = job.Result;
            fields["factorio"] = job.Factorio is { Installation: { } i } f
                ? new { id = i.Id, path = i.Path, version = i.Version, mode = f.Mode, reason = f.Reason }
                : null;
            return fields;
        }
    }

    public List<object> List(string? modId)
    {
        List<BuildJob> copy;
        lock (_gate) copy = _jobs.Where(j => modId is null || j.ModId == modId).Reverse().ToList();
        return copy.Select(j => Snapshot(j, null)).ToList();
    }

    // ------------------------------------------------------------ starting / cancelling

    /// <returns>The new job, or the active job that blocks it.</returns>
    public (BuildJob? Started, BuildJob? Busy) TryStart(CatalogEntry entry, JobRequest request)
    {
        BuildJob job;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BuildJobs));

            var sameMod = _jobs.FirstOrDefault(j => j.IsActive && j.ModId == entry.Id);
            if (sameMod is not null) return (null, sameMod);

            if (request.Kind is JobKind.Smoke or JobKind.Run)
            {
                var game = _jobs.FirstOrDefault(j => j.IsActive && j.Kind is JobKind.Smoke or JobKind.Run);
                if (game is not null) return (null, game);
            }

            job = new BuildJob(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), entry.Id, request.Kind);
            _jobs.Add(job);
            Evict();
        }

        if (request.Kind is JobKind.Smoke or JobKind.Run)
        {
            // Decided now (not in the worker) so that the first snapshot already says which Factorio is used.
            try
            {
                var modVersion = ModFactorioVersion(entry);
                var pick = Pick(GetCandidates(refresh: true), modVersion, request.Installation);
                lock (_gate) job.Factorio = new JobFactorio(pick.Chosen, pick.Mode, pick.Reason, modVersion);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_gate) job.Factorio = new JobFactorio(null, "none", "Could not look for Factorio installations: " + ex.Message, null);
            }
        }

        var token = job.Cancellation.Token;
        Task.Factory.StartNew(() => Execute(job, entry, request, token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return (job, null);
    }

    private void Evict()
    {
        while (_jobs.Count > MaxJobs)
        {
            var oldest = _jobs.FirstOrDefault(j => !j.IsActive && j.IsTerminal);
            if (oldest is null) break; // only running jobs left: never evicted
            _jobs.Remove(oldest);
        }
    }

    /// <summary>Cancels an unfinished job and waits (briefly) until its processes are gone. Returns false when it was already finished.</summary>
    public async Task<bool> CancelAsync(BuildJob job)
    {
        lock (_gate)
        {
            if (job.IsTerminal) return false;
            job.State = JobState.Cancelled;
            job.FinishedAt = DateTime.UtcNow;
        }

        try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        await Task.WhenAny(job.Done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        return true;
    }

    public void Dispose()
    {
        List<BuildJob> active;
        lock (_gate)
        {
            _disposed = true;
            active = _jobs.Where(j => j.IsActive).ToList();
        }

        foreach (var job in active)
        {
            try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        Task.WaitAll(active.Select(j => (Task)j.Done.Task).ToArray(), TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------ execution

    private void Log(BuildJob job, string text)
    {
        lock (_gate) job.AddLine(text);
    }

    private void Complete(BuildJob job, JobState state, string? error, object? result)
    {
        lock (_gate)
        {
            if (job.State != JobState.Cancelled) // a cancel request wins; its state was already published
            {
                job.State = state;
                job.FinishedAt = DateTime.UtcNow;
                job.Error = error;
            }
            job.Result = result;
        }
    }

    private void Execute(BuildJob job, CatalogEntry entry, JobRequest request, CancellationToken token)
    {
        try
        {
            switch (request.Kind)
            {
                case JobKind.Validate: ExecuteValidate(job, entry); break;
                case JobKind.Pack: ExecutePack(job, entry, request); break;
                case JobKind.Smoke: ExecuteSmoke(job, entry, request, token); break;
                case JobKind.Run: ExecuteRun(job, entry, token); break;
            }
        }
        catch (OperationCanceledException)
        {
            Complete(job, JobState.Cancelled, null, null);
        }
        catch (Exception ex) when (ex is FmsException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log(job, "error: " + ex.Message);
            Complete(job, JobState.Failed, ex.Message, null);
        }
        catch (Exception ex)
        {
            Log(job, "error: " + ex.Message);
            Complete(job, JobState.Failed, "Unexpected error: " + ex.Message, null);
        }
        finally
        {
            lock (_gate)
            {
                if (!job.IsTerminal) // defensive: never leave a finished worker in a running state
                {
                    job.State = JobState.Failed;
                    job.FinishedAt = DateTime.UtcNow;
                    job.Error ??= "The job ended unexpectedly.";
                }
            }
            job.Done.TrySetResult();
            try { job.Cancellation.Dispose(); } catch (Exception) { /* cancel callbacks may still run */ }
        }
    }

    private void LogValidation(BuildJob job, ValidationResult validation)
    {
        foreach (var issue in validation.Issues.OrderByDescending(i => i.Severity))
        {
            var location = issue.File is null ? "" : issue.Line is null ? $"{issue.File}: " : $"{issue.File}:{issue.Line}: ";
            Log(job, $"{issue.Severity.ToString().ToLowerInvariant(),-7} {issue.Code}  {location}{issue.Message}");
        }

        Log(job, validation.Issues.Count == 0
            ? "No problems found."
            : $"{validation.ErrorCount} error(s), {validation.WarningCount} warning(s).");
    }

    private void ExecuteValidate(BuildJob job, CatalogEntry entry)
    {
        var directory = _catalog.Materialize(entry);
        var validation = ModValidator.Validate(directory);
        LogValidation(job, validation);

        var result = new ValidateJobResult(
            validation.ErrorCount, validation.WarningCount,
            validation.Issues.Count(i => i.Severity == Severity.Info), validation.Issues);
        if (validation.HasErrors) Complete(job, JobState.Failed, "validation errors", result);
        else Complete(job, JobState.Succeeded, null, result);
    }

    private void ExecutePack(BuildJob job, CatalogEntry entry, JobRequest request)
    {
        var pack = ModPackager.Pack(_catalog.Materialize(entry), DistDirectory, request.Force);
        LogValidation(job, pack.Validation);

        string? archive = pack.ArchivePath is null ? null : Path.GetFileName(pack.ArchivePath);
        var downloadable = pack.Success && archive is not null && IsDownloadName(archive);
        var result = new PackJobResult(pack.Success, archive, downloadable ? "/api/downloads/" + archive : null,
            pack.FileCount, pack.ArchiveBytes, pack.Validation.Issues);

        if (pack.Success)
        {
            Log(job, $"Packed {pack.FileCount} file(s) into {archive} ({pack.ArchiveBytes / 1024.0:F1} KiB).");
            Complete(job, JobState.Succeeded, null, result);
        }
        else
        {
            Log(job, pack.Validation.HasErrors ? "Not packed: fix the errors above (or use force)." : "Not packed.");
            Complete(job, JobState.Failed, pack.Validation.HasErrors ? "validation errors" : "Not packed.", result);
        }
    }

    /// <summary>"ui-&lt;mod&gt;-&lt;MAJOR.MINOR&gt;" (the version of the mod's factorio_version); "ui-&lt;mod&gt;" when that is invalid.</summary>
    private static string SanitizeProfileName(CatalogEntry entry, string? modVersion)
    {
        var name = Regex.Replace(entry.Name ?? "", @"[^A-Za-z0-9_.\-]", "_");
        if (name.Length == 0 || name.Trim('.', '_', '-').Length == 0) name = entry.Id;
        if (name.Length > 80) name = name[..80];
        return FactorioInstallations.NormaliseMajorMinor(modVersion) is { } version ? $"ui-{name}-{version}" : "ui-" + name;
    }

    /// <summary>
    /// A project view whose mod directory is the selected mod, with one profile "ui-&lt;mod&gt;" (own directory, never the CLI's
    /// profiles). Mods, args and save are taken from the project's "default" profile.
    /// </summary>
    private (ProjectContext Context, string Profile, ProfileConfig Config) ContextFor(CatalogEntry entry, string? modVersion)
    {
        var source = _project.Config;
        var profile = SanitizeProfileName(entry, modVersion);
        var defaults = source.Profiles.TryGetValue("default", out var d) ? d : new ProfileConfig();
        var config = new ProjectConfig
        {
            ModPath = Path.GetFullPath(entry.Path),
            FactorioPath = source.FactorioPath,
            ModsSource = source.ModsSource,
            Workspace = source.Workspace,
            Profiles = { [profile] = new ProfileConfig { Mods = new List<string>(defaults.Mods), Args = new List<string>(defaults.Args), Save = defaults.Save } }
        };
        var context = new ProjectContext(_project.Root, config, _project.ConfigFileExists);
        return (context, profile, config.Profiles[profile]);
    }

    /// <summary>The installation decided when the job started; throws when there is none (the reason is the message).</summary>
    private static FactorioInstallation RequireFactorio(BuildJob job)
    {
        var game = job.Factorio ?? throw new FmsException("No Factorio installation was chosen.");
        return game.Installation ?? throw new FmsException(game.Reason);
    }

    private void LogUsing(BuildJob job)
    {
        var game = job.Factorio!;
        var install = game.Installation!;
        Log(job, $"Using Factorio {install.Version ?? "(unknown version)"} ({install.Path}) [{game.Mode}]");
        if (game.Mode == "manual" && FactorioInstallations.NormaliseMajorMinor(game.ModVersion) is { } wanted && !FactorioInstallations.VersionMatches(install.Version, wanted))
            Log(job, $"warning: the mod targets Factorio {wanted} but the chosen installation is {install.Version ?? "of unknown version"}.");
    }

    private void LogPrepared(BuildJob job, PreparedProfile prepared)
    {
        Log(job, $"Profile \"{prepared.Name}\": {string.Join(", ", prepared.EnabledMods)}");
        foreach (var warning in prepared.Warnings) Log(job, $"warning: {warning}");
    }

    private void ExecuteSmoke(BuildJob job, CatalogEntry entry, JobRequest request, CancellationToken token)
    {
        var validation = ModValidator.Validate(_catalog.Materialize(entry));
        if (validation.HasErrors)
        {
            LogValidation(job, validation);
            Log(job, "Smoke test not started: fix the validation errors first.");
            Complete(job, JobState.Failed, "validation errors", null);
            return;
        }

        string factorio;
        try { factorio = RequireFactorio(job).Path; }
        catch (FmsException ex)
        {
            Log(job, "error: " + ex.Message);
            Complete(job, JobState.Failed, ex.Message, null);
            return;
        }

        token.ThrowIfCancellationRequested();
        LogUsing(job);
        var (context, profileName, profileConfig) = ContextFor(entry, job.Factorio!.ModVersion);
        var prepared = ProfileBuilder.Prepare(context, profileName);
        LogPrepared(job, prepared);

        var options = new SmokeOptions(request.Ticks, request.Seed, TimeSpan.FromMinutes(request.TimeoutMinutes));
        Log(job, $"Running Factorio headless for {options.Ticks} ticks...");
        var smoke = SmokeTest.Run(factorio, context, prepared, profileConfig, options, _runner, line => Log(job, line), token);

        var result = new SmokeJobResult(smoke.Passed, smoke.FailureReason, smoke.Report.ErrorCount, smoke.Report.WarningCount, smoke.Report.Diagnostics);
        if (smoke.Cancelled)
        {
            Log(job, "SMOKE TEST CANCELLED");
            Complete(job, JobState.Cancelled, null, result);
        }
        else if (smoke.Passed)
        {
            Log(job, "SMOKE TEST PASSED");
            Complete(job, JobState.Succeeded, null, result);
        }
        else
        {
            Log(job, $"SMOKE TEST FAILED: {smoke.FailureReason}");
            Complete(job, JobState.Failed, smoke.FailureReason ?? "Smoke test failed.", result);
        }
    }

    private void ExecuteRun(BuildJob job, CatalogEntry entry, CancellationToken token)
    {
        string factorio;
        try { factorio = RequireFactorio(job).Path; }
        catch (FmsException ex)
        {
            Log(job, "error: " + ex.Message);
            Complete(job, JobState.Failed, ex.Message, null);
            return;
        }

        LogUsing(job);
        var (context, profileName, profileConfig) = ContextFor(entry, job.Factorio!.ModVersion);
        var prepared = ProfileBuilder.Prepare(context, profileName);
        LogPrepared(job, prepared);

        var args = new List<string> { "--mod-directory", prepared.ModsDirectory };
        if (profileConfig.Save is { Length: > 0 } save)
        {
            args.Add("--load-game");
            args.Add(context.ResolvePath(save));
        }
        args.AddRange(profileConfig.Args);

        token.ThrowIfCancellationRequested();
        Log(job, $"Starting {factorio}");
        var process = _runner.Run(factorio, args, TimeSpan.FromDays(365), line => Log(job, line), token);

        if (process.Cancelled)
        {
            Log(job, "Factorio was stopped.");
            Complete(job, JobState.Cancelled, null, null);
            return;
        }

        var result = new RunJobResult(process.ExitCode);
        Log(job, $"Factorio exited with code {process.ExitCode}.");
        if (process.ExitCode == 0) Complete(job, JobState.Succeeded, null, result);
        else Complete(job, JobState.Failed, $"Factorio exited with code {process.ExitCode}.", result);
    }
}
