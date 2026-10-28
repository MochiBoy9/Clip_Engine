using System.Diagnostics;

namespace ClipEngine.Desktop.Services;

internal enum EngineState { Stopped, Starting, Running, Missing, Unsupported }

// Runs clip_engine.exe without a console window and turns its output into events.
internal sealed class EngineHost : IDisposable
{
    // Must match the names in src/app/App.cpp.
    private const string SaveEventName = @"Local\ClipEngine.SaveClip";
    private const string QuitEventName = @"Local\ClipEngine.Quit";
    private const string RecordEventName = @"Local\ClipEngine.ToggleRecording";

    private readonly object _gate = new();
    private Process? _process;
    private bool _stopping;

    public event Action<LogLine>? LineReceived;
    public event Action<EngineEvent>? EventReceived;
    public event Action<EngineState, string?>? StateChanged;

    public EngineState State { get; private set; } = EngineState.Stopped;

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            SetState(EngineState.Unsupported, "The recording engine runs on Windows 10 (1903) or newer.");
            return;
        }
        if (!File.Exists(AppPaths.EngineExe))
        {
            SetState(EngineState.Missing, $"clip_engine.exe wasn't found next to the app in {AppContext.BaseDirectory}");
            return;
        }

        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            _stopping = false;
            var info = new ProcessStartInfo(AppPaths.EngineExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnOutput(e.Data);
            process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
            process.Exited += (_, _) => OnExited(process);

            SetState(EngineState.Starting, null);
            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                SetState(EngineState.Stopped, $"Couldn't start the engine: {e.Message}");
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
        }
    }

    // Asks the engine to finish any clip it is writing and exit, then waits for it.
    public async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _stopping = true;
        }
        if (process is null || process.HasExited) return;

        Signal(QuitEventName);
        var exited = process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false) != exited)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Start();
    }

    public bool SaveClip() => Signal(SaveEventName);

    // Starts a long recording, or stops the one running.
    public bool ToggleLongRecording() => Signal(RecordEventName);

    private static bool Signal(string name)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private void OnOutput(string? data)
    {
        if (data is null) return;
        var line = EngineLogParser.ParseLine(data);
        if (line is null) return;

        if (State == EngineState.Starting) SetState(EngineState.Running, null);
        LineReceived?.Invoke(line);
        if (EngineLogParser.ParseEvent(line) is { } engineEvent) EventReceived?.Invoke(engineEvent);
    }

    private void OnExited(Process process)
    {
        string? reason = null;
        lock (_gate)
        {
            if (!_stopping)
            {
                reason = process.ExitCode == 1
                    ? "The engine stopped. Another copy may already be running; check the log for details."
                    : $"The engine stopped unexpectedly (exit code {process.ExitCode}).";
            }
            if (ReferenceEquals(_process, process)) _process = null;
        }
        SetState(EngineState.Stopped, reason);
    }

    private void SetState(EngineState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    public void Dispose()
    {
        StopAsync().Wait(TimeSpan.FromSeconds(10));
    }
}
