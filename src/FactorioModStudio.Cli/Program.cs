using System.Diagnostics;
using System.Text.Json;
using FactorioModStudio.Core;
using FactorioModStudio.Server;

namespace FactorioModStudio.Cli;

public static class Program
{
    public const int ExitOk = 0;
    public const int ExitProblemsFound = 1;
    public const int ExitUsageError = 2;

    public static int Main(string[] args)
    {
        // What the last update left behind (the old program files) can go now that they no longer run.
        StudioUpdates.CleanUp();

        // A double click in Explorer: open the studio instead of printing the help into a window that closes at once.
        if (args.Length > 0 || !ConsoleWindow.IsOwn()) return Run(args, Console.Out, Console.Error);

        // The release archives ship the studio's desktop window next to this program: open that rather than a browser tab.
        if (StartDesktopApp()) return ExitOk;

        var exitCode = Run(["ui", "--create-workspace"], Console.Out, Console.Error);
        if (exitCode != ExitOk)
        {
            Console.WriteLine("Press Enter to close this window.");
            Console.ReadLine();
        }
        return exitCode;
    }

    /// <summary>Starts FactorioModStudio(.exe) from this program's folder in the current folder; false when it is not there.</summary>
    private static bool StartDesktopApp()
    {
        var app = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "FactorioModStudio.exe" : "FactorioModStudio");
        if (!File.Exists(app)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(app) { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Entry point with injectable streams, process runner and HTTP client, so it can be tested.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error, IProcessRunner? runner = null, HttpClient? http = null)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                output.WriteLine(HelpText);
                return args.Length == 0 ? ExitUsageError : ExitOk;
            }

            if (args[0] is "--version")
            {
                output.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown");
                return ExitOk;
            }

            var command = args[0];
            var cmd = CommandLine.Parse(args.Skip(1));
            runner ??= new ProcessRunner();

