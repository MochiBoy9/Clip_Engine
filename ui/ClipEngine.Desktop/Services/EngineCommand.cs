using System.Diagnostics;
using System.Text;

namespace ClipEngine.Desktop.Services;

// Runs clip_engine.exe for a one-off job (--export, --trim) alongside the recording engine.
internal static class EngineCommand
{
    // Reports what the engine logs as it works. Returns its exit code and the first error it logged.
    public static async Task<(int ExitCode, string? Failure)> RunAsync(IEnumerable<string> arguments, Action<EngineEvent> onEvent,
                                                                     CancellationToken cancel = default)
    {
        var info = new ProcessStartInfo(AppPaths.EngineExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        string? failure = null;
        void OnLine(string? data)
        {
            if (data is null || EngineLogParser.ParseLine(data) is not { } line) return;
            if (line.Level == LogLevel.Error) failure ??= line.Message;
            if (EngineLogParser.ParseEvent(line) is { } e) onEvent(e);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, failure);
    }
}
