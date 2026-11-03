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

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Entry point with injectable streams and process runner, so it can be tested.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error, IProcessRunner? runner = null)
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
                "smoke" => Smoke(cmd, output, error, runner),
                "factorio" => FactorioCommand(cmd, output),
                "ui" => Ui(cmd, output),
                "new" => New(cmd, output, error),
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
                throw new FmsException("Usage: fmstudio new <name> [--template <id>] [--title T] [--author A] [--description D] [--factorio-version <MAJOR.MINOR>] [--out <dir>] [--json]  (see `fmstudio new --list`)");
            if (cmd.Positional.Count > 1)
                throw new FmsException($"Unexpected argument \"{cmd.Positional[1]}\". Quote values that contain spaces, e.g. --title \"My Mod\".");

            var parent = cmd.Get("--out", "-o") is { } o ? Path.GetFullPath(o) : LoadProject(cmd).WorkspaceDirectory;
            var request = new ScaffoldRequest(
                cmd.Positional[0], cmd.Get("--template") ?? "empty", cmd.Get("--title"), cmd.Get("--author"), cmd.Get("--description"),
                cmd.Get("--factorio-version") ?? ModScaffolder.DefaultFactorioVersion);
            var result = ModScaffolder.Create(request, parent);

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
        var factorio = ChooseFactorio(cmd, context, output);

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

    private static string VersionText(string? version) => version ?? "unknown version";

    /// <summary>
    /// <c>--factorio</c> is used exactly as given (a version mismatch only prints a warning); otherwise the installation that matches the
    /// mod's factorio_version is picked from FACTORIO_PATH, fmstudio.json, the UI's list and the usual folders.
    /// </summary>
    private static string ChooseFactorio(CommandLine cmd, ProjectContext context, TextWriter log)
    {
        var modVersion = ProjectModFactorioVersion(context);
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
            var pick = FactorioInstallations.Pick(candidates, modVersion);
            if (pick.Chosen is null)
            {
                var unusable = FactorioInstallations.UnusableConfiguredSources(context);
                var note = unusable.Count > 0 ? $" Not usable: {string.Join(", ", unusable)}." : "";
                throw new FmsException($"{pick.Reason}{note} Pass --factorio <path>, set {FactorioLocator.EnvironmentVariable}, or set \"factorioPath\" in {ProjectConfig.FileName}.");
            }

            path = pick.Chosen.Path;
            version = pick.Chosen.Version;
            log.WriteLine($"Using Factorio {VersionText(version)} ({path})");
        }

        if (FactorioInstallations.NormaliseMajorMinor(modVersion) is { } wanted && version is not null && !FactorioInstallations.VersionMatches(version, wanted))
            log.WriteLine($"warning: the mod targets Factorio {wanted} but this installation is {version}.");
        return path;
    }

    private static int FactorioCommand(CommandLine cmd, TextWriter output)
    {
        var context = LoadProject(cmd);
        var candidates = FactorioInstallations.Candidates(context, new FactorioSettings(context.StateDirectory).Read());
        var modVersion = ProjectModFactorioVersion(context);
        var pick = FactorioInstallations.Pick(candidates, modVersion);

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

    private static int Smoke(CommandLine cmd, TextWriter output, TextWriter error, IProcessRunner runner)
    {
        var context = LoadProject(cmd);
        var name = cmd.Positional.FirstOrDefault() ?? "default";
        var profileConfig = context.GetProfile(name);

        var json = cmd.Has("--json");
        var log = json ? error : output; // keep stdout clean for JSON consumers
        var factorio = ChooseFactorio(cmd, context, log);

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
                                 [--out <folder, default: the workspace>] [--json]   `new --list` shows the templates
          factorio               List the Factorio installations found and the one chosen for the project's mod  [--json]
          validate [mod-dir]     Check info.json, changelog, thumbnail and file references
          pack                   Validate and build NAME_VERSION.zip   [--out dir] [--force]
          prepare [profile]      Build the isolated mods folder for a profile
          run [profile]          Prepare a profile and start Factorio with it
          log <file>             Summarize errors/warnings from a Factorio log  [--json] [--verbose]
          smoke [profile]        Headless test: load the mods, simulate ticks, fail on log errors
                                 [--ticks N] [--seed N] [--timeout MINUTES] [--json]
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
