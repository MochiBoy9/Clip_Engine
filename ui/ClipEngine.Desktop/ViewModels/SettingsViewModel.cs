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
    private double _bitrate;
    private string _codec = "auto";
    private bool _gameAudio = true;
    private bool _micAudio = true;
    private string _saveHotkey = "F8";
    private bool _recordWithoutGame;
    private bool _closeToTray;
    private bool _animatedBackground;
    private bool _hasChanges;
    private string? _savedMessage;

    internal SettingsViewModel(EngineSettings engine, DesktopSettings desktop, Func<Task>? applyToEngine)
    {
        _desktop = desktop;
        _applyToEngine = applyToEngine;
        _fps = FpsOptions[0];
        Load(engine);
        _closeToTray = desktop.CloseToTray;
        _animatedBackground = desktop.AnimatedBackground;
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => HasChanges);
        RevertCommand = new RelayCommand(() =>
        {
            Load(EngineSettings.Load(AppPaths.EngineSettingsFile));
            HasChanges = false;
        }, () => HasChanges);
    }

    public IReadOnlyList<FpsOption> FpsOptions { get; } =
    [
        new(0, "Match monitor"), new(30, "30 fps"), new(60, "60 fps"), new(120, "120 fps"),
        new(144, "144 fps"), new(165, "165 fps"), new(240, "240 fps"),
    ];

    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }

    public event Action<bool>? AnimatedBackgroundChanged;

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
    public string SaveHotkey { get => _saveHotkey; set => Change(ref _saveHotkey, value); }
    public bool RecordWithoutGame { get => _recordWithoutGame; set => Change(ref _recordWithoutGame, value); }

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

    public bool AnimatedBackground
    {
        get => _animatedBackground;
        set
        {
            if (!Set(ref _animatedBackground, value)) return;
            _desktop.AnimatedBackground = value;
            TrySaveDesktop();
            AnimatedBackgroundChanged?.Invoke(value);
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
        Codec = _codec,
        GameAudio = GameAudio,
        MicAudio = MicAudio,
        SaveHotkey = string.IsNullOrWhiteSpace(SaveHotkey) ? "F8" : SaveHotkey.Trim(),
        RecordWithoutGame = RecordWithoutGame,
    };

    private async Task SaveAsync()
    {
        try
        {
            ToEngineSettings().Save(AppPaths.EngineSettingsFile);
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
        _bitrate = Math.Clamp(s.BitrateMbps, 0, 300);
        _codec = s.Codec is "h264" or "hevc" ? s.Codec : "auto";
        _gameAudio = s.GameAudio;
        _micAudio = s.MicAudio;
        _saveHotkey = s.SaveHotkey;
        _recordWithoutGame = s.RecordWithoutGame;
        Raise(string.Empty);
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

    private static string FormatSeconds(double seconds) =>
        seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60:0} min" : seconds >= 60 ? $"{(int)seconds / 60}m {(int)seconds % 60}s" : $"{seconds:0} s";
}
