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
    private bool _voiceClips;
    private string _voicePhrases = "clip that";
    private double _voiceClipSeconds;
    private string _voiceSensitivity = "medium";
    private CancellationTokenSource? _levelsPending;
    private string _saveHotkey = "F8";
    private string _recordHotkey = "F9";
    private bool _recordWithoutGame;
    private bool _gameFocus = true;
    private bool _discordPresence = true;
    private bool _detection;
    private string _readingQuality = "medium";
    private bool _autoClip;
    private bool _clipLoneDeaths = true;
    private bool _autoHighlights = true;
    private bool _journal;
    private double _clipBeforeSeconds;
    private double _clipAfterSeconds;
    private string _sidePanelHotkey;
    private bool _closeToTray;
    private bool _hasChanges;
    private string? _savedMessage;
    private string? _cleanupPreview;
    private string _statsOverlayHotkey;
    private FrameTimingState _frameTiming;
    private string? _frameTimingProblem;

    private enum FrameTimingState { Allowed, NeedsPermission, SignInAgain }

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
        GameTrack = new AudioTrackViewModel("game", "Game", MarkChanged, OnLevelChanged);
        MicTrack = new AudioTrackViewModel("mic", "Microphone", MarkChanged, OnLevelChanged);
        DiscordTrack = new AudioTrackViewModel("discord", "Discord", MarkChanged, OnLevelChanged);
        AppsTrack = new AudioTrackViewModel("apps", "Other apps", MarkChanged, OnLevelChanged);
        AudioTracks = [GameTrack, MicTrack, DiscordTrack, AppsTrack];
        MoreClipKeys = new ClipKeybindsViewModel(() => [SaveHotkey, RecordHotkey], () => { HasChanges = true; SavedMessage = null; },
                                                 _bufferSeconds);
        Load(engine);
        _closeToTray = desktop.CloseToTray;
        _sidePanelHotkey = desktop.SidePanelHotkey;
        CleanUpNowCommand = new RelayCommand(() => CleanUpNowRequested?.Invoke());
        _statsOverlayHotkey = desktop.StatsOverlay.Hotkey;
        _frameTiming = !OperatingSystem.IsWindows() || Services.Stats.FrameTimingAccess.Allowed() ? FrameTimingState.Allowed : FrameTimingState.NeedsPermission;
        AllowFrameTimingCommand = new RelayCommand(() => _ = AllowFrameTimingAsync(), () => _frameTiming == FrameTimingState.NeedsPermission);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => HasChanges);
        RevertCommand = new RelayCommand(() =>
        {
            Load(EngineSettings.Load(AppPaths.EngineSettingsFile));
            HasChanges = false;
        }, () => HasChanges);
    }

    // Sends a line to the running engine, for changes it takes at once (volumes and mutes). Null when no engine runs.
    internal Func<string, bool>? EngineCommand { get; init; }

    public IReadOnlyList<FpsOption> FpsOptions { get; } =
    [
        new(0, "Match monitor (up to 120)"), new(30, "30 fps"), new(60, "60 fps"), new(120, "120 fps"),
        new(144, "144 fps"), new(165, "165 fps"), new(240, "240 fps"),
    ];

    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand CleanUpNowCommand { get; }
    public RelayCommand AllowFrameTimingCommand { get; }

    // The plan card at the top of the right column. Every plan records up to 4K; the plan sets the cloud library.
    public PlanViewModel? Plan
    {
        get => _plan;
        init
        {
            _plan = value;
            if (value is not null) value.PlanChanged += _ => Raise(nameof(ResolutionHint));
        }
    }

    private Services.Plan CurrentPlan => _plan?.Plan ?? Services.Plan.Free;

    public IReadOnlyList<ResolutionOption> ResolutionOptions { get; }

    public ResolutionOption Resolution
    {
        get => _resolution;
        set
        {
            if (value is null) return;
            if (!Change(ref _resolution, value)) return;
            Raise(nameof(ResolutionHint));
            RaiseBufferAdvice();
        }
    }

    public string ResolutionHint
    {
        get
        {
            var effective = Resolutions.Effective(_resolution.Value);
            var cloud = _plan?.CloudLines ?? Plans.Free.CloudLines;
            var plan = cloud >= Resolutions.Max
                ? "Every plan records up to 4K, and yours keeps 4K clips in 4K in the cloud."
                : $"Every plan records up to 4K. Clips go to the cloud at {Resolutions.Name(cloud)} on {Plans.For(CurrentPlan).Label}; Pro and Ultra keep them in 4K.";
            return $"Records at {Resolutions.Name(effective)}, or the monitor's own size if that's smaller. Lower is lighter on the game and takes less space. {plan}";
        }
    }

    // The engine's file gets the picked resolution, at most 4K, at start. Older versions wrote a lower plan cap there.
    internal bool EnforceResolution()
    {
        try
        {
            var engine = EngineSettings.Load(AppPaths.EngineSettingsFile);
            var effective = Resolutions.Effective(_desktop.Resolution);
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

    // The updates card under it.
    public UpdatesViewModel? Updates { get; init; }

    // Settings > Notifications: which pop-ups show, or none.
    public NotificationsViewModel? Notifications { get; init; }

    public event Action<string>? SidePanelHotkeyChanged;
    public event Action<string>? StatsOverlayHotkeyChanged;
    // Turned on or off, or what it shows or where changed.
    public event Action? StatsOverlayChanged;
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
            if (VoiceClipSeconds > _bufferSeconds) VoiceClipSeconds = _bufferSeconds;
            MoreClipKeys.MaxSeconds = _bufferSeconds;
            RaiseBufferAdvice();
        }
    }

    public int MaxBufferSeconds => ReplayMemory.MaxSeconds;

    // What the replay buffer takes in memory at the resolution, frame rate and bitrate picked, and a warning when this
    // PC can't comfortably spare it.
    private ReplayMemory.Advice BufferAdvice
    {
        get
        {
            var (lines, hz) = MainScreen.Value;
            var recorded = Math.Min(lines, Resolutions.Effective(_resolution.Value));
            var fps = Fps.Value > 0 ? Fps.Value : Math.Clamp(hz, 24, 120);
            var (memory, cores) = ThisPc.Value;
            return ReplayMemory.For((int)BufferSeconds, recorded, fps, (int)Bitrate, memory, cores);
        }
    }

    private static readonly Lazy<(int Lines, int Hz)> MainScreen = new(ReplayMemory.MainMonitor);
    private static readonly Lazy<(long Memory, int Cores)> ThisPc = new(ReplayMemory.ThisPc);

    public string BufferHint => BufferAdvice is { Level: ReplayAdviceLevel.Fine or ReplayAdviceLevel.Note } advice
        ? $"Recent gameplay kept in memory, ready to save. {advice.Text}"
        : "Recent gameplay kept in memory, ready to save.";

    public string? BufferWarning => BufferAdvice is { Level: ReplayAdviceLevel.Warning or ReplayAdviceLevel.TooBig } advice ? advice.Text : null;
    public bool HasBufferWarning => BufferWarning is not null;

    private void RaiseBufferAdvice()
    {
        Raise(nameof(BufferHint));
        Raise(nameof(BufferWarning));
        Raise(nameof(HasBufferWarning));
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

    public FpsOption Fps
    {
        get => _fps;
        set
        {
            if (Change(ref _fps, value)) RaiseBufferAdvice();
        }
    }

    public double Bitrate
    {
        get => _bitrate;
        set
        {
            if (!Change(ref _bitrate, Math.Round(value / 5) * 5)) return;
            Raise(nameof(BitrateText));
            RaiseBufferAdvice();
        }
    }

    public string BitrateText => Bitrate <= 0 ? "Auto" : $"{Bitrate:0} Mbps";

    public bool CodecAuto { get => _codec == "auto"; set { if (value) SetCodec("auto"); } }
    public bool CodecH264 { get => _codec == "h264"; set { if (value) SetCodec("h264"); } }
    public bool CodecHevc { get => _codec == "hevc"; set { if (value) SetCodec("hevc"); } }

    // The audio tracks: whether each is recorded (saved with the other settings), and its volume and mute, which apply
    // at once, even mid-game.
    public AudioTrackViewModel GameTrack { get; }
    public AudioTrackViewModel MicTrack { get; }
    public AudioTrackViewModel DiscordTrack { get; }
    public AudioTrackViewModel AppsTrack { get; }
    public IReadOnlyList<AudioTrackViewModel> AudioTracks { get; }

    // Saying a phrase saves a clip.
    public bool VoiceClips { get => _voiceClips; set => Change(ref _voiceClips, value); }

    public string VoicePhrasesText
    {
        get => _voicePhrases;
        set
        {
            if (!Change(ref _voicePhrases, value ?? "")) return;
            Raise(nameof(VoicePhrasesProblem));
            Raise(nameof(VoicePhrasesAdvice));
        }
    }

    public string? VoicePhrasesProblem => VoicePhrases.Problem(_voicePhrases);
    public string? VoicePhrasesAdvice => VoicePhrasesProblem is null ? VoicePhrases.Advice(_voicePhrases) : null;

    // The length of a clip saved by voice, unless a phrase has its own; 0 is the usual clip length.
    public double VoiceClipSeconds
    {
        get => _voiceClipSeconds;
        set
        {
            if (Change(ref _voiceClipSeconds, Math.Round(Math.Min(value, BufferSeconds)))) Raise(nameof(VoiceClipText));
        }
    }

    public string VoiceClipText => VoiceClipSeconds <= 0 ? "Clip length" : FormatSeconds(VoiceClipSeconds);

    public bool VoiceLow { get => _voiceSensitivity == "low"; set { if (value) SetVoiceSensitivity("low"); } }
    public bool VoiceMedium { get => _voiceSensitivity == "medium"; set { if (value) SetVoiceSensitivity("medium"); } }
    public bool VoiceHigh { get => _voiceSensitivity == "high"; set { if (value) SetVoiceSensitivity("high"); } }

    public string VoiceSensitivityHint => _voiceSensitivity switch
    {
        "low" => "Only a clear, close match counts. The fewest clips by accident; you may need to say it twice in a noisy room.",
        "high" => "Takes the loosest match. For noisy rooms and voices speech recognition finds hard; expect the odd clip by accident.",
        _ => "A good match counts. Recommended.",
    };
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
    public bool ClipLoneDeaths { get => _clipLoneDeaths; set => Change(ref _clipLoneDeaths, value); }
    public bool AutoHighlights { get => _autoHighlights; set => Change(ref _autoHighlights, value); }
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

    // The stats overlay over the game.
    public bool StatsOverlayOn
    {
        get => _desktop.StatsOverlay.On;
        set => SetStat(o => o.On, (o, v) => o.On = v, value);
    }

    public bool StatsFps { get => _desktop.StatsOverlay.Fps; set => SetStat(o => o.Fps, (o, v) => o.Fps = v, value); }
    public bool StatsLows { get => _desktop.StatsOverlay.Lows; set => SetStat(o => o.Lows, (o, v) => o.Lows = v, value); }
    public bool StatsCpu { get => _desktop.StatsOverlay.Cpu; set => SetStat(o => o.Cpu, (o, v) => o.Cpu = v, value); }
    public bool StatsGpu { get => _desktop.StatsOverlay.Gpu; set => SetStat(o => o.Gpu, (o, v) => o.Gpu = v, value); }
    public bool StatsGpuTemp { get => _desktop.StatsOverlay.GpuTemp; set => SetStat(o => o.GpuTemp, (o, v) => o.GpuTemp = v, value); }
    public bool StatsVram { get => _desktop.StatsOverlay.Vram; set => SetStat(o => o.Vram, (o, v) => o.Vram = v, value); }
    public bool StatsRam { get => _desktop.StatsOverlay.Ram; set => SetStat(o => o.Ram, (o, v) => o.Ram = v, value); }

    public bool StatsTopLeft { get => _desktop.StatsOverlay.Corner == "top-left"; set { if (value) SetStatsCorner("top-left"); } }
    public bool StatsTopRight { get => _desktop.StatsOverlay.Corner == "top-right"; set { if (value) SetStatsCorner("top-right"); } }
    public bool StatsBottomLeft { get => _desktop.StatsOverlay.Corner == "bottom-left"; set { if (value) SetStatsCorner("bottom-left"); } }
    public bool StatsBottomRight { get => _desktop.StatsOverlay.Corner == "bottom-right"; set { if (value) SetStatsCorner("bottom-right"); } }

    public string StatsOverlayHotkey
    {
        get => _statsOverlayHotkey;
        set
        {
            value = value?.Trim() ?? "";
            if (!Set(ref _statsOverlayHotkey, value)) return;
            Raise(nameof(StatsOverlayHotkeyProblem));
            if (!HotkeyText.TryParse(value, out _, out _)) return;
            _desktop.StatsOverlay.Hotkey = value;
            TrySaveDesktop();
            StatsOverlayHotkeyChanged?.Invoke(value);
        }
    }

    public string? StatsOverlayHotkeyProblem =>
        HotkeyText.TryParse(_statsOverlayHotkey, out _, out _) ? null : "Use a key with Ctrl, Alt or Shift, like Alt+Shift+F";

    // FPS needs Windows' frame timing, which it shares with administrators and Performance Log Users.
    public bool FrameTimingNeedsPermission => _frameTiming == FrameTimingState.NeedsPermission;
    public bool FrameTimingNeedsSignIn => _frameTiming == FrameTimingState.SignInAgain;

    public string? FrameTimingProblem
    {
        get => _frameTimingProblem;
        private set { if (Set(ref _frameTimingProblem, value)) Raise(nameof(HasFrameTimingProblem)); }
    }

    public bool HasFrameTimingProblem => _frameTimingProblem is not null;

    // The overlay found Windows wouldn't share frame timing after all (or would).
    internal void OnFrameTimingChecked(Services.Stats.FrameTiming timing)
    {
        if (timing == Services.Stats.FrameTiming.Reading) SetFrameTiming(FrameTimingState.Allowed);
        else if (timing == Services.Stats.FrameTiming.NeedsPermission && _frameTiming == FrameTimingState.Allowed) SetFrameTiming(FrameTimingState.NeedsPermission);
    }

    private async Task AllowFrameTimingAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        FrameTimingProblem = null;
        var problem = await Services.Stats.FrameTimingAccess.RequestAsync();
        if (problem is null) SetFrameTiming(FrameTimingState.SignInAgain);
        else FrameTimingProblem = problem;
    }

    private void SetFrameTiming(FrameTimingState state)
    {
        if (_frameTiming == state) return;
        _frameTiming = state;
        Raise(nameof(FrameTimingNeedsPermission));
        Raise(nameof(FrameTimingNeedsSignIn));
        AllowFrameTimingCommand.NotifyCanExecuteChanged();
    }

    private void SetStat(Func<StatsOverlaySettings, bool> get, Action<StatsOverlaySettings, bool> set, bool value,
                         [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (get(_desktop.StatsOverlay) == value) return;
        set(_desktop.StatsOverlay, value);
        TrySaveDesktop();
        Raise(name);
        StatsOverlayChanged?.Invoke();
    }

    private void SetStatsCorner(string corner)
    {
        if (_desktop.StatsOverlay.Corner == corner) return;
        _desktop.StatsOverlay.Corner = corner;
        TrySaveDesktop();
        Raise(nameof(StatsTopLeft));
        Raise(nameof(StatsTopRight));
        Raise(nameof(StatsBottomLeft));
        Raise(nameof(StatsBottomRight));
        StatsOverlayChanged?.Invoke();
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
        Resolution = Resolutions.Effective(_resolution.Value),
        Codec = _codec,
        GameAudio = GameTrack.IsRecorded,
        MicAudio = MicTrack.IsRecorded,
        DiscordAudio = DiscordTrack.IsRecorded,
        AppsAudio = AppsTrack.IsRecorded,
        Volumes = AudioTracks.ToDictionary(t => t.Key, t => (int)t.Volume),
        MutedAudio = AudioTracks.Where(t => t.IsMuted).Select(t => t.Key).ToList(),
        VoiceClips = VoiceClips,
        VoicePhrases = string.Join(", ", VoicePhrases.Parse(_voicePhrases).Select(p => p.Seconds > 0 ? $"{p.Words} {p.Seconds}" : p.Words)),
        VoiceClipSeconds = (int)VoiceClipSeconds,
        VoiceSensitivity = _voiceSensitivity,
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
        ClipLoneDeaths = ClipLoneDeaths,
        AutoHighlights = AutoHighlights,
    };

    private async Task SaveAsync()
    {
        if (MoreClipKeys.FirstProblem is { } problem)
        {
            SavedMessage = $"Fix the clip keys first. {problem}";
            return;
        }
        if (VoiceClips && VoicePhrasesProblem is { } phrases)
        {
            SavedMessage = $"Fix the voice phrases first. {phrases}";
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
        _bufferSeconds = Math.Clamp(s.BufferSeconds, 10, ReplayMemory.MaxSeconds);
        _clipSeconds = Math.Clamp(s.ClipSeconds, 5, (int)_bufferSeconds);
        _fps = FpsOptions.FirstOrDefault(o => o.Value == s.Fps) ?? new FpsOption(s.Fps, $"{s.Fps} fps");
        _resolution = ResolutionOptions.FirstOrDefault(o => o.Value == _desktop.Resolution) ?? ResolutionOptions[0];
        _bitrate = Math.Clamp(s.BitrateMbps, 0, 300);
        _codec = s.Codec is "h264" or "hevc" ? s.Codec : "auto";
        GameTrack.Load(s.GameAudio, s.VolumeOf("game"), s.IsMuted("game"));
        MicTrack.Load(s.MicAudio, s.VolumeOf("mic"), s.IsMuted("mic"));
        DiscordTrack.Load(s.DiscordAudio, s.VolumeOf("discord"), s.IsMuted("discord"));
        AppsTrack.Load(s.AppsAudio, s.VolumeOf("apps"), s.IsMuted("apps"));
        _voiceClips = s.VoiceClips;
        _voicePhrases = s.VoicePhrases;
        _voiceClipSeconds = Math.Clamp(s.VoiceClipSeconds, 0, (int)_bufferSeconds);
        _voiceSensitivity = EngineSettings.VoiceSensitivities.Contains(s.VoiceSensitivity) ? s.VoiceSensitivity : "medium";
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
        _clipLoneDeaths = s.ClipLoneDeaths;
        _autoHighlights = s.AutoHighlights;
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

    private void SetVoiceSensitivity(string sensitivity)
    {
        if (!Change(ref _voiceSensitivity, sensitivity, nameof(VoiceLow))) return;
        Raise(nameof(VoiceMedium));
        Raise(nameof(VoiceHigh));
        Raise(nameof(VoiceSensitivityHint));
    }

    private void MarkChanged()
    {
        HasChanges = true;
        SavedMessage = null;
    }

    // A volume or mute changed: once the slider settles, the engine's file gets the new levels and a running engine
    // is told to take them.
    private async void OnLevelChanged()
    {
        _levelsPending?.Cancel();
        var pending = _levelsPending = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, pending.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        ApplyLevelsNow();
    }

    internal void ApplyLevelsNow()
    {
        try
        {
            new EngineSettings
            {
                Volumes = AudioTracks.ToDictionary(t => t.Key, t => (int)t.Volume),
                MutedAudio = AudioTracks.Where(t => t.IsMuted).Select(t => t.Key).ToList(),
            }.SaveAudioLevels(AppPaths.EngineSettingsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SavedMessage = $"Couldn't save the volume: {e.Message}";
            return;
        }
        EngineCommand?.Invoke("reload-audio");
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

// One audio track on the Settings page: whether it's recorded, which takes a save, and its volume and mute, which apply
// as they change.
public sealed class AudioTrackViewModel(string key, string title, Action recordedChanged, Action levelChanged) : ObservableObject
{
    private bool _isRecorded = true;
    private double _volume = 100;
    private bool _isMuted;

    public string Key { get; } = key;
    public string Title { get; } = title;

    public bool IsRecorded
    {
        get => _isRecorded;
        set
        {
            if (Set(ref _isRecorded, value)) recordedChanged();
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(Math.Round(value / 5) * 5, 0, EngineSettings.MaxVolume))) return;
            Raise(nameof(VolumeText));
            levelChanged();
        }
    }

    public string VolumeText => IsMuted ? "Muted" : $"{Volume:0}%";

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!Set(ref _isMuted, value)) return;
            Raise(nameof(VolumeText));
            levelChanged();
        }
    }

    internal void Load(bool recorded, int volume, bool muted)
    {
        _isRecorded = recorded;
        _volume = Math.Clamp(volume, 0, EngineSettings.MaxVolume);
        _isMuted = muted;
        Raise(nameof(IsRecorded));
        Raise(nameof(Volume));
        Raise(nameof(IsMuted));
        Raise(nameof(VolumeText));
    }
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
