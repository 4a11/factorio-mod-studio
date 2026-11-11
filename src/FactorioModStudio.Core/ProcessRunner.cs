using System.ComponentModel;
using System.Diagnostics;

namespace FactorioModStudio.Core;

/// <param name="Cancelled">True when the process was killed because cancellation was requested.</param>
public sealed record ProcessResult(int ExitCode, bool TimedOut, IReadOnlyList<string> Lines, bool Cancelled = false);

/// <summary>Abstraction over starting a process, so tests can fake Factorio.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="executable"/>, captures stdout and stderr line by line, kills it after <paramref name="timeout"/>.</summary>
    ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine = null);
}

/// <summary>A runner that can also stop a running process (and its child processes) on request.</summary>
public interface ICancellableProcessRunner : IProcessRunner
{
    /// <summary>Like <see cref="IProcessRunner.Run"/>; when <paramref name="cancellationToken"/> is cancelled the process tree is killed and the result has <c>Cancelled = true</c>.</summary>
    ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : ICancellableProcessRunner
{
    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine = null) =>
        Run(executable, arguments, timeout, onLine, CancellationToken.None);

    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new ProcessResult(-1, false, Array.Empty<string>(), Cancelled: true);

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        SteamLaunch.Apply(startInfo); // the Steam copy of Factorio would otherwise restart itself through Steam and exit at once

        var lines = new List<string>();
        var gate = new object();

        using var process = new Process { StartInfo = startInfo };
        DataReceivedEventHandler handler = (_, e) =>
        {
            if (e.Data is null) return;
            lock (gate) lines.Add(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.OutputDataReceived += handler;
        process.ErrorDataReceived += handler;

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new FmsException($"Could not start \"{executable}\": {ex.Message}", ex);
        }

        var cancelled = false;
        using var registration = cancellationToken.Register(() =>
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
        });

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeoutMs = (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            lock (gate) return new ProcessResult(-1, !cancelled, lines.ToList(), cancelled);
        }

        process.WaitForExit(); // flush the asynchronous output readers
        lock (gate) return new ProcessResult(cancelled ? -1 : process.ExitCode, false, lines.ToList(), cancelled);
    }
}
