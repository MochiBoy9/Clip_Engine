using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly Func<Task>? _applyToEngine;
    private readonly DesktopSettings _desktop;
    private string _outputDir = "";
    private double _bufferSeconds = 60;
    private double _clipSeconds = 30;
    private FpsOption _fps;
    private ResolutionOption _resolution;
    private PlanViewModel? _plan;
    private double _bitrate;
    private string _codec = "auto";
    private bool _gameAudio = true;
    private bool _micAudio = true;
    private string _saveHotkey = "F8";
    private string _recordHotkey = "F9";
    private bool _recordWithoutGame;
    private bool _gameFocus = true;
    private bool _discordPresence = true;
    private bool _detection;
    private string _readingQuality = "medium";
    private bool _autoClip;
    private bool _journal;
    private double _clipBeforeSeconds;
    private double _clipAfterSeconds;
    private string _sidePanelHotkey;
    private bool _closeToTray;
    private bool _hasChanges;
    private string? _savedMessage;
    private string? _cleanupPreview;

    internal SettingsViewModel(EngineSettings engine, DesktopSettings desktop, Func<Task>? applyToEngine)
    {
        _desktop = desktop;
        _applyToEngine = applyToEngine;
        _fps = FpsOptions[0];
        ResolutionOptions = Resolutions.Choices.Select(c => new ResolutionOption(c)).ToList();
        _resolution = ResolutionOptions[0];
        AutoClipEvents = EngineSettings.AllEvents
            .Select(e => new EventToggle(e, EventKinds.Plural(e), () => { HasChanges = true; SavedMessage = null; }))
            .ToList();
        MoreClipKeys = new ClipKeybindsViewModel(() => [SaveHotkey, RecordHotkey], () => { HasChanges = true; SavedMessage = null; },
                                                 _bufferSeconds);
        Load(engine);
        _closeToTray = desktop.CloseToTray;
        _sidePanelHotkey = desktop.SidePanelHotkey;
        CleanUpNowCommand = new RelayCommand(() => CleanUpNowRequested?.Invoke());
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => HasChanges);
        RevertCommand = new RelayCommand(() =>
        {
            Load(EngineSettings.Load(AppPaths.EngineSettingsFile));
            HasChanges = false;
        }, () => HasChanges);
    }

    public IReadOnlyList<FpsOption> FpsOptions { get; } =
    [
        new(0, "Match monitor (up to 120)"), new(30, "30 fps"), new(60, "60 fps"), new(120, "120 fps"),
        new(144, "144 fps"), new(165, "165 fps"), new(240, "240 fps"),
    ];

    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand CleanUpNowCommand { get; }

    // The plan card at the top of the right column. Its plan sets the highest recording resolution.
    public PlanViewModel? Plan
    {
        get => _plan;
        init
        {
            _plan = value;
            if (value is not null) value.PlanChanged += plan => _ = OnPlanChangedAsync(plan);
            RefreshResolutions();
        }
    }

    private Services.Plan CurrentPlan => _plan?.Plan ?? Services.Plan.Free;

    // The highest resolution allowed: the plan's, or a special key's.
    private int MaxLines => _plan?.MaxLines ?? Plans.Free.Lines;

    public IReadOnlyList<ResolutionOption> ResolutionOptions { get; }

    public ResolutionOption Resolution
    {
        get => _resolution;
        set
        {
            if (value is null || !value.IsAvailable) return;
            if (Change(ref _resolution, value)) Raise(nameof(ResolutionHint));
        }
    }

    public string ResolutionHint
    {
        get
        {
            var effective = Resolutions.Effective(_resolution.Value, MaxLines);
            var plan = _plan?.IsSpecial == true && MaxLines != Plans.For(CurrentPlan).Lines
                ? $"Your special key records up to {Resolutions.Name(MaxLines)}."
                : CurrentPlan switch
            {
                Services.Plan.Ultra => "Ultra records up to 4K.",
                Services.Plan.Pro => "Pro records up to 1440p, and Ultra up to 4K.",
                _ => "Free records up to 1080p, Pro up to 1440p and Ultra up to 4K.",
            };
            return $"Records at {Resolutions.Name(effective)}, or the monitor's own size if that's smaller. Lower is lighter on the game and takes less space. {plan}";
        }
    }

    // The engine's file gets the smaller of the picked resolution and the plan's, at start and whenever the plan
    // changes, so a plan that ended stops recording above Free's.
    internal bool EnforceResolution()
    {
        try
        {
            var engine = EngineSettings.Load(AppPaths.EngineSettingsFile);
            var effective = Resolutions.Effective(_desktop.Resolution, MaxLines);
            if (engine.Resolution == effective) return false;
            engine.Resolution = effective;
            engine.Save(AppPaths.EngineSettingsFile);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task OnPlanChangedAsync(Services.Plan plan)
    {
        RefreshResolutions();
        if (_applyToEngine is null) return;
        if (EnforceResolution()) await _applyToEngine();
    }

    private void RefreshResolutions()
    {
        foreach (var option in ResolutionOptions) option.Refresh(MaxLines);
        if (!HasChanges) _resolution = ResolutionOptions.FirstOrDefault(o => o.Value == _desktop.Resolution && o.IsAvailable) ?? _resolution;
        if (!_resolution.IsAvailable) _resolution = ResolutionOptions[0];
        Raise(nameof(Resolution));
        Raise(nameof(ResolutionHint));
    }

    // The updates card under it.
    public UpdatesViewModel? Updates { get; init; }

    public event Action<string>? SidePanelHotkeyChanged;
    public event Action? StorageRulesChanged;
    public event Action? CleanUpNowRequested;

    public string OutputDir { get => _outputDir; set => Change(ref _outputDir, value); }
    public string OutputDirShown => string.IsNullOrWhiteSpace(OutputDir) ? AppPaths.DefaultClipsDir : OutputDir;

    public double BufferSeconds
    {
        get => _bufferSeconds;
        set
        {
            if (!Change(ref _bufferSeconds, Math.Round(value))) return;
            Raise(nameof(BufferText));
            if (ClipSeconds > _bufferSeconds) ClipSeconds = _bufferSeconds;
            MoreClipKeys.MaxSeconds = _bufferSeconds;
        }
    }

    public double ClipSeconds
    {
        get => _clipSeconds;
        set
        {
            if (Change(ref _clipSeconds, Math.Round(Math.Min(value, BufferSeconds)))) Raise(nameof(ClipText));
        }
    }

    public string BufferText => FormatSeconds(BufferSeconds);
    public string ClipText => FormatSeconds(ClipSeconds);

    public FpsOption Fps { get => _fps; set => Change(ref _fps, value); }

    public double Bitrate
    {
        get => _bitrate;
        set
        {
            if (Change(ref _bitrate, Math.Round(value / 5) * 5)) Raise(nameof(BitrateText));
        }
    }

    public string BitrateText => Bitrate <= 0 ? "Auto" : $"{Bitrate:0} Mbps";

    public bool CodecAuto { get => _codec == "auto"; set { if (value) SetCodec("auto"); } }
    public bool CodecH264 { get => _codec == "h264"; set { if (value) SetCodec("h264"); } }
    public bool CodecHevc { get => _codec == "hevc"; set { if (value) SetCodec("hevc"); } }

    public bool GameAudio { get => _gameAudio; set => Change(ref _gameAudio, value); }
    public bool MicAudio { get => _micAudio; set => Change(ref _micAudio, value); }
    public string SaveHotkey { get => _saveHotkey; set { if (Change(ref _saveHotkey, value)) MoreClipKeys.Recheck(); } }
    public string RecordHotkey { get => _recordHotkey; set { if (Change(ref _recordHotkey, value)) MoreClipKeys.Recheck(); } }

    // Keys besides the save hotkey that each save a clip of their own length.
    public ClipKeybindsViewModel MoreClipKeys { get; }
    public bool RecordWithoutGame { get => _recordWithoutGame; set => Change(ref _recordWithoutGame, value); }
    public bool GameFocus { get => _gameFocus; set => Change(ref _gameFocus, value); }
    public bool DiscordPresence { get => _discordPresence; set => Change(ref _discordPresence, value); }

    // Reading kills and other moments off the screen, and saving clips around them.
    public bool Detection { get => _detection; set => Change(ref _detection, value); }

    // How hard the screen is read: higher misses and misreads less, and uses more CPU.
    public bool QualityLow { get => _readingQuality == "low"; set { if (value) SetReadingQuality("low"); } }
    public bool QualityMedium { get => _readingQuality == "medium"; set { if (value) SetReadingQuality("medium"); } }
    public bool QualityHigh { get => _readingQuality == "high"; set { if (value) SetReadingQuality("high"); } }
    public bool QualityUltra { get => _readingQuality == "ultra"; set { if (value) SetReadingQuality("ultra"); } }

    public string ReadingQualityHint => _readingQuality switch
    {
        "low" => "One quick read of each part of the screen. Lightest on the CPU; small text gets misread more.",
        "high" => "Reads small text bigger, two ways, and a little more often. Fewer misses, more CPU.",
        "ultra" => "Reads three ways, twice as often. Catches the most, and uses the most CPU.",
        _ => "Reads small text bigger and sharper than Low, at little extra cost. Recommended.",
    };
    public bool AutoClip { get => _autoClip; set => Change(ref _autoClip, value); }
    public bool Journal { get => _journal; set => Change(ref _journal, value); }
    public IReadOnlyList<EventToggle> AutoClipEvents { get; }

    public double ClipBeforeSeconds
    {
        get => _clipBeforeSeconds;
        set { if (Change(ref _clipBeforeSeconds, Math.Round(value))) Raise(nameof(ClipBeforeText)); }
    }

    public double ClipAfterSeconds
    {
        get => _clipAfterSeconds;
        set { if (Change(ref _clipAfterSeconds, Math.Round(value))) Raise(nameof(ClipAfterText)); }
    }

    public string ClipBeforeText => FormatSeconds(ClipBeforeSeconds);
    public string ClipAfterText => FormatSeconds(ClipAfterSeconds);

    // The desktop-only settings apply immediately.
    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (!Set(ref _closeToTray, value)) return;
            _desktop.CloseToTray = value;
            TrySaveDesktop();
        }
    }

    // Side panel and sharing.
    public string SidePanelHotkey
    {
        get => _sidePanelHotkey;
        set
        {
            value = value?.Trim() ?? "";
            if (!Set(ref _sidePanelHotkey, value)) return;
            Raise(nameof(SidePanelHotkeyProblem));
            if (!HotkeyText.TryParse(value, out _, out _)) return;
            _desktop.SidePanelHotkey = value;
            TrySaveDesktop();
            SidePanelHotkeyChanged?.Invoke(value);
        }
    }

    public string? SidePanelHotkeyProblem =>
        HotkeyText.TryParse(_sidePanelHotkey, out _, out _) ? null : "Use a key with Ctrl, Alt or Shift, like Alt+Shift+C";

    public bool DockLeft { get => _desktop.SidePanelEdge == "left"; set { if (value) SetEdge("left"); } }
    public bool DockRight { get => _desktop.SidePanelEdge != "left"; set { if (value) SetEdge("right"); } }

    public bool Share10 { get => _desktop.ShareLimitMb == 10; set { if (value) SetShareLimit(10); } }
    public bool Share50 { get => _desktop.ShareLimitMb == 50; set { if (value) SetShareLimit(50); } }
    public bool Share500 { get => _desktop.ShareLimitMb == 500; set { if (value) SetShareLimit(500); } }

    // Storage cleanup, applied by the app on a timer (see MainViewModel).
    public bool KeepForever { get => _desktop.DeleteAfterDays <= 0; set { if (value) SetDeleteAfter(0); } }
    public bool Keep7Days { get => _desktop.DeleteAfterDays == 7; set { if (value) SetDeleteAfter(7); } }
    public bool Keep30Days { get => _desktop.DeleteAfterDays == 30; set { if (value) SetDeleteAfter(30); } }
    public bool Keep90Days { get => _desktop.DeleteAfterDays == 90; set { if (value) SetDeleteAfter(90); } }

    public bool NoSizeLimit { get => _desktop.MaxLibraryGb <= 0; set { if (value) SetSizeLimit(0); } }
    public bool Limit50Gb { get => _desktop.MaxLibraryGb == 50; set { if (value) SetSizeLimit(50); } }
    public bool Limit100Gb { get => _desktop.MaxLibraryGb == 100; set { if (value) SetSizeLimit(100); } }
    public bool Limit250Gb { get => _desktop.MaxLibraryGb == 250; set { if (value) SetSizeLimit(250); } }

    // What the cleanup would remove right now, or null while it's off.
    public string? CleanupPreview
    {
        get => _cleanupPreview;
        internal set { if (Set(ref _cleanupPreview, value)) Raise(nameof(HasCleanupPreview)); }
    }

    public bool HasCleanupPreview => _cleanupPreview is not null;

    public bool GameNotifications
    {
        get => _desktop.GameNotifications;
        set
        {
            if (_desktop.GameNotifications == value) return;
            _desktop.GameNotifications = value;
            TrySaveDesktop();
            Raise();
        }
    }

    public bool MomentNotices
    {
        get => _desktop.MomentNotices;
        set
        {
            if (_desktop.MomentNotices == value) return;
            _desktop.MomentNotices = value;
            TrySaveDesktop();
            Raise();
        }
    }

    public bool GameArtOnline
    {
        get => _desktop.GameArtOnline;
        set
        {
            if (_desktop.GameArtOnline == value) return;
            _desktop.GameArtOnline = value;
            TrySaveDesktop();
            Raise();
        }
    }

    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            if (!Set(ref _hasChanges, value)) return;
            SaveCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
        }
    }

    public string? SavedMessage { get => _savedMessage; private set => Set(ref _savedMessage, value); }

    internal EngineSettings ToEngineSettings() => new()
    {
        OutputDir = OutputDir.Trim(),
        BufferSeconds = (int)BufferSeconds,
        ClipSeconds = (int)ClipSeconds,
        Fps = Fps.Value,
        BitrateMbps = (int)Bitrate,
        Resolution = Resolutions.Effective(_resolution.Value, MaxLines),
        Codec = _codec,
        GameAudio = GameAudio,
        MicAudio = MicAudio,
        SaveHotkey = string.IsNullOrWhiteSpace(SaveHotkey) ? "F8" : SaveHotkey.Trim(),
        MoreClipHotkeys = MoreClipKeys.ToKeybinds(),
        RecordHotkey = string.IsNullOrWhiteSpace(RecordHotkey) ? "F9" : RecordHotkey.Trim(),
        RecordWithoutGame = RecordWithoutGame,
        GameFocus = GameFocus,
        DiscordPresence = DiscordPresence,
        Detection = Detection,
        ReadingQuality = _readingQuality,
        AutoClip = AutoClip,
        Journal = Journal,
        AutoClipEvents = AutoClipEvents.Where(e => e.IsOn).Select(e => e.Name).ToList(),
        ClipBeforeSeconds = (int)ClipBeforeSeconds,
        ClipAfterSeconds = (int)ClipAfterSeconds,
    };

    private async Task SaveAsync()
    {
        if (MoreClipKeys.FirstProblem is { } problem)
        {
            SavedMessage = $"Fix the clip keys first. {problem}";
            return;
        }
        try
        {
            ToEngineSettings().Save(AppPaths.EngineSettingsFile);
            _desktop.Resolution = _resolution.Value;
            TrySaveDesktop();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SavedMessage = $"Couldn't save the settings: {e.Message}";
            return;
        }
        HasChanges = false;
        SavedMessage = "Saved. The engine is restarting with the new settings.";
        if (_applyToEngine is not null) await _applyToEngine();
    }

    private void Load(EngineSettings s)
    {
        _outputDir = s.OutputDir;
        _bufferSeconds = Math.Clamp(s.BufferSeconds, 10, 600);
        _clipSeconds = Math.Clamp(s.ClipSeconds, 5, (int)_bufferSeconds);
        _fps = FpsOptions.FirstOrDefault(o => o.Value == s.Fps) ?? new FpsOption(s.Fps, $"{s.Fps} fps");
        _resolution = ResolutionOptions.FirstOrDefault(o => o.Value == _desktop.Resolution && o.IsAvailable) ?? ResolutionOptions[0];
        _bitrate = Math.Clamp(s.BitrateMbps, 0, 300);
        _codec = s.Codec is "h264" or "hevc" ? s.Codec : "auto";
        _gameAudio = s.GameAudio;
        _micAudio = s.MicAudio;
        _saveHotkey = s.SaveHotkey;
        _recordHotkey = s.RecordHotkey;
        MoreClipKeys.MaxSeconds = _bufferSeconds;
        MoreClipKeys.Load(s.MoreClipHotkeys);
        _recordWithoutGame = s.RecordWithoutGame;
        _gameFocus = s.GameFocus;
        _discordPresence = s.DiscordPresence;
        _detection = s.Detection;
        _readingQuality = EngineSettings.ReadingQualities.Contains(s.ReadingQuality) ? s.ReadingQuality : "medium";
        _autoClip = s.AutoClip;
        _journal = s.Journal;
        _clipBeforeSeconds = Math.Clamp(s.ClipBeforeSeconds, 0, 60);
        _clipAfterSeconds = Math.Clamp(s.ClipAfterSeconds, 0, 30);
        foreach (var toggle in AutoClipEvents) toggle.Load(s.AutoClipEvents.Contains(toggle.Name));
        Raise(string.Empty);
    }

    private void SetEdge(string edge)
    {
        if (_desktop.SidePanelEdge == edge) return;
        _desktop.SidePanelEdge = edge;
        TrySaveDesktop();
        Raise(nameof(DockLeft));
        Raise(nameof(DockRight));
    }

    private void SetDeleteAfter(int days)
    {
        if (_desktop.DeleteAfterDays == days) return;
        _desktop.DeleteAfterDays = days;
        TrySaveDesktop();
        Raise(nameof(KeepForever));
        Raise(nameof(Keep7Days));
        Raise(nameof(Keep30Days));
        Raise(nameof(Keep90Days));
        StorageRulesChanged?.Invoke();
    }

    private void SetSizeLimit(int gigabytes)
    {
        if (_desktop.MaxLibraryGb == gigabytes) return;
        _desktop.MaxLibraryGb = gigabytes;
        TrySaveDesktop();
        Raise(nameof(NoSizeLimit));
        Raise(nameof(Limit50Gb));
        Raise(nameof(Limit100Gb));
        Raise(nameof(Limit250Gb));
        StorageRulesChanged?.Invoke();
    }

    private void SetShareLimit(int megabytes)
    {
        if (_desktop.ShareLimitMb == megabytes) return;
        _desktop.ShareLimitMb = megabytes;
        TrySaveDesktop();
        Raise(nameof(Share10));
        Raise(nameof(Share50));
        Raise(nameof(Share500));
    }

    private void SetReadingQuality(string quality)
    {
        if (!Change(ref _readingQuality, quality, nameof(QualityLow))) return;
        Raise(nameof(QualityMedium));
        Raise(nameof(QualityHigh));
        Raise(nameof(QualityUltra));
        Raise(nameof(ReadingQualityHint));
    }

    private void SetCodec(string codec)
    {
        if (!Change(ref _codec, codec, nameof(CodecAuto))) return;
        Raise(nameof(CodecH264));
        Raise(nameof(CodecHevc));
    }

    private bool Change<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        if (name == nameof(OutputDir)) Raise(nameof(OutputDirShown));
        HasChanges = true;
        SavedMessage = null;
        return true;
    }

    private void TrySaveDesktop()
    {
        try
        {
            _desktop.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string FormatSeconds(double seconds) =>
        seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60:0} min" : seconds >= 60 ? $"{(int)seconds / 60}m {(int)seconds % 60}s" : $"{seconds:0} s";
}

// One of the moments that can trigger an automatic clip.
public sealed class EventToggle(string name, string label, Action changed) : ObservableObject
{
    private bool _isOn;

    public string Name { get; } = name;
    public string Label { get; } = label;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (Set(ref _isOn, value)) changed();
        }
    }

    internal void Load(bool on)
    {
        _isOn = on;
        Raise(nameof(IsOn));
    }
}
