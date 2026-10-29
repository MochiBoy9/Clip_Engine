using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The Updates card in Settings: finds a newer release on GitHub, downloads it and installs it over this copy, or
// installs a release zip picked from disk. Installing stops the engine, swaps the files and starts the new version.
public sealed class UpdatesViewModel : ObservableObject
{
    private readonly DesktopSettings _desktop;
    private readonly bool _live;
    private UpdateRelease? _available;
    private string _status;
    private bool _statusIsError;
    private bool _busy;
    private double _progress = -1;
    private string _tokenInput = "";
    private string _repoInput;

    internal UpdatesViewModel(DesktopSettings desktop, bool live)
    {
        _desktop = desktop;
        _live = live;
        _repoInput = desktop.UpdateRepo;
        _status = $"You have {CurrentVersion}.";
        CheckCommand = new RelayCommand(() => _ = CheckAsync(false), () => !Busy);
        InstallCommand = new RelayCommand(() => _ = InstallAvailableAsync(), () => !Busy && _available is not null);
        InstallFromZipCommand = new RelayCommand(() => _ = InstallFromZipAsync(), () => !Busy);
        SaveTokenCommand = new RelayCommand(SaveToken, () => TokenInput.Trim().Length > 0);
        RemoveTokenCommand = new RelayCommand(RemoveToken, () => HasToken);
        OpenReleaseCommand = new RelayCommand(() => { if (_available is { PageUrl.Length: > 0 } a) Shell.Open(a.PageUrl); });
    }

