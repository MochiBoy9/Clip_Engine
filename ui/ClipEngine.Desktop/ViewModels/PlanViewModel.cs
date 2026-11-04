using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The plan card in Settings: Free, Pro and Ultra, the box a subscription's license key goes in, the colour themes a
// plan unlocks, the subscriber's name in the credits and the names of everyone who backs Clip Engine.
public sealed class PlanViewModel : ObservableObject
{
    private readonly string? _file;
    private readonly DesktopSettings? _desktop;
    private readonly Func<string> _server;
    private readonly LicenseState _state;
    private readonly IReadOnlyList<string> _fileCredits;
    private readonly DispatcherTimer? _recheck;
    private Credits _credits = new([], []);
    private Plan _plan;
    private string _keyInput = "";
    private string _creditInput;
    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    // file is null in the preview tool and tests, which never save; server gives the online service's address.
    internal PlanViewModel(string? file, DesktopSettings? desktop, Func<string> server, IReadOnlyList<string>? credits = null)
    {
        _file = file;
        _desktop = desktop;
        _server = server;
        _state = file is null ? new LicenseState() : LicenseState.Load(file);
        _plan = LicenseRules.Effective(_state, DateTimeOffset.UtcNow);
        _creditInput = _state.CreditName.Length > 0 ? _state.CreditName : desktop?.Squad.PlayerName ?? "";
        _fileCredits = credits ?? LoadCredits();
        Themes = AccentThemes.All.Select(t => new ThemeOption(t, Select)).ToList();
        Offers = Plans.All.Where(p => p.Plan != Plan.Free).Select(p => new PlanOffer(p, this)).ToList();
        UpdateThemes();

        ActivateCommand = new RelayCommand(() => _ = ActivateAsync(), () => KeyInput.Trim().Length > 0 && !IsBusy);
        RemoveCommand = new RelayCommand(Remove, () => _state.HasKey);
        ManageCommand = new RelayCommand(() => Shell.Open(PlanConfig.ManageUrl), () => HasManage);
        SaveCreditCommand = new RelayCommand(() => _ = SaveCreditAsync(), () => IsPaid && !IsBusy);

        if (file is not null)
        {
            _recheck = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            _recheck.Tick += (_, _) => _ = RecheckAsync();
            _recheck.Start();
            _ = RecheckAsync();
            _ = LoadServerCreditsAsync();
        }
    }

    public RelayCommand ActivateCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ManageCommand { get; }
    public RelayCommand SaveCreditCommand { get; }

    // The plan changed: themes, the recording resolution and the cloud library follow it.
    public event Action<Plan>? PlanChanged;
    internal event Action<AccentTheme>? ThemeChanged;

    public Plan Plan => _plan;
    internal PlanInfo Info => Plans.For(_plan);
    internal string? LicenseKey => _state.HasKey ? _state.Key : null;

    public bool IsPaid => _plan != Plan.Free;
    public bool IsUltra => _plan == Plan.Ultra;
    public string Badge => _plan.ToString().ToUpperInvariant();

    public IReadOnlyList<ThemeOption> Themes { get; }
    public IReadOnlyList<PlanOffer> Offers { get; }

    // The theme in use: the saved choice if the plan has it, otherwise the default.
    internal AccentTheme Theme
    {
        get
        {
            var saved = AccentThemes.Find(_desktop?.AccentTheme);
            return saved.IsUnlocked(_plan) ? saved : AccentThemes.Default;
        }
    }

    public string ThemeCaption => _plan switch
    {
        Plan.Ultra => "Changes the accent colour across the app. The last three are Ultra's own.",
        Plan.Pro => "Changes the accent colour across the app. The last three come with Ultra.",
        _ => "Pro unlocks the first seven, and Ultra all ten.",
    };

    public string Title => _plan switch
    {
        Plan.Ultra => "Clip Engine Ultra",
        Plan.Pro => "Clip Engine Pro",
        _ => "Clip Engine Free",
    };

