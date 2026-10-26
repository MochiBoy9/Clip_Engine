using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxActivity = 60;

    private readonly EngineHost? _engine;
    private readonly ClipLibrary? _library;
    private readonly DispatcherTimer _toastTimer;
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

    public const string AllGames = "All clips";

    // live: start the engine and watch the clips folder. The preview tool passes false and fills in sample data.
    public MainViewModel(bool live = true)
    {
        Nav =
        [
            new NavItem(Page.Home, "Home", Icon("IconHome")),
            new NavItem(Page.Clips, "Clips", Icon("IconClips")),
            new NavItem(Page.Games, "Games", Icon("IconGames")),
            new NavItem(Page.Settings, "Settings", Icon("IconSettings")),
        ];
        _selectedNav = Nav[0];

        var engineSettings = live ? EngineSettings.Load(AppPaths.EngineSettingsFile) : new EngineSettings();
        DesktopSettings = live ? DesktopSettings.Load() : new DesktopSettings();
        Settings = new SettingsViewModel(engineSettings, DesktopSettings, live ? RestartEngineAsync : null);
        _hotkey = engineSettings.SaveHotkey;
        _clipSeconds = engineSettings.ClipSeconds;
        _bufferSeconds = engineSettings.BufferSeconds;

        Home = new HomePage(this);
        ClipsPage = new ClipsPage(this);
        GamesPage = new GamesPage(this);
        _currentPage = Home;

        SaveClipCommand = new RelayCommand(SaveClip, () => IsRecording);
        OpenClipsFolderCommand = new RelayCommand(() => Shell.Open(ClipsFolder));
        OpenLogCommand = new RelayCommand(() => Shell.Open(AppPaths.EngineLogFile));
        RestartEngineCommand = new RelayCommand(() => _ = RestartEngineAsync());
        OpenClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) Shell.Open(c.Path); });
        ShowClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) Shell.ShowInFolder(c.Path); });
        DeleteClipCommand = new RelayCommand(p => { if (p is ClipItemViewModel c) DeleteClip(c); });
        GoToCommand = new RelayCommand(p => { if (p is Page page) SelectedNav = Nav.First(n => n.Page == page); });

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };

        if (!live) return;

        _library = new ClipLibrary();
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
    }

    public IReadOnlyList<NavItem> Nav { get; }
    public HomePage Home { get; }
    public ClipsPage ClipsPage { get; }
    public GamesPage GamesPage { get; }
    public SettingsViewModel Settings { get; }
    internal DesktopSettings DesktopSettings { get; }

    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];
    public ObservableCollection<ClipItemViewModel> RecentClips { get; } = [];
    public ObservableCollection<ClipItemViewModel> FilteredClips { get; } = [];
    public ObservableCollection<string> Filters { get; } = [AllGames];
    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    public RelayCommand SaveClipCommand { get; }
    public RelayCommand OpenClipsFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand RestartEngineCommand { get; }
    public RelayCommand OpenClipCommand { get; }
    public RelayCommand ShowClipCommand { get; }
    public RelayCommand DeleteClipCommand { get; }
    public RelayCommand GoToCommand { get; }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || !Set(ref _selectedNav, value)) return;
            CurrentPage = value.Page switch
            {
                Page.Clips => ClipsPage,
                Page.Games => GamesPage,
                Page.Settings => Settings,
                _ => Home,
            };
        }
    }

    public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }

    public string ClipsFolder => _library?.Folder ?? AppPaths.DefaultClipsDir;

    // Status shown in the hero card and the sidebar.
    public bool IsRecording => _recording is not null && _engineState == EngineState.Running;
    public bool IsEngineRunning => _engineState is EngineState.Running or EngineState.Starting;
    public bool HasEngineProblem => _engineProblem is not null;
    public string? EngineProblem => _engineProblem;

    public string StatusEyebrow => _engineState switch
    {
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

    public string SidebarStatus => IsRecording ? "Recording" : _engineState switch
    {
        EngineState.Running => "Engine ready",
        EngineState.Starting => "Starting…",
        _ => "Engine stopped",
    };

    public string SaveHint => $"{Hotkey} saves the last {ClipSeconds} s";
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

    private async Task RestartEngineAsync()
    {
        if (_engine is null) return;
        var settings = Settings.ToEngineSettings();
        Hotkey = settings.SaveHotkey;
        ClipSeconds = settings.ClipSeconds;
        BufferSeconds = settings.BufferSeconds;
        _library?.Watch(settings.OutputDir);
        Raise(nameof(ClipsFolder));
        await _engine.RestartAsync();
    }

    private void SaveClip()
    {
        if (_engine?.SaveClip() == true) return;
        ShowToast("The engine isn't running, so there's nothing to save.", isError: true);
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
                AddActivity(ActivityKind.Recording, $"Recording {r.Label} at {r.Width}×{r.Height}, {r.Fps} fps");
                break;
            case RecordingStopped:
                _recording = null;
                break;
            case ClipSaved s:
                AddActivity(ActivityKind.Clip, $"Saved a {s.Seconds:0} s clip ({s.Megabytes:0.0} MB)");
                ShowToast($"Clip saved · {s.Seconds:0} s · {s.Megabytes:0.0} MB");
                _ = RefreshClipsAsync();
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
        Activity.Insert(0, new ActivityItem(time ?? DateTime.Now, kind, text));
        while (Activity.Count > MaxActivity) Activity.RemoveAt(Activity.Count - 1);
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
            if (!existing.TryGetValue(file.Path, out var clip))
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
            if (!games.Contains(Filters[i], StringComparer.OrdinalIgnoreCase)) Filters.RemoveAt(i);
        }
        foreach (var game in games)
        {
            if (!Filters.Contains(game)) Filters.Add(game);
        }
        if (!Filters.Contains(_filter)) Filter = AllGames;

        _libraryBytes = files.Sum(f => f.Bytes);
        ClipsToday = files.Count(f => f.Created.Date == DateTime.Today);
        ApplyFilter();
        UpdateGames();
        Raise(nameof(LibrarySize));
        Raise(nameof(ClipCount));
        Raise(nameof(HasClips));
        Raise(nameof(HasNoClips));
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
            if (_filter != AllGames && !string.Equals(clip.Game, _filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (_search.Length > 0 && !clip.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
                && !clip.When.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
            FilteredClips.Add(clip);
        }
        Raise(nameof(HasFilteredClips));
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
        SaveClipCommand.NotifyCanExecuteChanged();
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