    public RelayCommand CheckCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand InstallFromZipCommand { get; }
    public RelayCommand SaveTokenCommand { get; }
    public RelayCommand RemoveTokenCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }

    // Set by the main view model: picks a zip, stops the engine before files are replaced, starts it again if they
    // couldn't be, and starts the new copy and quits once they are.
    internal Func<Task<string?>>? PickZip { get; set; }
    internal Func<Task>? StopEngine { get; set; }
    internal Func<Task>? StartEngine { get; set; }
    internal event Action? RestartRequested;
    internal event Action<UpdateRelease>? UpdateFound;

    public string CurrentVersion => $"v{UpdateFeed.CurrentVersion}";

    public bool CheckOnStart
    {
        get => _desktop.CheckForUpdates;
        set
        {
            if (_desktop.CheckForUpdates == value) return;
            _desktop.CheckForUpdates = value;
            TrySaveDesktop();
            Raise();
        }
    }

    // owner/name of the repository whose releases are installed.
    public string RepoInput
    {
        get => _repoInput;
        set
        {
            if (!Set(ref _repoInput, value ?? "")) return;
            var repo = _repoInput.Trim();
            if (repo.Length > 0 && !IsRepo(repo)) return;
            _desktop.UpdateRepo = repo;
            TrySaveDesktop();
            Raise(nameof(RepoShown));
        }
    }

    public string RepoShown => string.IsNullOrWhiteSpace(_desktop.UpdateRepo) ? UpdateFeed.DefaultRepo : _desktop.UpdateRepo;

    public string TokenInput
    {
        get => _tokenInput;
        set
        {
            if (Set(ref _tokenInput, value ?? "")) SaveTokenCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasToken => _desktop.UpdateToken.Length > 0;
    public bool ShowTokenBox => !HasToken;

    public bool HasUpdate => _available is not null;
    public string? AvailableTitle => _available is null ? null : $"Clip Engine {_available.Version} is ready to install";
    public string? AvailableNotes => _available?.Notes is { Length: > 0 } notes ? Summarize(notes) : null;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            CheckCommand.NotifyCanExecuteChanged();
            InstallCommand.NotifyCanExecuteChanged();
            InstallFromZipCommand.NotifyCanExecuteChanged();
        }
    }

    // 0 to 1 while downloading, -1 otherwise.
    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value)) Raise(nameof(ShowProgress));
        }
    }

    public bool ShowProgress => _progress >= 0;

    private static string UpdatesDir => Path.Combine(AppPaths.DataDir, "updates");

    // At start: tidies up after the last update and, when allowed, quietly looks for the next one.
    internal void OnStarted()
    {
        if (!_live) return;
        UpdateInstaller.CleanUp(AppContext.BaseDirectory);
        try
        {
            if (Directory.Exists(UpdatesDir)) Directory.Delete(UpdatesDir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        if (Program.StartedAfterUpdate) SetStatus($"Updated to {CurrentVersion}.", false);
        if (CheckOnStart) _ = CheckAsync(true);
    }

    internal async Task CheckAsync(bool quiet)
    {
        if (Busy || !_live) return;
        Busy = true;
        if (!quiet) SetStatus("Looking for updates…", false);
        try
        {
            var release = await UpdateFeed.CheckAsync(RepoShown, Token());
            SetAvailable(release);
            if (release is null)
            {
                if (!quiet) SetStatus($"You have the latest version, {CurrentVersion}.", false);
            }
            else
            {
                SetStatus($"{release.Version} is out. You have {CurrentVersion}.", false);
                UpdateFound?.Invoke(release);
            }
        }
        catch (UpdateException e)
        {
            if (!quiet) SetStatus(e.Message, true);
        }
        catch (Exception e) when (e is TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!quiet) SetStatus("GitHub didn't answer properly. Try again in a minute.", true);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallAvailableAsync()
    {
        if (_available is not { } release || Busy) return;
        Busy = true;
        try
        {
            var zip = Path.Combine(UpdatesDir, release.AssetName);
            SetStatus($"Downloading {release.Version}…", false);
            Progress = 0;
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => Progress = p));
            await UpdateFeed.DownloadAsync(release, Token(), zip, progress);
            Progress = -1;
            await InstallZipAsync(zip, release.Version);
        }
        catch (UpdateException e)
        {
            SetStatus(e.Message, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            SetStatus($"The update couldn't be installed: {e.Message}", true);
        }
        finally
        {
            Progress = -1;
            Busy = false;
        }
    }

    private async Task InstallFromZipAsync()
    {
        if (Busy || PickZip is null) return;
        var zip = await PickZip();
        if (zip is null) return;
        Busy = true;
        try
        {
            await InstallZipAsync(zip, null);
        }
        catch (UpdateException e)
        {
            SetStatus(e.Message, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus($"The update couldn't be installed: {e.Message}", true);
        }
        finally
        {
            Busy = false;
        }
    }

    // Unpacks the zip, checks it, swaps the files and restarts. expected: the version the zip should hold.
    private async Task InstallZipAsync(string zip, Version? expected)
    {
        SetStatus("Unpacking the update…", false);
        var staged = await Task.Run(() => UpdateInstaller.Unpack(zip, Path.Combine(UpdatesDir, "staged")));
        var version = UpdateInstaller.VersionOf(Path.Combine(staged, UpdateInstaller.AppExe));
        if (expected is not null && version is not null && version != expected)
            throw new UpdateException($"The download holds {version}, not {expected}. Try again, or install it from a zip.");
        if (expected is null && version is not null && version < UpdateFeed.CurrentVersion)
            throw new UpdateException($"That zip is {version}, older than the {CurrentVersion} you have.");

        SetStatus("Installing… Clip Engine restarts in a moment.", false);
        if (StopEngine is not null) await StopEngine();
        try
        {
            await Task.Run(() => UpdateInstaller.Install(staged, AppContext.BaseDirectory));
        }
        catch
        {
            if (StartEngine is not null) await StartEngine();
            throw;
        }
        RestartRequested?.Invoke();
    }

    private void SetAvailable(UpdateRelease? release)
    {
        _available = release;
        Raise(nameof(HasUpdate));
        Raise(nameof(AvailableTitle));
        Raise(nameof(AvailableNotes));
        InstallCommand.NotifyCanExecuteChanged();
    }

    private void SaveToken()
    {
        try
        {
            _desktop.UpdateToken = UpdateToken.Protect(TokenInput.Trim());
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            SetStatus($"Couldn't keep the token: {e.Message}", true);
            return;
        }
        TokenInput = "";
        TrySaveDesktop();
        RaiseToken();
        SetStatus("Token saved. Looking for updates with it…", false);
        _ = CheckAsync(false);
    }

    private void RemoveToken()
    {
        _desktop.UpdateToken = "";
        TrySaveDesktop();
        RaiseToken();
        SetStatus("Token removed from this PC.", false);
    }

    private void RaiseToken()
    {
        Raise(nameof(HasToken));
        Raise(nameof(ShowTokenBox));
        RemoveTokenCommand.NotifyCanExecuteChanged();
    }

    private string? Token() => UpdateToken.Unprotect(_desktop.UpdateToken);

    private void SetStatus(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
    }

    private void TrySaveDesktop()
    {
        if (!_live) return;
        try
        {
            _desktop.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool IsRepo(string text)
    {
        var parts = text.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    // The release notes' bullet headlines ("**Each kill counts once.**"), or their first lines.
    internal static string Summarize(string notes)
    {
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l =>
            {
                var text = l[2..];
                if (text.StartsWith("**", StringComparison.Ordinal) && text.IndexOf("**", 2, StringComparison.Ordinal) is var end and > 2)
                    return text[2..end].TrimEnd('.');
                return text.Length > 80 ? text[..80] + "…" : text;
            })
            .Take(6)
            .ToList();
        return string.Join(" · ", lines);
    }
}