            return command switch
            {
                "init" => Init(cmd, output),
                "validate" => Validate(cmd, output),
                "pack" => Pack(cmd, output),
                "prepare" => Prepare(cmd, output),
                "run" => RunGame(cmd, output),
                "log" => Log(cmd, output),
                "smoke" => Smoke(cmd, output, error, runner, http),
                "factorio" => FactorioCommand(cmd, output, runner, http),
                "ui" => Ui(cmd, output),
                "new" => New(cmd, output, error),
                "add" => Add(cmd, output, error),
                "update" => Update(cmd, output, runner, http),
                _ => UnknownCommand(command, error)
            };
        }
        catch (FmsException ex)
        {
            error.WriteLine($"error: {ex.Message}");
            return ExitUsageError;
        }
    }

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"error: unknown command \"{command}\". Run `fmstudio help`.");
        return ExitUsageError;
    }

    private static ProjectContext LoadProject(CommandLine cmd) =>
        ProjectContext.Load(cmd.Get("--project", "-p") ?? Directory.GetCurrentDirectory());

    private static readonly JsonSerializerOptions NewJson = new(Reporting.Json)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // keep quotes and non-ASCII readable
    };

    private static int New(CommandLine cmd, TextWriter output, TextWriter error)
    {
        var json = cmd.Has("--json");
        try
        {
            if (cmd.Has("--list"))
            {
                if (json) output.WriteLine(JsonSerializer.Serialize(ModScaffolder.Templates, NewJson));
                else
                {
                    var width = ModScaffolder.Templates.Max(t => t.Id.Length);
                    foreach (var t in ModScaffolder.Templates)
                        output.WriteLine($"{t.Id.PadRight(width)}  {t.Title} - {t.Description} (Factorio {string.Join(", ", t.SupportedVersions)})");
                }
                return ExitOk;
            }

            if (cmd.Positional.Count == 0)
                throw new FmsException("Usage: fmstudio new <name> [--template <id>] [--title T] [--author A] [--description D] [--factorio-version <MAJOR.MINOR>] [--content block,block] [--out <dir>] [--json]  (see `fmstudio new --list`, `fmstudio add --list`)");
            if (cmd.Positional.Count > 1)
                throw new FmsException($"Unexpected argument \"{cmd.Positional[1]}\". Quote values that contain spaces, e.g. --title \"My Mod\".");

            var parent = cmd.Get("--out", "-o") is { } o ? Path.GetFullPath(o) : LoadProject(cmd).WorkspaceDirectory;
            var request = new ScaffoldRequest(
                cmd.Positional[0], cmd.Get("--template") ?? "empty", cmd.Get("--title"), cmd.Get("--author"), cmd.Get("--description"),
                cmd.Get("--factorio-version") ?? ModScaffolder.DefaultFactorioVersion,
                cmd.Get("--content")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var result = ModScaffolder.Create(request with { GameDataDirectory = GameData(cmd) }, parent);

            if (json)
            {
                output.WriteLine(JsonSerializer.Serialize(
                    new { result.Name, result.Version, Template = request.Template, FactorioVersion = request.FactorioVersion, result.Folder, result.Files }, NewJson));
                return ExitOk;
            }

            output.WriteLine($"Created mod \"{result.Name}\" {result.Version} (template: {request.Template}, Factorio {request.FactorioVersion})");
            output.WriteLine($"  {result.Folder}");
            foreach (var file in result.Files) output.WriteLine($"    {file}");
            output.WriteLine();
            output.WriteLine("Next steps:");
            output.WriteLine($"  fmstudio validate \"{result.Folder}\"   check the new mod");
            output.WriteLine("  fmstudio ui                              browse and edit it in the browser");
            return ExitOk;
        }
        catch (FmsException ex)
        {
            if (json)
            {
                var kind = ex is ScaffoldException s ? s.Kind.ToString() : "usage";
                error.WriteLine(JsonSerializer.Serialize(new { Error = ex.Message, Kind = char.ToLowerInvariant(kind[0]) + kind[1..] }, NewJson));
            }
            else error.WriteLine($"error: {ex.Message}");
            return ExitUsageError;
        }
    }

    private static int Add(CommandLine cmd, TextWriter output, TextWriter error)
    {
        var json = cmd.Has("--json");
        try
        {
            if (cmd.Has("--list"))
            {
                if (json) output.WriteLine(JsonSerializer.Serialize(ModContent.Blocks, NewJson));
                else
                {
                    foreach (var group in ModContent.Blocks.GroupBy(b => b.Category))
                    {
                        output.WriteLine($"{group.Key}:");
                        foreach (var b in group) output.WriteLine($"  {b.Id.PadRight(10)} named <mod>-{b.DefaultName} unless you give a name (Factorio {string.Join(", ", b.SupportedVersions)})");
                    }
                }
                return ExitOk;
            }

            if (cmd.Positional.Count is 0 or > 2)
                throw new FmsException("Usage: fmstudio add <block> [name] [--title T] [--dry-run] [--json]  (see `fmstudio add --list`)");

            var modDirectory = LoadProject(cmd).ModDirectory;
            var blockId = cmd.Positional[0];
            var name = cmd.Positional.Count == 2 ? cmd.Positional[1] : DefaultContentName(modDirectory, blockId);
            var request = new ContentRequest(blockId, name, cmd.Get("--title"));
            var dryRun = cmd.Has("--dry-run");
            var gameData = GameData(cmd);
            var plan = dryRun ? ModContent.Plan(modDirectory, request, gameData) : ModContent.Apply(modDirectory, request, gameData);

            if (json)
            {
                output.WriteLine(JsonSerializer.Serialize(new { plan.Block, plan.Name, plan.Title, plan.Create, plan.Change, plan.Prototypes, DryRun = dryRun }, NewJson));
                return ExitOk;
            }

            output.WriteLine(dryRun ? $"Would add {plan.Block} \"{plan.Name}\" ({plan.Title}):" : $"Added {plan.Block} \"{plan.Name}\" ({plan.Title}):");
            foreach (var file in plan.Create) output.WriteLine($"  + {file}");
            foreach (var file in plan.Change) output.WriteLine($"  ~ {file}");
            output.WriteLine($"Prototypes: {string.Join(", ", plan.Prototypes)}");
            return ExitOk;
        }
        catch (FmsException ex)
        {
            if (json)
            {
                var kind = ex is ContentException c ? c.Kind.ToString() : "usage";
                error.WriteLine(JsonSerializer.Serialize(new { Error = ex.Message, Kind = char.ToLowerInvariant(kind[0]) + kind[1..] }, NewJson));
            }
            else error.WriteLine($"error: {ex.Message}");
            return ExitUsageError;
        }
    }

    /// <summary>The data folder of --factorio or of the newest Factorio found; null without one (icons are then tinted in the game).</summary>
    private static string? GameData(CommandLine cmd)
    {
        try
        {
            var context = LoadProject(cmd);
            var configured = context.Config.FactorioPath is { } p ? context.ResolvePath(p) : null;
            var factorio = cmd.Get("--factorio") is { } explicitPath
                ? FactorioLocator.Locate(explicitPath, configured)
                : FactorioInstallations.Pick(FactorioInstallations.Candidates(context, new FactorioSettings(context.StateDirectory).Read()), null).Chosen?.Path;
            return ModCatalog.GameDataFor(factorio);
        }
        catch (FmsException) { return null; }
    }

    /// <summary>"&lt;mod name&gt;-&lt;the block's default name&gt;", like the blocks of a new mod.</summary>
    private static string DefaultContentName(string modDirectory, string blockId)
    {
        string? modName = null;
        try { modName = ModInfo.Load(Path.Combine(modDirectory, "info.json")).Name; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* reported by ModContent */ }
        var suffix = ModContent.Blocks.FirstOrDefault(b => b.Id == blockId)?.DefaultName ?? blockId;
        return string.IsNullOrEmpty(modName) ? suffix : $"{modName}-{suffix}";
    }

    private static int Init(CommandLine cmd, TextWriter output)
    {
        var root = Path.GetFullPath(cmd.Get("--project", "-p") ?? Directory.GetCurrentDirectory());
        var path = Path.Combine(root, ProjectConfig.FileName);
        if (File.Exists(path))
        {
            output.WriteLine($"{ProjectConfig.FileName} already exists; leaving it unchanged.");
            return ExitOk;
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(path, ProjectConfig.Template + Environment.NewLine);
        output.WriteLine($"Created {path}");
        output.WriteLine("Add `.fmstudio/` and `dist/` to your .gitignore.");
        return ExitOk;
    }

    private static int Validate(CommandLine cmd, TextWriter output)
    {
        var modDir = cmd.Positional.FirstOrDefault() is { } given
            ? Path.GetFullPath(given)
            : LoadProject(cmd).ModDirectory;

        var result = ModValidator.Validate(modDir);

        if (cmd.Has("--json"))
            output.WriteLine(JsonSerializer.Serialize(new { result.ErrorCount, result.WarningCount, result.Issues }, Reporting.Json));
        else
            Reporting.PrintValidation(output, result);

        return result.HasErrors ? ExitProblemsFound : ExitOk;
    }

    private static int Pack(CommandLine cmd, TextWriter output)
    {
        var context = LoadProject(cmd);
        var outDir = cmd.Get("--out", "-o") is { } o ? Path.GetFullPath(o) : Path.Combine(context.Root, "dist");

        var result = ModPackager.Pack(context.ModDirectory, outDir, cmd.Has("--force"));
        Reporting.PrintValidation(output, result.Validation);

        if (!result.Success)
        {
            output.WriteLine(result.Validation.HasErrors
                ? "Not packed: fix the errors above (or use --force)."
                : "Not packed.");
            return ExitProblemsFound;
        }

        output.WriteLine($"Packed {result.FileCount} file(s) into {result.ArchivePath} ({result.ArchiveBytes / 1024.0:F1} KiB).");
        return ExitOk;
    }

    private static int Prepare(CommandLine cmd, TextWriter output)
    {
        var context = LoadProject(cmd);
        var name = cmd.Positional.FirstOrDefault() ?? "default";
        var prepared = ProfileBuilder.Prepare(context, name);
        PrintPrepared(output, prepared);
        return ExitOk;
    }

    private static void PrintPrepared(TextWriter output, PreparedProfile prepared)
    {
        output.WriteLine($"Profile \"{prepared.Name}\" ready: {prepared.ModsDirectory}");
        output.WriteLine($"Enabled mods: {string.Join(", ", prepared.EnabledMods)}");
        foreach (var warning in prepared.Warnings) output.WriteLine($"warning: {warning}");
    }

    private static int RunGame(CommandLine cmd, TextWriter output)
    {
        var context = LoadProject(cmd);
        var name = cmd.Positional.FirstOrDefault() ?? "default";
        var profileConfig = context.GetProfile(name);
        if (cmd.Has("--install"))
            throw new FmsException("--install downloads Factorio's headless server, which has no graphics; use it with `fmstudio smoke`.");
        var factorio = ChooseFactorio(cmd, context, output, play: true);

        var prepared = ProfileBuilder.Prepare(context, name);
        PrintPrepared(output, prepared);

        var startInfo = new ProcessStartInfo(factorio) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--mod-directory");
        startInfo.ArgumentList.Add(prepared.ModsDirectory);
        if (profileConfig.Save is { Length: > 0 } save)
        {
            startInfo.ArgumentList.Add("--load-game");
            startInfo.ArgumentList.Add(context.ResolvePath(save));
        }
        foreach (var arg in profileConfig.Args) startInfo.ArgumentList.Add(arg);

        output.WriteLine($"Starting {factorio}");
        try
        {
            using var process = Process.Start(startInfo) ?? throw new FmsException("Factorio did not start.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FmsException($"Could not start \"{factorio}\": {ex.Message}", ex);
        }
    }

    /// <summary>The target Factorio version of the project's mod (info.json), or null when it cannot be read.</summary>
    private static string? ProjectModFactorioVersion(ProjectContext context)
    {
        try { return ModInfo.Load(Path.Combine(context.ModDirectory, "info.json")).FactorioVersion; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The project's mod's constraint on the game ("base &gt;= 2.0.20"), or null when there is none or info.json cannot be read.</summary>
    private static Dependency? ProjectModBaseRequirement(ProjectContext context)
    {
        try { return FactorioInstallations.BaseRequirement(ModInfo.Load(Path.Combine(context.ModDirectory, "info.json")).ParsedDependencies); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FmsException) { return null; }
    }

    private static string VersionText(string? version) => version ?? "unknown version";

    /// <summary>
    /// <c>--factorio</c> is used exactly as given (a version mismatch only prints a warning); otherwise the installation that matches the
    /// mod's factorio_version is picked from FACTORIO_PATH, fmstudio.json, the UI's list and the usual folders.
    /// <paramref name="play"/>: the game itself is started, so the studio's headless servers do not count.
    /// <paramref name="installMissing"/>: when nothing matches, called with what to install (the mod's MAJOR.MINOR, or the exact release its
    /// base dependency pins, see <see cref="FactorioInstallations.InstallSpec"/>); returns the executable.
    /// </summary>
    private static string ChooseFactorio(CommandLine cmd, ProjectContext context, TextWriter log, bool play = false, Func<string, string>? installMissing = null)
    {
        var modVersion = ProjectModFactorioVersion(context);
        var baseRequirement = ProjectModBaseRequirement(context);
        string path;
        string? version;

        if (cmd.Get("--factorio") is { } explicitPath)
        {
            path = FactorioLocator.Locate(explicitPath, context.Config.FactorioPath is { } p ? context.ResolvePath(p) : null);
            version = FactorioInstallations.DetectVersion(path);
            log.WriteLine($"Using Factorio {VersionText(version)} ({path})");
        }
        else
        {
            var candidates = FactorioInstallations.Candidates(context, new FactorioSettings(context.StateDirectory).Read());
            var pick = play
                ? FactorioInstallations.PickToPlay(candidates, modVersion, baseRequirement: baseRequirement)
                : FactorioInstallations.Pick(candidates, modVersion, baseRequirement: baseRequirement);
            var branch = FactorioInstallations.InstallSpec(modVersion, baseRequirement);
            if (pick.Chosen is null && installMissing is not null && branch is not null)
            {
                log.WriteLine($"{pick.Reason} Installing it, as --install asks.");
                path = installMissing(branch);
                version = FactorioInstallations.DetectVersion(path);
                log.WriteLine($"Using Factorio {VersionText(version)} ({path})");
            }
            else if (pick.Chosen is null)
            {
                var unusable = FactorioInstallations.UnusableConfiguredSources(context);
                var note = unusable.Count > 0 ? $" Not usable: {string.Join(", ", unusable)}." : "";
                // Offer the download only where it can work: the headless server, for smoke tests, on Linux x64.
                var install = !play && branch is not null && FactorioDownloads.HeadlessPlatformProblem() is null
                    ? $" Install it with `fmstudio factorio install {branch}` or rerun with --install;"
                    : "";
                throw new FmsException($"{pick.Reason}{note}{install} Pass --factorio <path>, set {FactorioLocator.EnvironmentVariable}, or set \"factorioPath\" in {ProjectConfig.FileName}.");
            }
            else
            {
                path = pick.Chosen.Path;
                version = pick.Chosen.Version;
                log.WriteLine($"Using Factorio {VersionText(version)} ({path})");
            }
        }

        if (FactorioInstallations.NormaliseMajorMinor(modVersion) is { } wanted && version is not null && !FactorioInstallations.VersionMatches(version, wanted))
            log.WriteLine($"warning: the mod targets Factorio {wanted} but this installation is {version}.");
        else if (version is not null && !FactorioInstallations.SatisfiesBase(version, baseRequirement))
            log.WriteLine($"warning: the mod needs {baseRequirement} but this installation is {version}.");
        return path;
    }

    private static int FactorioCommand(CommandLine cmd, TextWriter output, IProcessRunner runner, HttpClient? http)
    {
        switch (cmd.Positional.FirstOrDefault())
        {
            case null: return ListFactorio(cmd, output);
            case "available": return FactorioAvailable(cmd, output, Downloads(runner, http));
            case "install": return FactorioInstall(cmd, output, Downloads(runner, http));
            case "remove": return FactorioRemove(cmd, output, Downloads(runner, http));
            default:
                throw new FmsException($"Unknown \"factorio {cmd.Positional[0]}\". Use `fmstudio factorio` (list), `factorio available`, `factorio install <version>` or `factorio remove <version>`.");
        }
    }

    private static FactorioDownloads Downloads(IProcessRunner runner, HttpClient? http)
    {
        http ??= NewHttpClient();
        return new FactorioDownloads(http, runner, FactorioInstallations.ManagedInstallationsDirectory());
    }

    private static AvailableReleases FetchReleases(FactorioDownloads downloads)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { return downloads.AvailableAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw new FmsException("Factorio's updater did not answer within 30 seconds."); }
    }

    /// <summary><c>fmstudio factorio available [--all] [--json]</c>: the headless releases, newest of each branch unless --all.</summary>
    private static int FactorioAvailable(CommandLine cmd, TextWriter output, FactorioDownloads downloads)
    {
        var releases = FetchReleases(downloads);
        var installed = downloads.Installed().Select(i => i.Version).ToHashSet(StringComparer.Ordinal);
        var branches = releases.Versions.GroupBy(v => string.Join('.', v.Split('.').Take(2))).ToList();

        if (cmd.Has("--json"))
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                releases.Stable, releases.Experimental,
                branches = branches.Select(b => new { branch = b.Key, newest = b.First(), versions = b.ToList() }),
                installed,
                platformProblem = FactorioDownloads.HeadlessPlatformProblem()
            }, NewJson));
            return ExitOk;
        }

        output.WriteLine("Factorio headless server releases (Linux x64), from Factorio's updater:");
        output.WriteLine($"  stable {releases.Stable ?? "-"}, experimental {releases.Experimental ?? "-"}");
        output.WriteLine();
        foreach (var branch in branches)
        {
            var shown = cmd.Has("--all") ? branch.ToList() : new List<string> { branch.First() };
            var text = string.Join(", ", shown.Select(v => v + (installed.Contains(v) ? " (installed)" : "")));
            output.WriteLine($"  {branch.Key.PadRight(5)} {text}{(cmd.Has("--all") ? "" : $"   ({branch.Count()} releases)")}");
        }
        output.WriteLine();
        output.WriteLine("Install one with `fmstudio factorio install <MAJOR.MINOR | MAJOR.MINOR.PATCH | stable>`; `--all` lists every release.");
        if (FactorioDownloads.HeadlessPlatformProblem() is { } problem) output.WriteLine($"note: {problem}");
        return ExitOk;
    }

    /// <summary>
    /// <c>fmstudio factorio install [version] [--json]</c>: without a version, the newest release of the project mod's factorio_version.
    /// </summary>
    private static int FactorioInstall(CommandLine cmd, TextWriter output, FactorioDownloads downloads)
    {
        var spec = cmd.Positional.Skip(1).FirstOrDefault();
        if (spec is null)
        {
            var project = LoadProject(cmd);
            spec = FactorioInstallations.InstallSpec(ProjectModFactorioVersion(project), ProjectModBaseRequirement(project))
                   ?? throw new FmsException("Usage: fmstudio factorio install <MAJOR.MINOR | MAJOR.MINOR.PATCH | stable | latest>. Without a version, the project's mod must declare factorio_version.");
            if (!cmd.Has("--json")) output.WriteLine($"The project's mod needs Factorio {spec}.");
        }

        // Check the platform before going to the network: on Windows and macOS nothing could be installed anyway.
        if (downloads.PlatformProblem is { } problem) throw new FmsException(problem);

        var releases = FetchReleases(downloads);
        var version = FactorioDownloads.Resolve(releases, spec)
                      ?? throw new FmsException($"Factorio has no headless release matching \"{spec}\". See `fmstudio factorio available`.");

        var json = cmd.Has("--json");
        var result = InstallWithProgress(downloads, version, json ? null : output);
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { result.Version, result.Path, result.Executable, result.AlreadyInstalled }, NewJson));
            return ExitOk;
        }

        output.WriteLine($"  {result.Path}");
        output.WriteLine($"smoke now uses it for mods with factorio_version {string.Join('.', version.Split('.').Take(2))} (see `fmstudio factorio`); run needs the game with graphics.");
        return ExitOk;
    }

    /// <summary>Installs one headless release, writing "Factorio 2.0.72 (headless): 10% 20% ... done." to <paramref name="log"/>.</summary>
    private static ManagedInstallation InstallWithProgress(FactorioDownloads downloads, string version, TextWriter? log)
    {
        var lastStep = -1;
        var progress = new SyncProgress<(long Received, long? Total)>(p =>
        {
            if (log is null) return;
            var step = p.Total is > 0 ? (int)(p.Received * 10 / p.Total.Value) : (int)(p.Received / (10L * 1024 * 1024));
            if (step == lastStep) return;
            lastStep = step;
            log.Write(p.Total is > 0 ? $" {step * 10}%" : $" {p.Received / (1024 * 1024)} MB");
        });

        log?.Write($"Factorio {version} (headless):");
        var result = downloads.InstallHeadlessAsync(version, progress).GetAwaiter().GetResult();
        log?.WriteLine(result.AlreadyInstalled ? " already installed." : " done.");
        return result;
    }

    /// <summary>
    /// <c>fmstudio update [--check] [--channel stable|preview] [--json]</c>: the newest release of the channel from GitHub; a copy
    /// unpacked from a release archive replaces its own files, a .NET tool is told the command. A given channel is remembered.
    /// </summary>
    private static int Update(CommandLine cmd, TextWriter output, IProcessRunner runner, HttpClient? http)
    {
        UpdateChannel channel;
        if (cmd.Get("--channel") is { } text)
        {
            channel = StudioUpdates.ParseChannel(text) ?? throw new FmsException($"\"{text}\" is not an update channel; use stable (releases: 0.3.0, 0.4.0) or preview (also 0.3.1, 0.3.2 ...).");
            UpdateSettings.WriteChannel(channel);
        }
        else channel = UpdateSettings.Channel(StudioUpdates.CurrentVersion);

        var updates = new StudioUpdates(http ?? NewHttpClient(), runner);
        UpdateCheck check;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try { check = updates.CheckAsync(channel, timeout.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new FmsException("GitHub did not answer within 30 seconds."); }
        }

        var json = cmd.Has("--json");
        var checkOnly = cmd.Has("--check");
        if (json && (checkOnly || check.Newer is null || check.Kind != InstallKind.ReleaseArchive))
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                check.Current, Channel = StudioUpdates.ChannelName(channel), Latest = check.Newer?.Version, check.Newer?.Url,
                Kind = check.Kind.ToString(), check.Command
            }, NewJson));
            return ExitOk;
        }

        if (!json) output.WriteLine($"Factorio Mod Studio {check.Current}, {StudioUpdates.ChannelName(channel)} channel.");
        if (check.Newer is not { } newer)
        {
            output.WriteLine("Up to date.");
            return ExitOk;
        }

        if (!json) output.WriteLine($"Version {newer.Version} is out: {newer.Url}");
        if (checkOnly || check.Kind != InstallKind.ReleaseArchive)
        {
            output.WriteLine(check.Kind switch
            {
                InstallKind.ReleaseArchive => "Run `fmstudio update` to install it.",
                InstallKind.DotnetTool => $"Update the .NET tool with `{check.Command}`.",
                _ => "This copy was built from source or installed by a package manager; update it the same way."
            });
            return ExitOk;
        }

        var lastStep = -1;
        var progress = new SyncProgress<(long Received, long? Total)>(p =>
        {
            if (json) return;
            var step = p.Total is > 0 ? (int)(p.Received * 10 / p.Total.Value) : (int)(p.Received / (10L * 1024 * 1024));
            if (step == lastStep) return;
            lastStep = step;
            output.Write(p.Total is > 0 ? $" {step * 10}%" : $" {p.Received / (1024 * 1024)} MB");
        });
        if (!json) output.Write($"Downloading {newer.Version}:");
        updates.ApplyAsync(newer, progress).GetAwaiter().GetResult();
        if (json) output.WriteLine(JsonSerializer.Serialize(new { check.Current, Updated = newer.Version }, NewJson));
        else
        {
            output.WriteLine(" done.");
            output.WriteLine($"Updated to {newer.Version}. Start fmstudio (or the window) again to use it.");
        }
        return ExitOk;
    }

    private static HttpClient NewHttpClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // downloads can take a while; checks have their own limit
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FactorioModStudio/{typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        return http;
    }

    /// <summary>For <c>smoke --install</c>: the newest headless release of <paramref name="branch"/> (or that exact release); returns its executable.</summary>
    private static string InstallBranch(string branch, TextWriter log, IProcessRunner runner, HttpClient? http)
    {
        var downloads = Downloads(runner, http);
        if (downloads.PlatformProblem is { } problem) throw new FmsException(problem);
        var version = FactorioDownloads.Resolve(FetchReleases(downloads), branch)
                      ?? throw new FmsException($"Factorio has no headless release of {branch}. See `fmstudio factorio available`.");
        return InstallWithProgress(downloads, version, log).Executable;
    }

    /// <summary><c>fmstudio factorio remove &lt;MAJOR.MINOR.PATCH&gt;</c>: only installations the studio made itself.</summary>
    private static int FactorioRemove(CommandLine cmd, TextWriter output, FactorioDownloads downloads)
    {
        var version = cmd.Positional.Skip(1).FirstOrDefault()
                      ?? throw new FmsException("Usage: fmstudio factorio remove <MAJOR.MINOR.PATCH>. Only versions installed with `fmstudio factorio install` can be removed.");
        if (!downloads.Remove(version))
        {
            output.WriteLine($"Factorio {version} was not installed by the studio (nothing in {downloads.Directory}).");
            return ExitProblemsFound;
        }
        output.WriteLine($"Removed Factorio {version} (headless).");
        return ExitOk;
    }

    /// <summary>Calls the handler on the reporting thread (Progress&lt;T&gt; would post to the thread pool and interleave the output).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static int ListFactorio(CommandLine cmd, TextWriter output)
    {
        var context = LoadProject(cmd);
        var candidates = FactorioInstallations.Candidates(context, new FactorioSettings(context.StateDirectory).Read());
        var modVersion = ProjectModFactorioVersion(context);
        var pick = FactorioInstallations.Pick(candidates, modVersion, baseRequirement: ProjectModBaseRequirement(context));

        if (cmd.Has("--json"))
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                installations = candidates.Select(c => new { c.Id, c.Version, c.Sources, c.Label, c.Path }),
                modFactorioVersion = modVersion,
                pick = new { pick.Mode, pick.Reason, ChosenId = pick.Chosen?.Id }
            }, NewJson));
            return ExitOk;
        }

        if (candidates.Count == 0) output.WriteLine("No Factorio installation found.");
        foreach (var c in candidates)
            output.WriteLine($"{c.Id}  {(c.Version ?? "unknown").PadRight(9)}  [{string.Join(",", c.Sources)}]  {c.Path}{(c.Label is null ? "" : $"  ({c.Label})")}");
        output.WriteLine();
        output.WriteLine(modVersion is null ? "The project's mod declares no factorio_version." : $"The project's mod targets Factorio {modVersion}.");
        output.WriteLine($"Pick: {pick.Mode} - {pick.Reason}");
        return ExitOk;
    }

    private static int Log(CommandLine cmd, TextWriter output)
    {
        var file = cmd.Positional.FirstOrDefault() ?? throw new FmsException("Usage: fmstudio log <path to factorio-current.log> [--json] [--verbose]");
        if (!File.Exists(file)) throw new FmsException($"Log file \"{file}\" not found.");

        var report = LogParser.ParseFile(file);
        if (cmd.Has("--json"))
            output.WriteLine(JsonSerializer.Serialize(new { report.ErrorCount, report.WarningCount, report.Diagnostics }, Reporting.Json));
        else
            Reporting.PrintLog(output, report, cmd.Has("--verbose", "-v"));

        return report.HasErrors ? ExitProblemsFound : ExitOk;
    }

    private static int Smoke(CommandLine cmd, TextWriter output, TextWriter error, IProcessRunner runner, HttpClient? http)
    {
        var context = LoadProject(cmd);
        var name = cmd.Positional.FirstOrDefault() ?? "default";
        var profileConfig = context.GetProfile(name);

        var json = cmd.Has("--json");
        var log = json ? error : output; // keep stdout clean for JSON consumers
        var factorio = ChooseFactorio(cmd, context, log,
            installMissing: cmd.Has("--install") ? branch => InstallBranch(branch, log, runner, http) : null);

        var validation = ModValidator.Validate(context.ModDirectory);
        if (validation.HasErrors)
        {
            Reporting.PrintValidation(log, validation);
            log.WriteLine("Smoke test not started: fix the validation errors first.");
            return ExitProblemsFound;
        }

        var prepared = ProfileBuilder.Prepare(context, name);
        log.WriteLine($"Profile \"{prepared.Name}\": {string.Join(", ", prepared.EnabledMods)}");
        foreach (var warning in prepared.Warnings) log.WriteLine($"warning: {warning}");

        var options = new SmokeOptions(
            Ticks: cmd.GetInt("--ticks") ?? 600,
            Seed: cmd.GetInt("--seed") ?? 1,
            StepTimeout: TimeSpan.FromMinutes(cmd.GetInt("--timeout") ?? 5));

        log.WriteLine($"Running Factorio headless for {options.Ticks} ticks...");
        var result = SmokeTest.Run(factorio, context, prepared, profileConfig, options, runner);

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(
                new { result.Passed, result.FailureReason, result.Report.ErrorCount, result.Report.WarningCount, result.Report.Diagnostics },
                Reporting.Json));
        }
        else
        {
            Reporting.PrintLog(output, result.Report, verbose: !result.Passed);
            // Factorio can stop with an exit code and nothing the log parser calls an error (a rejected option, say): show what it said.
            if (!result.Passed && !result.Cancelled && result.Report.ErrorCount == 0 && result.Lines.Count > 0)
            {
                output.WriteLine("Last lines from Factorio:");
                foreach (var line in result.Lines.TakeLast(20)) output.WriteLine($"  | {line.TrimEnd()}");
            }
            output.WriteLine(result.Passed ? "SMOKE TEST PASSED" : $"SMOKE TEST FAILED: {result.FailureReason}");
        }

        return result.Passed ? ExitOk : ExitProblemsFound;
    }


    private static int Ui(CommandLine cmd, TextWriter output)
    {
        var context = LoadProject(cmd);

        // Factorio is optional here: when found, the game's own data (base, core, ...) can be browsed as well.
        string? factorio = null;
        var configured = context.Config.FactorioPath is { } p ? context.ResolvePath(p) : null;
        if (cmd.Get("--factorio") is { } explicitPath)
            factorio = FactorioLocator.Locate(explicitPath, configured);
        else
        {
            // Any installation will do for browsing the game data: the newest one.
            try { factorio = FactorioInstallations.Pick(FactorioInstallations.Candidates(context, new FactorioSettings(context.StateDirectory).Read()), null).Chosen?.Path; }
            catch (FmsException) { /* not installed or not configured: fine */ }
        }

        var extra = cmd.Get("--mods") is { } folder ? new[] { Path.GetFullPath(folder) } : Array.Empty<string>();
        var workspace = cmd.Get("--workspace") is { } ws ? Path.GetFullPath(ws) : context.WorkspaceDirectory;
        // Set when started by a double click: the folder next to the program becomes the workspace right away.
        if (cmd.Has("--create-workspace")) Directory.CreateDirectory(workspace);
        var options = new UiOptions(cmd.GetInt("--port") ?? 5757, extra, factorio, workspace);

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        var ui = UiServer.StartAsync(context, options, cancel.Token).GetAwaiter().GetResult();
        output.WriteLine($"Factorio Mod Studio UI: {ui.Url}");
        output.WriteLine(Directory.Exists(workspace)
            ? $"Workspace: {workspace} (mods inside are discovered live and can be edited)"
            : $"Workspace folder not found: {workspace}. Create it and put mods inside; restart to watch it.");
        output.WriteLine("Press Ctrl+C to stop.");
        if (!cmd.Has("--no-browser")) BrowserLauncher.Open(ui.Url);

        try { Task.Delay(Timeout.Infinite, cancel.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { /* Ctrl+C */ }

        ui.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return ExitOk;
    }

    private const string HelpText = """
        fmstudio - toolkit for Factorio mod developers

        Usage: fmstudio <command> [arguments] [options]

        Commands:
          init                   Create fmstudio.json in the project folder
          new <name>             Create a mod from a template (empty, item-recipe, entity, runtime-script)
                                 [--template <id>] [--title T] [--author A] [--description D] [--factorio-version <MAJOR.MINOR, default 2.0>]
                                 [--content block,block: building blocks to add right away (Factorio 2.0+)]
                                 [--out <folder, default: the workspace>] [--json]   `new --list` shows the templates
          add <block> [name]     Add a building block (armor, enemy, belt tier, assembler, car, ...) to the project mod
                                 [--title T] [--dry-run] [--json]   `add --list` shows the blocks; name: <mod>-<block>
                                 Writes prototypes/<name>.lua and appends to data.lua and locale/en; never overwrites
          factorio               List the Factorio installations found and the one chosen for the project's mod  [--json]
          factorio available     Factorio's headless server releases (Linux x64)  [--all] [--json]
          factorio install [v]   Download a headless server release into the studio's folder, where smoke finds it
                                 v: MAJOR.MINOR (newest of the branch), MAJOR.MINOR.PATCH, stable or latest;
                                 without v: the project mod's factorio_version   [--json]
          factorio remove <v>    Delete a release installed with `factorio install`
          validate [mod-dir]     Check info.json, changelog, thumbnail and file references
          pack                   Validate and build NAME_VERSION.zip   [--out dir] [--force]
          prepare [profile]      Build the isolated mods folder for a profile
          run [profile]          Prepare a profile and start Factorio with it
          log <file>             Summarize errors/warnings from a Factorio log  [--json] [--verbose]
          smoke [profile]        Headless test: load the mods, simulate ticks, fail on log errors
                                 [--ticks N] [--seed N] [--timeout MINUTES] [--json]
                                 [--install: no matching Factorio? download the headless server first (Linux)]
          update                 Install the newest version from GitHub (a copy from a release archive updates itself;
                                 a .NET tool is told the command)   [--check: only say whether one is out] [--json]
                                 [--channel stable|preview: releases only (0.3.0, 0.4.0) or also the previews
                                 between them (0.3.1, 0.3.2 ...); remembered. Default: the running version's]
          ui                     Open the graphical mod browser in your web browser
                                 [--port N] [--workspace <mods folder to edit>] [--mods <installed mods, read-only>]
                                 [--no-browser]

        Common options:
          --project, -p <dir>    Project folder (default: current directory)
          --factorio <path>      Factorio executable or install folder for run/smoke. Without it, the installation whose version
                                 matches the mod's factorio_version is chosen automatically from FACTORIO_PATH, "factorioPath" in
                                 fmstudio.json, those added in the UI and the usual folders (see `fmstudio factorio`)

        Exit codes: 0 ok, 1 problems found, 2 usage or configuration error.
        """;
}
