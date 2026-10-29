using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxActivity = 60;

    // The storage cleanup waits a while after starting or after its rules change, so there's
    // time to star clips to keep, then runs every hour.
    private static readonly TimeSpan CleanupDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly EngineHost? _engine;
    private readonly ClipLibrary? _library;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _saveDesktopTimer;
    private readonly DispatcherTimer _cleanupTimer;
    private readonly DispatcherTimer _longRecordingTimer;
    private DateTime? _longRecordingSince;
    private string _recordHotkey = "F9";
    private NavItem _selectedNav;
    private object _currentPage;
    private EngineState _engineState = EngineState.Stopped;
    private string? _engineProblem;
    private string? _detectedGame;
    private RecordingStarted? _recording;
    private string _hotkey = "F8";
    private int _clipSeconds = 30;
    private int _bufferSeconds = 60;
    private string _filter = AllGames;
    private string _search = "";
    private string? _toast;
    private bool _toastIsError;
    private long _libraryBytes;
    private int _clipsToday;
    private bool _isSharing;
    private bool _showMatches;
    private string? _freeSpace;
    private IReadOnlyList<ClipFile> _files = [];

    public const string AllGames = "All clips";
    public const string FavoritesFilter = "Favorites";

    // live: start the engine and watch the clips folder. The preview tool passes false and fills in sample data.
    public MainViewModel(bool live = true)
    {
        IsLive = live;
        Nav =
        [
            new NavItem(Page.Home, "Home", Icon("IconHome")),
            new NavItem(Page.Clips, "Clips", Icon("IconClips")),
            new NavItem(Page.Records, "Records", Icon("IconTrophy")),
            new NavItem(Page.Games, "Games", Icon("IconGames")),
            new NavItem(Page.Settings, "Settings", Icon("IconSettings")),
        ];
        _selectedNav = Nav[0];

        var engineSettings = live ? EngineSettings.Load(AppPaths.EngineSettingsFile) : new EngineSettings();
        DesktopSettings = live ? DesktopSettings.Load() : new DesktopSettings();
        Supporter = live
            ? new SupporterViewModel(SupporterConfig.PublicKey, AppPaths.SupporterKeyFile, DesktopSettings)
            : new SupporterViewModel(SupporterConfig.PublicKey, null, DesktopSettings);
        Updates = new UpdatesViewModel(DesktopSettings, live)
        {
            PickZip = () => Dialogs?.PickZipAsync() ?? Task.FromResult<string?>(null),
            StopEngine = StopEngineAsync,
            StartEngine = () =>
            {
                _engine?.Start();
                return Task.CompletedTask;
            },
        };
        Updates.UpdateFound += release =>
            AddActivity(ActivityKind.Info, $"Clip Engine {release.Version} is out. Install it from Settings > Updates.");
        Settings = new SettingsViewModel(engineSettings, DesktopSettings, live ? RestartEngineAsync : null)
        {
            Supporter = Supporter,
            Updates = Updates,
        };
        Settings.SidePanelHotkeyChanged += _ => Raise(nameof(SidePanelHotkey));
        Settings.StorageRulesChanged += OnStorageRulesChanged;
        Settings.CleanUpNowRequested += () => _ = CleanUpAsync();
        _showMatches = DesktopSettings.ShowMatches;
        _hotkey = engineSettings.SaveHotkey;
        _recordHotkey = engineSettings.RecordHotkey;
        _clipSeconds = engineSettings.ClipSeconds;
        _bufferSeconds = engineSettings.BufferSeconds;

        Home = new HomePage(this);
        ClipsPage = new ClipsPage(this);
        GamesPage = new GamesPage(this);
        Records = new RecordsViewModel(this);
        _currentPage = Home;

        SaveClipCommand = new RelayCommand(SaveClip, () => IsRecording);
        ToggleLongRecordingCommand = new RelayCommand(ToggleLongRecording, () => IsRecording);
        OpenClipsFolderCommand = new RelayCommand(() => Shell.Open(ClipsFolder));
        OpenLogCommand = new RelayCommand(() => Shell.Open(AppPaths.EngineLogFile));
        RestartEngineCommand = new RelayCommand(() => _ = RestartEngineAsync());
        OpenClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) OpenPlayer(c); });
        ShowClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) Shell.ShowInFolder(c.Path); });
        DeleteClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) DeleteClip(c); });
        ShareClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) _ = ShareClipAsync(c); });
        CopyClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) CopyClip(c); });
        FavoriteClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) ToggleFavorite(c); });
        ShowClipsGridCommand = new RelayCommand(() => ShowMatches = false);
        ShowMatchesCommand = new RelayCommand(() => ShowMatches = true);
        ImportClipCommand = new RelayCommand(() => _ = ImportClipAsync(null));
        ToggleSidePanelCommand = new RelayCommand(() => SidePanelToggleRequested?.Invoke());
        ShowMainWindowCommand = new RelayCommand(() => ShowMainWindowRequested?.Invoke());
        OpenStudioCommand = new RelayCommand(p =>
        {
            var studio = OpenStudio();
            if (p is "add") studio.StartAddGameCommand.Execute(null);
        });
        GoToCommand = new RelayCommand(p => { if (p is Page page) SelectedNav = Nav.First(n => n.Page == page); });

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };

        _saveDesktopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveDesktopTimer.Tick += (_, _) =>
        {
            _saveDesktopTimer.Stop();
            SaveDesktopSettings();
        };

        _cleanupTimer = new DispatcherTimer { Interval = CleanupDelay };
        _cleanupTimer.Tick += (_, _) =>
        {
            _cleanupTimer.Interval = CleanupInterval;
            _ = CleanUpAsync();
        };

        // Ticks the long recording's clock while one runs.
        _longRecordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _longRecordingTimer.Tick += (_, _) => RaiseLongRecording();

        if (!live) return;
        _cleanupTimer.Start();

        _library = new ClipLibrary { ExtraFolders = DesktopSettings.ImportFolders };
        _library.Changed += () => _ = RefreshClipsAsync();
        _library.Watch(engineSettings.OutputDir);

        _engine = new EngineHost();
        _engine.StateChanged += (state, message) => Dispatcher.UIThread.Post(() => OnEngineState(state, message));
        _engine.EventReceived += e => Dispatcher.UIThread.Post(() => OnEngineEvent(e));
        _engine.LineReceived += line =>
        {
            if (line.Level == LogLevel.Info) return;
            Dispatcher.UIThread.Post(() => AddActivity(line.Level == LogLevel.Error ? ActivityKind.Error : ActivityKind.Warning,
                                                       line.Message));
        };
        _engine.Start();
        Updates.OnStarted();
    }

    public IReadOnlyList<NavItem> Nav { get; }
    public HomePage Home { get; }
    public ClipsPage ClipsPage { get; }
    public GamesPage GamesPage { get; }
    public RecordsViewModel Records { get; }
    public SettingsViewModel Settings { get; }
    public SupporterViewModel Supporter { get; }
    public UpdatesViewModel Updates { get; }
    internal DesktopSettings DesktopSettings { get; }

    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<ActivityItem> RecentMoments { get; } = [];
    public bool HasRecentMoments => RecentMoments.Count > 0;
    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];
    public ObservableCollection<ClipItemViewModel> RecentClips { get; } = [];
    public ObservableCollection<ClipItemViewModel> FilteredClips { get; } = [];
    public ObservableCollection<MatchViewModel> Matches { get; } = [];
    public ObservableCollection<string> Filters { get; } = [AllGames];
    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    public RelayCommand SaveClipCommand { get; }
    public RelayCommand ToggleLongRecordingCommand { get; }
    public RelayCommand OpenClipsFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand RestartEngineCommand { get; }
    public RelayCommand OpenClipCommand { get; }
    public RelayCommand OpenStudioCommand { get; }
    public RelayCommand ShowClipCommand { get; }
    public RelayCommand DeleteClipCommand { get; }
    public RelayCommand ShareClipCommand { get; }
    public RelayCommand CopyClipCommand { get; }
    public RelayCommand FavoriteClipCommand { get; }
    public RelayCommand ShowClipsGridCommand { get; }
    public RelayCommand ShowMatchesCommand { get; }
    public RelayCommand ImportClipCommand { get; }
    public RelayCommand ToggleSidePanelCommand { get; }
    public RelayCommand ShowMainWindowCommand { get; }
    public RelayCommand GoToCommand { get; }

    // False in the preview tool, which has no engine and no video window.
    internal bool IsLive { get; }

    // File pickers and the window handle, from the main window.
    internal IAppDialogs? Dialogs { get; set; }

    internal event Action? SidePanelToggleRequested;
    internal event Action? ShowMainWindowRequested;
    // A short notice to show over the game, like "Clip saved · 2 kills".
    internal event Action<string>? GameNoticeRequested;

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || !Set(ref _selectedNav, value)) return;
            CurrentPage = PageFor(value.Page);
        }
    }

    private object PageFor(Page page) => page switch
    {
        Page.Clips => ClipsPage,
        Page.Records => Records,
        Page.Games => GamesPage,
        Page.Settings => Settings,
        _ => Home,
    };

    public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }

    // Shows a page that isn't in the sidebar, like the player, or goes back to one that is.
    internal void ShowPage(object page)
    {
        var nav = Nav.FirstOrDefault(n => ReferenceEquals(PageFor(n.Page), page));
        if (nav is not null) Set(ref _selectedNav, nav, nameof(SelectedNav));
        CurrentPage = page;
    }

    internal PlayerViewModel OpenPlayer(ClipItemViewModel clip)
    {
        var returnTo = CurrentPage is PlayerViewModel open ? open.ReturnTo : CurrentPage;
        var player = new PlayerViewModel(this, clip, returnTo);
        ShowPage(player);
        ShowMainWindowRequested?.Invoke();
        return player;
    }

    // Detection Studio, for making or fixing the rules that spot moments in a game.
    internal StudioViewModel OpenStudio(string? gameName = null, ClipItemViewModel? clip = null, double seconds = 0)
    {
        var studio = new StudioViewModel(this, CurrentPage, gameName, clip, seconds);
        ShowPage(studio);
        return studio;
    }

    public bool IsSharing { get => _isSharing; private set => Set(ref _isSharing, value); }
    public string SidePanelHotkey => DesktopSettings.SidePanelHotkey;

    public string ClipsFolder => _library?.Folder ?? AppPaths.DefaultClipsDir;

    // Status shown in the hero card and the sidebar.
    public bool IsRecording => _recording is not null && _engineState == EngineState.Running;
    public bool IsEngineRunning => _engineState is EngineState.Running or EngineState.Starting;
    public bool HasEngineProblem => _engineProblem is not null;
    public string? EngineProblem => _engineProblem;

    public string StatusEyebrow => _engineState switch
    {
        EngineState.Running when IsLongRecording => $"LIVE · LONG RECORDING {LongRecordingTime}",
        EngineState.Running when IsRecording => "LIVE · REPLAY BUFFER ON",
        EngineState.Running => "ENGINE READY",
        EngineState.Starting => "STARTING",
        EngineState.Missing => "ENGINE NOT FOUND",
        EngineState.Unsupported => "WINDOWS ONLY",
        _ => "ENGINE STOPPED",
    };

    public string StatusTitle => _engineState switch
    {
        EngineState.Running when IsRecording && _recording!.Label == "Desktop" => "Recording your desktop",
        EngineState.Running when IsRecording => $"Recording {_recording!.Label}",
        EngineState.Running when _detectedGame is not null => $"{_detectedGame} detected",
        EngineState.Running => "Waiting for a game",
        EngineState.Starting => "Starting the engine…",
        _ => "Not recording",
    };

    public string StatusDetail
    {
        get
        {
            if (_engineProblem is not null) return _engineProblem;
            if (IsRecording)
            {
                var r = _recording!;
                return $"{r.Width}×{r.Height} · {r.Fps} fps · {r.Codec} {r.Mbps} Mbps · {r.Encoder}";
            }
            return _engineState == EngineState.Running
                ? "Launch a supported game and recording starts on its monitor by itself."
                : "Clips can't be saved until the engine is running.";
        }
    }

    public IBrush StatusBrush => (IBrush)(Avalonia.Application.Current!.FindResource(
        IsRecording ? "RecordBrush" : _engineState == EngineState.Running ? "SuccessBrush"
        : _engineState == EngineState.Starting ? "WarnBrush" : "IdleBrush") ?? Brushes.Gray);

    public string SidebarStatus => IsLongRecording ? $"Long recording {LongRecordingTime}" : IsRecording ? "Recording" : _engineState switch
    {
        EngineState.Running => "Engine ready",
        EngineState.Starting => "Starting…",
        _ => "Engine stopped",
    };

    public string SaveHint => $"{Hotkey} saves the last {ClipSeconds} s";

    // A long recording runs from its hotkey (or the button) until it's pressed again.
    public bool IsLongRecording => _longRecordingSince is not null && IsRecording;
    public string LongRecordingTime => _longRecordingSince is { } since ? Format.Duration(DateTime.Now - since) : "";
    public string LongRecordingButton => IsLongRecording ? $"Stop · {LongRecordingTime}" : "Long recording";
    public string LongRecordingHint => IsLongRecording
        ? $"{RecordHotkey} stops it. Moments are still marked."
        : $"{RecordHotkey} records until pressed again";
    public string RecordHotkey { get => _recordHotkey; private set { if (Set(ref _recordHotkey, value)) RaiseLongRecording(); } }
    public string Hotkey { get => _hotkey; private set { if (Set(ref _hotkey, value)) Raise(nameof(SaveHint)); } }
    public int ClipSeconds { get => _clipSeconds; private set { if (Set(ref _clipSeconds, value)) Raise(nameof(SaveHint)); } }
    public int BufferSeconds { get => _bufferSeconds; private set { if (Set(ref _bufferSeconds, value)) Raise(nameof(BufferText)); } }
    public string BufferText => $"{BufferSeconds} s";

    public int ClipsToday { get => _clipsToday; private set => Set(ref _clipsToday, value); }
    public string LibrarySize => Format.Bytes(_libraryBytes);
    public int ClipCount => Clips.Count;
    public string GamesSupported => Games.Count.ToString();
    public bool HasClips => Clips.Count > 0;
    public bool HasNoClips => Clips.Count == 0;
    public bool HasFilteredClips => FilteredClips.Count > 0;
    public bool HasMatches => Matches.Count > 0;

    // The Clips page shows single clips in a grid, or whole matches.
    public bool ShowMatches
    {
        get => _showMatches;
        set
        {
            if (!Set(ref _showMatches, value)) return;
            Raise(nameof(ShowClipsGrid));
            DesktopSettings.ShowMatches = value;
            SaveDesktopSettingsSoon();
        }
    }

    public bool ShowClipsGrid => !_showMatches;

    // "12.4 GB · 230 GB free" for the clips folder's drive.
    public string StorageText => _freeSpace is null ? LibrarySize : $"{LibrarySize} · {_freeSpace} free";

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? AllGames)) ApplyFilter();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? "")) ApplyFilter();
        }
    }

    public string? Toast { get => _toast; private set { if (Set(ref _toast, value)) Raise(nameof(HasToast)); } }
    public bool HasToast => _toast is not null;
    public bool ToastIsError { get => _toastIsError; private set => Set(ref _toastIsError, value); }

    public string Version { get; } =
        typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"v{v.Major}.{v.Minor}.{v.Build}" : "";

    public void ShowToast(string text, bool isError = false)
    {
        ToastIsError = isError;
        Toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public Task StopEngineAsync() => _engine?.StopAsync() ?? Task.CompletedTask;

    internal async Task RestartEngineAsync()
    {
        if (_engine is null) return;
        var settings = Settings.ToEngineSettings();
        Hotkey = settings.SaveHotkey;
        RecordHotkey = settings.RecordHotkey;
        ClipSeconds = settings.ClipSeconds;
        BufferSeconds = settings.BufferSeconds;
        _library?.Watch(settings.OutputDir);
        Raise(nameof(ClipsFolder));
        await _engine.RestartAsync();
    }

    private void ToggleLongRecording()
    {
        if (_engine?.ToggleLongRecording() == true) return;
        ShowToast("The engine isn't running, so nothing can be recorded.", isError: true);
    }

    private void RaiseLongRecording()
    {
        Raise(nameof(IsLongRecording));
        Raise(nameof(LongRecordingTime));
        Raise(nameof(LongRecordingButton));
        Raise(nameof(LongRecordingHint));
        Raise(nameof(StatusEyebrow));
        Raise(nameof(SidebarStatus));
    }

    private void SetLongRecording(DateTime? since)
    {
        _longRecordingSince = since;
        if (since is null) _longRecordingTimer.Stop();
        else _longRecordingTimer.Start();
        RaiseLongRecording();
    }

    private void SaveClip()
    {
        if (_engine?.SaveClip() == true) return;
        ShowToast("The engine isn't running, so there's nothing to save.", isError: true);
    }

    private bool CopyToClipboard(string path) =>
        OperatingSystem.IsWindows() && Dialogs is { WindowHandle: not 0 } dialogs
        && Win32.CopyFilesToClipboard(dialogs.WindowHandle, [path]);

    private void CopyClip(ClipItemViewModel clip)
    {
        if (!CopyToClipboard(clip.Path))
        {
            ShowToast("Couldn't put the clip on the clipboard.", isError: true);
            return;
        }
        var limit = DesktopSettings.ShareLimitMb;
        ShowToast(clip.File.Bytes > limit * 1_000_000L
            ? $"Clip copied ({clip.Size}). That's over {limit} MB, so for Discord use Share instead."
            : "Clip copied. Paste it into Discord, a chat or a folder.");
    }

    private async Task ShareClipAsync(ClipItemViewModel clip)
    {
        if (IsSharing)
        {
            ShowToast("Still making the last share copy…");
            return;
        }
        IsSharing = true;
        var limit = DesktopSettings.ShareLimitMb;
        try
        {
            ShowToast($"Making a copy under {limit} MB…");
            var progress = new Progress<int>(p => ShowToast($"Making a copy under {limit} MB… {p}%"));
            var (path, megabytes) = await ClipSharing.MakeShareCopyAsync(clip.Path, limit, progress);
            AddActivity(ActivityKind.Clip, $"Made a {megabytes:0.0} MB share copy of a {clip.Game} clip");
            if (CopyToClipboard(path))
            {
                ShowToast($"Copied a {megabytes:0.0} MB version. Paste it into Discord with Ctrl+V.");
            }
            else
            {
                ShowToast($"Saved a {megabytes:0.0} MB version to share.");
                Shell.ShowInFolder(path);
            }
        }
        catch (ShareFailedException e)
        {
            ShowToast(e.Message, isError: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowToast($"Couldn't make the share copy: {e.Message}", isError: true);
        }
        finally
        {
            IsSharing = false;
        }
    }

    // Brings a clip into the library, usually one a friend sent, into a folder the user picks.
    // Clips made with Clip Engine carry their markers inside the file, so those come back too.
    internal async Task ImportClipAsync(string? path)
    {
        if (Dialogs is null) return;
        path ??= await Dialogs.PickClipAsync();
        if (path is null || !File.Exists(path)) return;

        if (Clips.FirstOrDefault(c => string.Equals(c.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            OpenPlayer(known);
            return;
        }

        var suggested = DesktopSettings.ImportFolders.FirstOrDefault(Directory.Exists)
                        ?? Path.Combine(ClipsFolder, "Shared with me");
        var folder = await Dialogs.PickFolderAsync("Choose where to keep this clip", suggested);
        if (folder is null) return;

        string imported;
        try
        {
            imported = await Task.Run(() => ClipSharing.Import(path, folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Couldn't import the clip: {e.Message}", isError: true);
            return;
        }

        RememberImportFolder(folder);
        await RefreshClipsAsync();
        var clip = Clips.FirstOrDefault(c => string.Equals(c.Path, imported, StringComparison.OrdinalIgnoreCase));
        ShowToast($"Clip added to {Path.GetFileName(folder.TrimEnd('\\', '/'))}");
        AddActivity(ActivityKind.Clip, $"Imported {Path.GetFileName(imported)}");
        if (clip is not null) OpenPlayer(clip);
    }

    private void RememberImportFolder(string folder)
    {
        var list = DesktopSettings.ImportFolders;
        list.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, folder);
        if (list.Count > 8) list.RemoveRange(8, list.Count - 8);
        if (_library is not null) _library.ExtraFolders = [.. list];
        SaveDesktopSettings();
    }

    internal void SaveDesktopSettingsSoon()
    {
        _saveDesktopTimer.Stop();
        _saveDesktopTimer.Start();
    }

    private void SaveDesktopSettings()
    {
        if (!IsLive) return;
        try
        {
            DesktopSettings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DeleteClip(ClipItemViewModel clip)
    {
        try
        {
            Shell.Delete(clip.Path);
            ShowToast(OperatingSystem.IsWindows() ? "Moved to the Recycle Bin" : "Clip deleted");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowToast($"Couldn't delete the clip: {e.Message}", isError: true);
        }
    }

    private void OnEngineState(EngineState state, string? message)
    {
        _engineState = state;
        _engineProblem = message;
        if (state != EngineState.Running)
        {
            _recording = null;
            _detectedGame = null;
            SetLongRecording(null);
            Records.StartLiveMatch(null);
            UpdateGames();
        }
        if (message is not null) AddActivity(ActivityKind.Error, message);
        RaiseStatus();
    }

    private void OnEngineEvent(EngineEvent e)
    {
        switch (e)
        {
            case EngineStarted:
                AddActivity(ActivityKind.Info, "Engine started");
                break;
            case SupportedGames g:
                SetSupportedGames(g.Names);
                break;
            case ClipsFolder f:
                _library?.Watch(f.Path);
                Raise(nameof(ClipsFolder));
                break;
            case SaveHotkey h:
                Hotkey = h.Hotkey;
                ClipSeconds = h.ClipSeconds;
                break;
            case GameDetected d:
                _detectedGame = d.Game;
                AddActivity(ActivityKind.Info, $"{d.Game} detected");
                break;
            case GameClosed c:
                if (_detectedGame == c.Game) _detectedGame = null;
                AddActivity(ActivityKind.Info, $"{c.Game} closed");
                break;
            case RecordingStarted r:
                _recording = r;
                Records.StartLiveMatch(r.Label == "Desktop" ? null : r.Label);
                AddActivity(ActivityKind.Recording, $"Recording {r.Label} at {r.Width}×{r.Height}, {r.Fps} fps");
                break;
            case RecordingStopped:
                _recording = null;
                SetLongRecording(null);
                Records.StartLiveMatch(null);
                _ = RefreshRecordsAsync();
                break;
            case RecordHotkey h:
                RecordHotkey = h.Hotkey;
                break;
            case LongRecordingStarted l:
                SetLongRecording(DateTime.Now);
                AddActivity(ActivityKind.Recording, $"Long recording started in {l.Game}");
                ShowToast($"Long recording · {RecordHotkey} to stop");
                break;
            case LongRecordingStopped l:
                SetLongRecording(null);
                ShowToast(l.Reason is null ? "Saving the long recording…" : $"Long recording stopped: {l.Reason}", l.Reason is not null);
                break;
            case LongRecordingSaved l:
                AddActivity(ActivityKind.Clip, $"Saved a {Format.Duration(TimeSpan.FromSeconds(l.Seconds))} long recording ({Format.Bytes((long)(l.Megabytes * 1024 * 1024))})");
                ShowToast($"Long recording saved · {Format.Duration(TimeSpan.FromSeconds(l.Seconds))}");
                _ = RefreshClipsAsync();
                break;
            case ScoreboardRead b:
                Records.SetLiveScoreboard(b.Game, b.Stats);
                break;
            case ClipSaved s:
                AddActivity(ActivityKind.Clip, $"Saved a {s.Seconds:0} s clip ({s.Megabytes:0.0} MB)");
                ShowToast($"Clip saved · {s.Seconds:0} s · {s.Megabytes:0.0} MB");
                if (DesktopSettings.GameNotifications && IsRecording)
                {
                    var summary = ClipMetadataFile.ReadSidecar(s.Path)?.Title;
                    GameNoticeRequested?.Invoke(string.IsNullOrEmpty(summary) ? $"Clip saved · {s.Seconds:0} s" : $"Clip saved · {summary}");
                }
                _ = RefreshClipsAsync();
                break;
            case MomentDetected m:
                AddActivity(ActivityKind.Moment, $"{EventKinds.Label(m.Type)} in {m.Game}");
                Records.AddLiveMoment(m.Game, m.Type);
                break;
        }
        UpdateGames();
        RaiseStatus();
    }

    internal void SetSupportedGames(IEnumerable<string> names)
    {
        Games.Clear();
        foreach (var name in names) Games.Add(new GameItemViewModel(name, Palettes.IndexFor(name)));
        UpdateGames();
        Raise(nameof(GamesSupported));
    }

    private void UpdateGames()
    {
        foreach (var game in Games)
        {
            game.IsRunning = game.Name == _detectedGame;
            game.IsRecording = IsRecording && _recording!.Label == game.Name;
            game.ClipCount = Clips.Count(c => string.Equals(c.Game, game.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal void AddActivity(ActivityKind kind, string text, DateTime? time = null)
    {
        var item = new ActivityItem(time ?? DateTime.Now, kind, text);
        Activity.Insert(0, item);
        while (Activity.Count > MaxActivity) Activity.RemoveAt(Activity.Count - 1);
        if (kind != ActivityKind.Moment) return;
        RecentMoments.Insert(0, item);
        while (RecentMoments.Count > 4) RecentMoments.RemoveAt(RecentMoments.Count - 1);
        Raise(nameof(HasRecentMoments));
    }

    private async Task RefreshClipsAsync()
    {
        if (_library is null) return;
        var files = await Task.Run(_library.Scan);
        await Dispatcher.UIThread.InvokeAsync(() => SetClips(files));
    }

    internal void SetClips(IReadOnlyList<ClipFile> files)
    {
        // Keep the view models (and thumbnails) of clips that are still there.
        var existing = Clips.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        Clips.Clear();
        foreach (var file in files)
        {
            if (existing.TryGetValue(file.Path, out var clip))
            {
                clip.File = file;
            }
            else
            {
                clip = new ClipItemViewModel(file, Palettes.IndexFor(file.Game), this);
                _ = LoadThumbnailAsync(clip);
            }
            Clips.Add(clip);
        }

        RecentClips.Clear();
        foreach (var clip in Clips.Take(4)) RecentClips.Add(clip);

        var games = Clips.Select(c => c.Game).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        for (var i = Filters.Count - 1; i >= 1; i--)
        {
            if (Filters[i] != FavoritesFilter && !games.Contains(Filters[i], StringComparer.OrdinalIgnoreCase)) Filters.RemoveAt(i);
        }
        foreach (var game in games)
        {
            if (!Filters.Contains(game)) Filters.Add(game);
        }
        if (Clips.Any(c => c.IsFavorite))
        {
            if (!Filters.Contains(FavoritesFilter)) Filters.Insert(1, FavoritesFilter);
        }
        else if (Filters.Remove(FavoritesFilter) && _filter == FavoritesFilter)
        {
            _filter = AllGames;
            Raise(nameof(Filter));
        }
        if (!Filters.Contains(_filter)) Filter = AllGames;
        Settings.CleanupPreview = DescribeCleanup();

        _files = files;
        _libraryBytes = files.Sum(f => f.Bytes);
        _freeSpace = IsLive ? FreeSpace(ClipsFolder) : null;
        ClipsToday = files.Count(f => f.Created.Date == DateTime.Today);
        ApplyFilter();
        UpdateGames();
        Raise(nameof(LibrarySize));
        Raise(nameof(StorageText));
        Raise(nameof(ClipCount));
        Raise(nameof(HasClips));
        Raise(nameof(HasNoClips));
        _ = RefreshRecordsAsync();
    }

    // Timelines the preview tool shows records from, in place of the ones on disk.
    internal IReadOnlyList<Timeline> SampleTimelines { get; set; } = [];

    // Rebuilds the records from every timeline and clip. Cheap enough to run after each change.
    internal async Task RefreshRecordsAsync()
    {
        var files = _files;
        if (!IsLive)
        {
            Records.SetBook(PlayerRecords.Build(SampleTimelines, files));
            return;
        }
        var book = await Task.Run(() => PlayerRecords.Build(Timeline.LoadAll(Timeline.Folder), files));
        Records.SetBook(book);
    }

    private static async Task LoadThumbnailAsync(ClipItemViewModel clip)
    {
        var bitmap = await Thumbnails.LoadAsync(clip.Path, 480);
        if (bitmap is not null) await Dispatcher.UIThread.InvokeAsync(() => clip.Thumbnail = bitmap);
    }

    private void ApplyFilter()
    {
        FilteredClips.Clear();
        foreach (var clip in Clips)
        {
            if (_filter == FavoritesFilter ? !clip.IsFavorite
                : _filter != AllGames && !string.Equals(clip.Game, _filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (_search.Length > 0 && !clip.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
                && !clip.When.Contains(_search, StringComparison.OrdinalIgnoreCase)
                && !clip.Summary.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
            FilteredClips.Add(clip);
        }

        // Matches are made of the clips that pass the filter, so filtering by game or searching
        // "revive" narrows the matches too.
        Matches.Clear();
        var byPath = FilteredClips.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var match in MatchGrouping.Group(FilteredClips.Select(c => c.File)))
            Matches.Add(new MatchViewModel(match, match.Clips.Select(f => byPath[f.Path]).ToList()));
        Raise(nameof(HasFilteredClips));
        Raise(nameof(HasMatches));
    }

    private void ToggleFavorite(ClipItemViewModel clip)
    {
        var metadata = clip.File.Metadata ?? new ClipMetadata
        {
            Game = clip.Game,
            Trigger = "hotkey",
            Seconds = clip.File.Duration?.TotalSeconds ?? 0,
        };
        metadata.Favorite = !clip.IsFavorite;
        try
        {
            ClipMetadataFile.WriteSidecar(clip.Path, metadata);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            metadata.Favorite = !metadata.Favorite;
            ShowToast($"Couldn't star the clip: {e.Message}", isError: true);
            return;
        }
        clip.File = clip.File with { Metadata = metadata };
        ShowToast(metadata.Favorite ? "Added to favorites. Favorites are never cleaned up." : "Removed from favorites");
        SetClips(_files.Select(f => string.Equals(f.Path, clip.Path, StringComparison.OrdinalIgnoreCase) ? clip.File : f).ToList());
    }

    // Saves part of a clip as a new clip and returns it, or null if that failed (with a toast saying why).
    internal async Task<ClipItemViewModel?> TrimClipAsync(ClipItemViewModel clip, double start, double end)
    {
        string path;
        try
        {
            path = await ClipEditing.TrimAsync(clip.Path, start, end);
        }
        catch (EditFailedException e)
        {
            ShowToast(e.Message, isError: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowToast($"Couldn't trim the clip: {e.Message}", isError: true);
            return null;
        }
        await RefreshClipsAsync();
        AddActivity(ActivityKind.Clip, $"Trimmed a {clip.Game} clip to {Format.Duration(TimeSpan.FromSeconds(end - start))}");
        ShowToast("Trimmed copy saved next to the original");
        return Clips.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ClipFile> PlanCleanup() =>
        StorageCleanup.Plan(_files, ClipsFolder, DesktopSettings.ImportFolders, DateTime.Now,
                            DesktopSettings.DeleteAfterDays, DesktopSettings.MaxLibraryGb);

    private string? DescribeCleanup()
    {
        if (DesktopSettings.DeleteAfterDays <= 0 && DesktopSettings.MaxLibraryGb <= 0) return null;
        var plan = PlanCleanup();
        return plan.Count == 0
            ? "Nothing to clean up right now. Starred clips are always kept."
            : $"{(plan.Count == 1 ? "1 clip" : $"{plan.Count} clips")} ({Format.Bytes(plan.Sum(c => c.Bytes))}) will be removed at the next cleanup. Star any you want to keep.";
    }

    private void OnStorageRulesChanged()
    {
        Settings.CleanupPreview = DescribeCleanup();
        _cleanupTimer.Stop();
        _cleanupTimer.Interval = CleanupDelay;
        if (IsLive) _cleanupTimer.Start();
    }

    private async Task CleanUpAsync()
    {
        if (!IsLive) return;
        var plan = PlanCleanup();
        if (plan.Count == 0) return;
        var (count, bytes) = await Task.Run(() => StorageCleanup.Delete(plan));
        if (count == 0) return;
        AddActivity(ActivityKind.Info, $"Storage cleanup removed {(count == 1 ? "1 old clip" : $"{count} old clips")} ({Format.Bytes(bytes)})");
        await RefreshClipsAsync();
    }

    private static string? FreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : Format.Bytes(new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // Lets the preview tool show the app as it looks mid-game.
    internal void SimulateEngine(EngineState state, params EngineEvent[] events)
    {
        OnEngineState(state, null);
        foreach (var e in events) OnEngineEvent(e);
    }

    private void RaiseStatus()
    {
        Raise(nameof(IsRecording));
        Raise(nameof(IsEngineRunning));
        Raise(nameof(HasEngineProblem));
        Raise(nameof(EngineProblem));
        Raise(nameof(StatusEyebrow));
        Raise(nameof(StatusTitle));
        Raise(nameof(StatusDetail));
        Raise(nameof(StatusBrush));
        Raise(nameof(SidebarStatus));
        RaiseLongRecording();
        SaveClipCommand.NotifyCanExecuteChanged();
        ToggleLongRecordingCommand.NotifyCanExecuteChanged();
    }

    private static StreamGeometry Icon(string key) =>
        (StreamGeometry)Avalonia.Application.Current!.FindResource(key)!;

    public void Dispose()
    {
        _engine?.Dispose();
        _library?.Dispose();
    }
}

// Each page binds to the shared view model through Main.
public sealed record HomePage(MainViewModel Main);
public sealed record ClipsPage(MainViewModel Main);
public sealed record GamesPage(MainViewModel Main);
