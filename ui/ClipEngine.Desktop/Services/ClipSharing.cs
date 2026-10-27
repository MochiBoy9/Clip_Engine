using System.Diagnostics;
using System.Text;

namespace ClipEngine.Desktop.Services;

internal sealed class ShareFailedException(string message) : Exception(message);

// Makes the small copy of a clip that's sent to friends, and brings clips they send back into the library.
internal static class ClipSharing
{
    public static string SharedDir => Path.Combine(AppPaths.DataDir, "Shared");

    // Share copies are kept for a week so they can be pasted again, then cleaned up.
    private static readonly TimeSpan KeepShared = TimeSpan.FromDays(7);

    // Runs clip_engine.exe --export (src/media/Exporter.cpp) and returns the copy's path.
    // A clip that already fits is shared as it is.
    public static async Task<(string Path, double Megabytes)> MakeShareCopyAsync(string clip, int limitMb,
                                                                                 IProgress<int> progress,
                                                                                 CancellationToken cancel = default)
    {
        var bytes = new FileInfo(clip).Length;
        if (bytes <= limitMb * 1_000_000L * 0.98) return (clip, bytes / 1e6);
        if (!OperatingSystem.IsWindows() || !File.Exists(AppPaths.EngineExe))
            throw new ShareFailedException("Making a smaller copy needs clip_engine.exe next to the app.");

        Directory.CreateDirectory(SharedDir);
        CleanUp();
        var output = Path.Combine(SharedDir, $"{Path.GetFileNameWithoutExtension(clip)}_{limitMb}MB.mp4");

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
        info.ArgumentList.Add("--export");
        info.ArgumentList.Add(clip);
        info.ArgumentList.Add(output);
        info.ArgumentList.Add("--max-mb");
        info.ArgumentList.Add(limitMb.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = info };
        string? failure = null;
        Exported? exported = null;
        void OnLine(string? data)
        {
            if (data is null || EngineLogParser.ParseLine(data) is not { } line) return;
            if (line.Level == LogLevel.Error) failure ??= line.Message;
            switch (EngineLogParser.ParseEvent(line))
            {
                case ExportProgress p:
                    progress.Report(p.Percent);
                    break;
                case Exported e:
                    exported = e;
                    break;
            }
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

        if (process.ExitCode != 0 || exported is null || !File.Exists(output))
        {
            var reason = failure?.Replace("Export failed: ", "") ?? $"the engine exited with code {process.ExitCode}";
            throw new ShareFailedException($"Couldn't make the share copy: {reason}");
        }
        return (output, exported.Megabytes);
    }

    // Copies a clip someone sent into folder and gives it its markers back from the copy the engine
    // embedded in the file. Returns the new path, or the clip itself if it's already in folder.
    public static string Import(string clip, string folder)
    {
        var source = Path.GetFullPath(clip);
        Directory.CreateDirectory(folder);
        var target = string.Equals(Path.GetDirectoryName(source), Path.GetFullPath(folder).TrimEnd('\\', '/'),
                                   StringComparison.OrdinalIgnoreCase)
            ? source
            : UniquePath(folder, Path.GetFileName(source));
        if (target != source) File.Copy(source, target);

        if (!File.Exists(ClipMetadataFile.SidecarPath(target)) && ClipMetadataFile.ReadEmbedded(target) is { } metadata)
        {
            try
            {
                ClipMetadataFile.WriteSidecar(target, metadata);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The library also reads the embedded copy, so the markers still show.
            }
        }
        return target;
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem}_{i}{ext}");
        return path;
    }

    private static void CleanUp()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(SharedDir, "*.mp4"))
            {
                if (DateTime.Now - File.GetLastWriteTime(file) > KeepShared) File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
