namespace FactorioModStudio.Core;

public sealed record SmokeOptions(int Ticks = 600, int Seed = 1, TimeSpan? StepTimeout = null);

public sealed record SmokeResult(bool Passed, string? FailureReason, LogReport Report, IReadOnlyList<string> Lines, bool Cancelled = false);

/// <summary>
/// Headless smoke test: load the mods (by generating a map, or loading the profile's save) and simulate N ticks.
/// It fails on a non-zero exit code, a timeout or any error in the log. This catches data-stage failures
/// and errors thrown from <c>control.lua</c> during the first ticks; it is not a replacement for real unit tests.
/// </summary>
public static class SmokeTest
{
    public static SmokeResult Run(
        string factorioExecutable,
        ProjectContext context,
        PreparedProfile profile,
        ProfileConfig profileConfig,
        SmokeOptions options,
        IProcessRunner runner,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = options.StepTimeout ?? TimeSpan.FromMinutes(5);
        var allLines = new List<string>();

        string save;
        if (profileConfig.Save is { Length: > 0 } configured)
        {
            save = context.ResolvePath(configured);
            if (!File.Exists(save)) throw new FmsException($"Save file \"{save}\" from profile \"{profile.Name}\" does not exist.");
        }
        else
        {
            save = Path.Combine(profile.Directory, "smoke-save.zip");
            if (File.Exists(save)) File.Delete(save);

            if (cancellationToken.IsCancellationRequested) return Finish(false, CancelledReason, allLines, cancelled: true);

            var create = Run(runner, factorioExecutable, timeout, onLine, allLines, cancellationToken,
                "--create", save,
                "--map-gen-seed", options.Seed.ToString(),
                "--mod-directory", profile.ModsDirectory,
                "--disable-audio");

            if (create.Cancelled) return Finish(false, CancelledReason, allLines, cancelled: true);
            var failure = Failure("Map generation", create);
            if (failure is not null) return Finish(false, failure, allLines);
        }

        var args = new List<string>
        {
            "--benchmark", save,
            "--benchmark-ticks", options.Ticks.ToString(),
            "--mod-directory", profile.ModsDirectory,
            "--disable-audio"
        };
        args.AddRange(profileConfig.Args);

        if (cancellationToken.IsCancellationRequested) return Finish(false, CancelledReason, allLines, cancelled: true);
        var bench = Run(runner, factorioExecutable, timeout, onLine, allLines, cancellationToken, args.ToArray());
        if (bench.Cancelled) return Finish(false, CancelledReason, allLines, cancelled: true);
        var benchFailure = Failure($"Simulation of {options.Ticks} ticks", bench);
        return Finish(benchFailure is null, benchFailure, allLines);
    }

    private const string CancelledReason = "Cancelled.";

    private static ProcessResult Run(IProcessRunner runner, string exe, TimeSpan timeout, Action<string>? onLine,
        List<string> sink, CancellationToken cancellationToken, params string[] args)
    {
        var result = runner is ICancellableProcessRunner cancellable
            ? cancellable.Run(exe, args, timeout, onLine, cancellationToken)
            : runner.Run(exe, args, timeout, onLine);
        sink.AddRange(result.Lines);
        return result;
    }

    private static string? Failure(string step, ProcessResult result)
    {
        if (result.TimedOut) return $"{step} timed out.";
        if (result.ExitCode != 0) return $"{step} failed (Factorio exit code {result.ExitCode}).";
        return null;
    }

    private static SmokeResult Finish(bool ok, string? reason, List<string> lines, bool cancelled = false)
    {
        var report = LogParser.Parse(lines);
        if (cancelled) return new SmokeResult(false, reason, report, lines, Cancelled: true);
        if (ok && report.HasErrors) return new SmokeResult(false, $"{report.ErrorCount} error(s) found in the Factorio log.", report, lines);
        return new SmokeResult(ok, reason, report, lines);
    }
}
