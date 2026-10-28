using Avalonia.Media;
using Avalonia.Platform;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The support card in Settings: a link to Patreon, the box a supporter key is pasted into, the colour themes a key
// unlocks and the names of the people who back Clip Engine.
public sealed class SupporterViewModel : ObservableObject
{
    private readonly string _publicKey;
    private readonly string? _file;
    private readonly DesktopSettings? _desktop;
    private Supporter? _supporter;
    private string _keyInput = "";
    private string? _message;
    private bool _messageIsError;

    // file is null in the preview tool, which never saves.
    internal SupporterViewModel(string publicKey, string? file, DesktopSettings? desktop = null, string? patreonUrl = null,
                                IReadOnlyList<string>? credits = null)
    {
        _publicKey = publicKey;
        _file = file;
        _desktop = desktop;
        PatreonUrl = patreonUrl ?? SupporterConfig.PatreonUrl;
        Credits = credits ?? LoadCredits();
        if (file is not null && SupporterKeyFile.Load(file) is { } saved) _supporter = SupporterKey.Verify(saved, publicKey);
        Themes = AccentThemes.All.Select(t => new ThemeOption(t, Select)).ToList();
        UpdateThemes();

        ActivateCommand = new RelayCommand(Activate, () => KeyInput.Trim().Length > 0);
        RemoveCommand = new RelayCommand(Remove, () => IsSupporter);
        OpenPatreonCommand = new RelayCommand(() => Shell.Open(PatreonUrl), () => HasPatreon);
    }

    public RelayCommand ActivateCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenPatreonCommand { get; }

    public event Action? SupporterChanged;
    internal event Action<AccentTheme>? ThemeChanged;

    public IReadOnlyList<ThemeOption> Themes { get; }
    public IReadOnlyList<string> Credits { get; }
    public bool HasCredits => Credits.Count > 0;

    // The theme in use: the saved choice for supporters, the default for everyone else.
    internal AccentTheme Theme => IsSupporter ? AccentThemes.Find(_desktop?.AccentTheme) : AccentThemes.Default;

    public string ThemeCaption => IsSupporter ? "Changes the accent colour across the app." : "Unlocked with a supporter key.";

    public string PatreonUrl { get; }
    public bool HasPatreon => PatreonUrl.Length > 0;
    public bool KeysEnabled => _publicKey.Length > 0;

    public bool IsSupporter => _supporter is not null;
    public bool ShowKeyBox => KeysEnabled && !IsSupporter;
    public string? SupporterName => _supporter?.Name;

    public string Title => IsSupporter ? $"Thank you, {_supporter!.Name}" : "Support Clip Engine";

    public string Detail => IsSupporter
        ? $"Supporter since {_supporter!.Since:MMMM yyyy}. You're helping keep Clip Engine free for everyone."
        : "Clip Engine is free and made for the love of it. If it's earned a spot on your PC, backing it on Patreon helps it keep getting better.";

    public string KeyInput
    {
        get => _keyInput;
        set
        {
            if (Set(ref _keyInput, value ?? "")) ActivateCommand.NotifyCanExecuteChanged();
        }
    }

    public string? Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message is not null;
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    internal void Activate()
    {
        var key = KeyInput.Trim();
        if (SupporterKey.Verify(key, _publicKey) is not { } supporter)
        {
            MessageIsError = true;
            Message = "That key doesn't look right. Copy the whole key from your Patreon message, starting with CE1-.";
            return;
        }
        _supporter = supporter;
        Save(key);
        KeyInput = "";
        MessageIsError = false;
        Message = null;
        Changed();
    }

    internal void Remove()
    {
        _supporter = null;
        Save(null);
        Changed();
    }

    private void Save(string? key)
    {
        if (_file is null) return;
        try
        {
            SupporterKeyFile.Save(_file, key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Select(AccentTheme theme)
    {
        if (!IsSupporter || _desktop is null) return;
        _desktop.AccentTheme = theme.Id;
        try
        {
            _desktop.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        UpdateThemes();
        ThemeChanged?.Invoke(Theme);
    }

    private void UpdateThemes()
    {
        var current = Theme;
        foreach (var option in Themes) option.Update(option.Theme == current, IsSupporter || option.Theme.IsDefault);
        Raise(nameof(ThemeCaption));
    }

    private void Changed()
    {
        Raise(string.Empty);
        RemoveCommand.NotifyCanExecuteChanged();
        UpdateThemes();
        SupporterChanged?.Invoke();
        ThemeChanged?.Invoke(Theme);
    }

    // Names from Assets/Supporters.txt, which is updated by hand before each release.
    private static IReadOnlyList<string> LoadCredits()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://ClipEngine/Assets/Supporters.txt"));
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or FileNotFoundException)
        {
            return [];
        }
    }
}

// One colour swatch in the theme picker.
public sealed class ThemeOption : ObservableObject
{
    private readonly Action<AccentTheme> _select;
    private bool _isSelected;
    private bool _isAvailable;

    internal ThemeOption(AccentTheme theme, Action<AccentTheme> select)
    {
        Theme = theme;
        _select = select;
        Preview = AccentThemes.Preview(theme);
    }

    internal AccentTheme Theme { get; }
    public string Name => Theme.Name;
    public IBrush Preview { get; }
    public bool IsAvailable { get => _isAvailable; private set => Set(ref _isAvailable, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !_isSelected && IsAvailable) _select(Theme);
            else Raise();
        }
    }

    internal void Update(bool selected, bool available)
    {
        IsAvailable = available;
        _isSelected = selected;
        Raise(nameof(IsSelected));
    }
}