    public string Detail => _plan switch
    {
        Plan.Free => "Every recording feature is yours. Pro and Ultra add higher resolutions, more cloud storage and a few thank-yous, and keep Clip Engine going.",
        _ => $"{Info.LinesText} recording, {Info.StorageGb} GB in the cloud{KeyText}. Thank you for backing Clip Engine.",
    };

    private string KeyText => _state.Display.Length > 0 ? $", key {_state.Display}" : "";

    public string? StatusText
    {
        get
        {
            if (!_state.HasKey) return null;
            if (_plan == Plan.Free) return "This key doesn't give a plan right now. Check it again, or remove it.";
            if (_state.LastOk is { } ok && DateTimeOffset.UtcNow - ok > TimeSpan.FromDays(1))
            {
                var left = LicenseRules.OfflineGrace - (DateTimeOffset.UtcNow - ok);
                return $"Couldn't check the key lately. The plan keeps working for {Math.Max(1, (int)left.TotalDays)} more days offline.";
            }
            return null;
        }
    }

    public bool HasStatus => StatusText is not null;
    public bool ShowKeyBox => !_state.HasKey || _plan == Plan.Free;
    public bool HasManage => IsPaid && PlanConfig.ManageUrl.Length > 0;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            ActivateCommand.NotifyCanExecuteChanged();
            SaveCreditCommand.NotifyCanExecuteChanged();
        }
    }

    public string KeyInput
    {
        get => _keyInput;
        set
        {
            if (Set(ref _keyInput, value ?? "")) ActivateCommand.NotifyCanExecuteChanged();
        }
    }

    public string CreditInput { get => _creditInput; set => Set(ref _creditInput, value ?? ""); }

    public bool InCredits
    {
        get => _state.InCredits;
        set
        {
            if (_state.InCredits == value) return;
            _state.InCredits = value;
            Save();
            Raise();
            _ = SaveCreditAsync();
        }
    }

    public string CreditCaption => _state.InCredits && _state.CreditName.Length > 0
        ? $"Listed as {_state.CreditName}."
        : "Your name in the thank-you list, in the app and on the website.";

    public string? Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message is not null;
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    // Ultra first, then Pro, then the names kept with the app.
    public IReadOnlyList<CreditName> Credits =>
        _credits.Ultra.Select(n => new CreditName(n, true))
            .Concat(_credits.Pro.Concat(_fileCredits).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(n => !_credits.Ultra.Contains(n, StringComparer.OrdinalIgnoreCase))
                        .Select(n => new CreditName(n, false)))
            .ToList();

    public bool HasCredits => Credits.Count > 0;

    // The preview tool: a plan without a key or the service.
    internal void ShowSample(Plan plan, Credits credits)
    {
        _state.Key = "preview";
        _state.Display = "****-7Q4K";
        _state.Plan = plan;
        _state.LastOk = _state.LastChecked = DateTimeOffset.UtcNow;
        _state.CreditName = _creditInput = "Enterro";
        _credits = credits;
        SetPlan(LicenseRules.Effective(_state, DateTimeOffset.UtcNow));
    }

    internal async Task ActivateAsync()
    {
        var key = KeyInput.Trim();
        if (key.Length == 0) return;
        IsBusy = true;
        Message = null;
        try
        {
            var check = await new LicenseClient(_server()).CheckAsync(key, CreditFor());
            if (check.Outcome != LicenseOutcome.Valid)
            {
                MessageIsError = true;
                Message = check.Message;
                return;
            }
            _state.Key = key;
            Apply(check);
            KeyInput = "";
            MessageIsError = false;
            Message = $"{Plans.For(check.Plan).Label} is on. Thank you!";
            SetPlan(check.Plan);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Checks the key again twice a day: a renewed subscription carries on, a cancelled one goes back to Free.
    internal async Task RecheckAsync(bool force = false)
    {
        if (!_state.HasKey || (!force && !LicenseRules.NeedsCheck(_state, DateTimeOffset.UtcNow))) return;
        var check = await new LicenseClient(_server()).CheckAsync(_state.Key, CreditFor());
        _state.LastChecked = DateTimeOffset.UtcNow;
        if (check.Outcome == LicenseOutcome.Valid) Apply(check);
        else if (check.Outcome == LicenseOutcome.Rejected)
        {
            _state.Plan = Plan.Free;
            MessageIsError = true;
            Message = check.Message;
        }
        Save();
        SetPlan(LicenseRules.Effective(_state, DateTimeOffset.UtcNow));
    }

    private void Apply(LicenseCheck check)
    {
        _state.Plan = check.Plan;
        _state.Display = check.Display;
        _state.Expires = check.Expires;
        _state.LastChecked = _state.LastOk = DateTimeOffset.UtcNow;
        Save();
    }

    internal void Remove()
    {
        _state.Clear();
        Save();
        Message = null;
        SetPlan(Plan.Free);
    }

    private string? CreditFor() => _state.InCredits && CreditInput.Trim().Length > 0 ? CreditInput.Trim() : "";

    private async Task SaveCreditAsync()
    {
        if (!_state.HasKey || !IsPaid) return;
        _state.CreditName = CreditInput.Trim();
        Save();
        IsBusy = true;
        try
        {
            var check = await new LicenseClient(_server()).CheckAsync(_state.Key, CreditFor());
            MessageIsError = check.Outcome != LicenseOutcome.Valid;
            Message = check.Outcome == LicenseOutcome.Valid
                ? (CreditFor() is { Length: > 0 } name ? $"You're in the credits as {name}." : "Your name is out of the credits.")
                : check.Message;
            await LoadServerCreditsAsync();
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(CreditCaption));
        }
    }

    private async Task LoadServerCreditsAsync()
    {
        var server = _server();
        if (server.Length == 0) return;
        _credits = await new LicenseClient(server).CreditsAsync();
        Raise(nameof(Credits));
        Raise(nameof(HasCredits));
    }

    private void SetPlan(Plan plan)
    {
        var changed = plan != _plan;
        _plan = plan;
        Raise(string.Empty);
        RemoveCommand.NotifyCanExecuteChanged();
        ManageCommand.NotifyCanExecuteChanged();
        SaveCreditCommand.NotifyCanExecuteChanged();
        foreach (var offer in Offers) offer.Refresh();
        UpdateThemes();
        ThemeChanged?.Invoke(Theme);
        if (changed) PlanChanged?.Invoke(plan);
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            _state.Save(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Select(AccentTheme theme)
    {
        if (!theme.IsUnlocked(_plan) || _desktop is null) return;
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
        foreach (var option in Themes) option.Update(option.Theme == current, option.Theme.IsUnlocked(_plan));
        Raise(nameof(ThemeCaption));
    }

    // Names from Assets/Supporters.txt, for people thanked before subscriptions.
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

public sealed record CreditName(string Name, bool Ultra);

// Pro or Ultra as the plan card offers it: price, what it adds and a button to subscribe.
public sealed class PlanOffer : ObservableObject
{
    private readonly PlanViewModel _owner;

    internal PlanOffer(PlanInfo info, PlanViewModel owner)
    {
        Info = info;
        _owner = owner;
        SubscribeCommand = new RelayCommand(() => Shell.Open(PlanConfig.CheckoutUrl(info.Plan)), () => CanSubscribe);
    }

    internal PlanInfo Info { get; }
    public RelayCommand SubscribeCommand { get; }
    public string Label => Info.Label;
    public string PriceText => Info.PriceText;
    public IReadOnlyList<string> Perks => Info.Perks;
    public bool IsCurrent => _owner.Plan == Info.Plan;
    public bool IsUltra => Info.Plan == Plan.Ultra;
    public bool CanSubscribe => !IsCurrent && PlanConfig.CheckoutUrl(Info.Plan).Length > 0;

    public string ButtonText => IsCurrent ? "Your plan"
        : PlanConfig.CheckoutUrl(Info.Plan).Length == 0 ? "Coming soon"
        : _owner.Plan > Info.Plan ? "Switch" : "Subscribe";

    internal void Refresh()
    {
        Raise(string.Empty);
        SubscribeCommand.NotifyCanExecuteChanged();
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
    public string Name => Theme.Needs == Plan.Ultra ? $"{Theme.Name} (Ultra)" : Theme.Name;
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
