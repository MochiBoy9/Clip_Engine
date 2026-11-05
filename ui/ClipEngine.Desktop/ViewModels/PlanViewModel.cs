using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The plan card in Settings: Free, Pro and Ultra, the box a subscription's license key goes in, the colour themes a
// plan unlocks, the subscriber's name in the credits and the names of everyone who backs Clip Engine. A special key
// can add a role (Content Creator, Special, Exclusive), a place in the credits, and a direct line to the developer
// or bug reports that go straight to them.
public sealed partial class PlanViewModel : ObservableObject
{
    private readonly string? _file;
    private readonly DesktopSettings? _desktop;
    private readonly Func<string> _server;
    private readonly LicenseState _state;
    private readonly IReadOnlyList<string> _fileCredits;
    private readonly DispatcherTimer? _recheck;
    private Credits _credits = new([], []);
    private Plan _plan;
    private bool _active;
    private string _keyInput = "";
    private string _creditInput;
    private string? _message;
    private bool _messageIsError;
    private bool _busy;
    private string _contactInput = "";
    private string? _contactMessage;
    private bool _contactIsError;
    private bool _contactBusy;
    private IReadOnlyList<ContactMessageView> _contactMessages = [];

    // file is null in the preview tool and tests, which never save; server gives the online service's address.
    internal PlanViewModel(string? file, DesktopSettings? desktop, Func<string> server, IReadOnlyList<string>? credits = null)
    {
        _file = file;
        _desktop = desktop;
        _server = server;
        _state = file is null ? new LicenseState() : LicenseState.Load(file);
        _plan = LicenseRules.Effective(_state, DateTimeOffset.UtcNow);
        _active = LicenseRules.IsActive(_state, DateTimeOffset.UtcNow);
        _creditInput = _state.CreditName.Length > 0 ? _state.CreditName : desktop?.Squad.PlayerName ?? "";
        _fileCredits = credits ?? LoadCredits();
        Themes = AccentThemes.All.Append(AccentThemes.Custom(CustomAccent)).Select(t => new ThemeOption(t, Select)).ToList();
        LoadWheel();
        Offers = Plans.All.Where(p => p.Plan != Plan.Free).Select(p => new PlanOffer(p, this)).ToList();
        UpdateThemes();

        ActivateCommand = new RelayCommand(() => _ = ActivateAsync(), () => KeyInput.Trim().Length > 0 && !IsBusy);
        RemoveCommand = new RelayCommand(Remove, () => _state.HasKey);
        ManageCommand = new RelayCommand(() => Shell.Open(PlanConfig.ManageUrl), () => HasManage);
        SaveCreditCommand = new RelayCommand(() => _ = SaveCreditAsync(), () => CanBeCredited && !IsBusy);
        SendMessageCommand = new RelayCommand(() => _ = SendContactAsync(ContactClient.Message), () => CanSend(HasDirectLine));
        SendBugCommand = new RelayCommand(() => _ = SendContactAsync(ContactClient.Bug), () => CanSend(HasBugReports));

        if (file is not null)
        {
            _recheck = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            _recheck.Tick += (_, _) =>
            {
                _ = RecheckAsync();
                _ = LoadContactAsync();
            };
            _recheck.Start();
            _ = RecheckAsync();
            _ = LoadServerCreditsAsync();
            _ = LoadContactAsync();
        }
    }

    public RelayCommand ActivateCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ManageCommand { get; }
    public RelayCommand SaveCreditCommand { get; }
    public RelayCommand SendMessageCommand { get; }
    public RelayCommand SendBugCommand { get; }

    // The plan or a special key's features changed: themes, the recording resolution and the cloud library follow it.
    public event Action<Plan>? PlanChanged;

    public Plan Plan => _plan;
    internal PlanInfo Info => Plans.For(_plan);
    internal string? LicenseKey => _state.HasKey ? _state.Key : null;

    public bool IsPaid => _plan != Plan.Free;
    // People who paid are thanked in the credits; a special key's holder only when the developer let it in.
    public bool CanBeCredited => IsSpecial ? _state.CreditsAllowed : IsPaid;
    public bool IsUltra => _plan == Plan.Ultra;

    // A special key's title, such as Content Creator: the badge shows it in place of the plan.
    public string? RoleLabel => IsSpecial && _state.RoleLabel is { Length: > 0 } label ? label : null;
    public bool HasRole => RoleLabel is not null;
    public string Badge => (RoleLabel ?? _plan.ToString()).ToUpperInvariant();
    public bool ShowBadge => IsPaid || HasRole;

    // Why the developer gave the key, when they chose to share it.
    public string? Reason => IsSpecial && _state.Reason.Length > 0 ? $"Given for: {_state.Reason}" : null;
    public bool HasReason => Reason is not null;

    // Whether the key on this PC gives anything now, a plan or a special key's features.
    internal bool IsActive => _active;
    internal bool IsSpecial => _active && _state.IsSpecial;

    // The highest recording resolution: the plan's, or what a special key sets.
    internal int MaxLines => IsSpecial && _state.Lines is { } lines ? lines : Info.Lines;

    internal long ExtraStorage => IsSpecial ? _state.ExtraStorage : 0;

    // A special key with its own list of themes gives those (and the default); otherwise the plan decides.
    internal bool IsUnlocked(AccentTheme theme) =>
        IsSpecial && _state.Themes is { } themes ? theme.IsDefault || themes.Contains(theme.Id) : theme.IsUnlocked(_plan);

    // Ultra's colour wheel and adaptive themes, or a special key that lists them.
    public bool CanUseWheel => Allows(AccentThemes.WheelId);
    public bool CanUseAdaptive => Allows(AccentThemes.AdaptiveId);
    private bool Allows(string feature) => IsSpecial && _state.Themes is { } themes ? themes.Contains(feature) : _plan == Plan.Ultra;

    public IReadOnlyList<ThemeOption> Themes { get; }
    public IReadOnlyList<PlanOffer> Offers { get; }

    // The theme in use: the saved choice if the plan has it, otherwise the default.
    internal AccentTheme Theme
    {
        get
        {
            if (SavedThemeId == AccentThemes.WheelId) return CanUseWheel ? AccentThemes.Custom(CustomAccent) : AccentThemes.Default;
            var saved = AccentThemes.Find(SavedThemeId);
            return IsUnlocked(saved) ? saved : AccentThemes.Default;
        }
    }

    public string ThemeCaption => IsSpecial && _state.Themes is not null
        ? "Changes the accent colour across the app. Your special key unlocks the ones you can pick."
        : _plan switch
    {
        Plan.Ultra => "Changes the accent colour across the app. The last three and the colour wheel are Ultra's own.",
        Plan.Pro => "Changes the accent colour across the app. The last three and the colour wheel come with Ultra.",
        _ => "Pro unlocks the first thirteen. Ultra adds three more, a colour wheel for any colour and adaptive themes.",
    };

    public string Title => HasRole ? $"Clip Engine {RoleLabel}" : _plan switch
    {
        Plan.Ultra => "Clip Engine Ultra",
        Plan.Pro => "Clip Engine Pro",
        _ => "Clip Engine Free",
    };

    public string Detail => _plan switch
    {
        Plan.Free when IsSpecial => $"Your special key gives {SpecialText}{UntilText}{KeyText}.",
        _ when HasRole => $"{Info.Label} features: {Resolutions.Name(MaxLines)} recording, {StorageText} in the cloud{UntilText}{KeyText}.",
        Plan.Free => "Every recording feature is yours. Pro and Ultra add higher resolutions, more cloud storage and a few thank-yous, and keep Clip Engine going.",
        _ => $"{Resolutions.Name(MaxLines)} recording, {StorageText} in the cloud{UntilText}{KeyText}. Thank you for backing Clip Engine.",
    };

    private string KeyText => _state.Display.Length > 0 ? $", key {_state.Display}" : "";

    private string StorageText => Gb(Info.StorageGb * (1L << 30) + ExtraStorage);

    private static string Gb(long bytes) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB");

    // What a Free special key adds, in words.
    private string SpecialText
    {
        get
        {
            var parts = new List<string>();
            if (MaxLines != Info.Lines) parts.Add($"{Resolutions.Name(MaxLines)} recording");
            if (ExtraStorage > 0) parts.Add($"{Gb(ExtraStorage)} more cloud storage");
            if (_state.Themes is { Count: > 0 } themes && themes.Any(t => t != AccentThemes.Default.Id)) parts.Add("extra colour themes");
            return parts.Count switch
            {
                0 => "nothing extra right now",
                1 => parts[0],
                _ => string.Join(", ", parts[..^1]) + " and " + parts[^1],
            };
        }
    }

    // A special key's end date. Subscriptions renew, so theirs isn't shown.
    private string UntilText => IsSpecial && _state.Expires is { } end
        ? $", until {end.ToLocalTime():MMM d, yyyy}"
        : "";

    public string? StatusText
    {
        get
        {
            if (!_state.HasKey) return null;
            if (!_active) return "This key doesn't give a plan right now. Check it again, or remove it.";
            if (_state.LastOk is { } ok && DateTimeOffset.UtcNow - ok > TimeSpan.FromDays(1))
            {
                var left = LicenseRules.OfflineGrace - (DateTimeOffset.UtcNow - ok);
                return $"Couldn't check the key lately. The plan keeps working for {Math.Max(1, (int)left.TotalDays)} more days offline.";
            }
            return null;
        }
    }

    public bool HasStatus => StatusText is not null;
    public bool ShowKeyBox => !_state.HasKey || !_active;
    // The subscription portal; a special key has no subscription to manage.
    public bool HasManage => IsPaid && !IsSpecial && PlanConfig.ManageUrl.Length > 0;

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

    // Ultra first, then Pro and the names kept with the app, then the people thanked with a role.
    public IReadOnlyList<CreditName> Credits =>
        _credits.Ultra.Select(n => new CreditName(n, true))
            .Concat(_credits.Pro.Concat(_fileCredits).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(n => !_credits.Ultra.Contains(n, StringComparer.OrdinalIgnoreCase))
                        .Select(n => new CreditName(n, false)))
            .Concat(_credits.Exclusive.Select(n => new CreditName(n, false, "Exclusive")))
            .Concat(_credits.Creator.Select(n => new CreditName(n, false, "Content Creator")))
            .Concat(_credits.Special.Select(n => new CreditName(n, false, "Special")))
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

    // The preview tool: a special key's messages without the service.
    internal void ShowSampleMessages(IReadOnlyList<ContactMessage> messages) => ShowContact(messages);

    internal async Task ActivateAsync()
    {
        var key = KeyInput.Trim();
        if (key.Length == 0) return;
        IsBusy = true;
        Message = null;
        try
        {
            var check = await new LicenseClient(_server()).CheckAsync(key, CreditFor(), Device());
            if (check.Outcome != LicenseOutcome.Valid)
            {
                MessageIsError = true;
                Message = check.Message;
                return;
            }
            Accept(key, check);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // A key from somewhere other than the key box, like the month of Pro that inviting friends earns. True when it's on.
    internal async Task<bool> UseKeyAsync(string key)
    {
        KeyInput = key;
        await ActivateAsync();
        return _state.HasKey && _state.Key == key.Trim();
    }

    // A key the service said yes to goes on this PC.
    internal void Accept(string key, LicenseCheck check)
    {
        _state.Key = key;
        Apply(check);
        KeyInput = "";
        MessageIsError = false;
        Message = check.IsSpecial
            ? check.RoleLabel is { Length: > 0 } role ? $"Your {role} key is on. Welcome!"
            : check.Plan == Plan.Free ? "Your special key is on." : $"{Plans.For(check.Plan).Label} is on from your special key."
            : $"{Plans.For(check.Plan).Label} is on. Thank you!";
        SetPlan(check.Plan, force: true);
        _ = LoadContactAsync();
    }

    // Checks the key again twice a day: a renewed subscription carries on, a cancelled one goes back to Free.
    internal async Task RecheckAsync(bool force = false)
    {
        if (!_state.HasKey || (!force && !LicenseRules.NeedsCheck(_state, DateTimeOffset.UtcNow))) return;
        var check = await new LicenseClient(_server()).CheckAsync(_state.Key, CreditFor(), Device());
        _state.LastChecked = DateTimeOffset.UtcNow;
        if (check.Outcome == LicenseOutcome.Valid) Apply(check);
        else if (check.Outcome == LicenseOutcome.Rejected)
        {
            _state.Plan = Plan.Free;
            _state.LastOk = null;
            MessageIsError = true;
            Message = check.Message;
        }
        Save();
        SetPlan(LicenseRules.Effective(_state, DateTimeOffset.UtcNow), force: true);
    }

    private void Apply(LicenseCheck check)
    {
        _state.Plan = check.Plan;
        _state.Display = check.Display;
        _state.Expires = check.Expires;
        _state.IsSpecial = check.IsSpecial;
        _state.Lines = check.Lines;
        _state.Themes = check.Themes?.ToList();
        _state.ExtraStorage = check.ExtraStorage;
        _state.Role = check.Role;
        _state.RoleLabel = check.RoleLabel;
        _state.Perks = check.Perks?.ToList();
        _state.CreditsAllowed = check.CreditsAllowed;
        _state.Reason = check.Reason;
        _state.LastChecked = _state.LastOk = DateTimeOffset.UtcNow;
        Save();
    }

    internal void Remove()
    {
        _state.Clear();
        Save();
        Message = null;
        _contactMessages = [];
        ContactMessage = null;
        SetPlan(Plan.Free, force: true);
    }

    // ---- the direct line and bug reports ----

    public bool HasDirectLine => IsSpecial && _state.Perks?.Contains(PerkIds.DirectLine) == true;
    public bool HasBugReports => IsSpecial && _state.Perks?.Contains(PerkIds.BugReports) == true;
    public bool HasContact => HasDirectLine || HasBugReports;

    public string ContactTitle => HasDirectLine && HasBugReports ? "Talk to the developer"
        : HasDirectLine ? "Direct line to the developer" : "Report a bug";

    public string ContactCaption => HasDirectLine && HasBugReports
        ? "Your key comes with a direct line. Send a message or a bug report and it goes straight to the developer, who answers here."
        : HasDirectLine
            ? "Your key comes with a direct line. Your message goes straight to the developer, who answers here."
            : "Your key sends bug reports straight to the developer. Say what happened and what you expected; answers show up here.";

    public string ContactInput
    {
        get => _contactInput;
        set
        {
            if (!Set(ref _contactInput, value ?? "")) return;
            SendMessageCommand.NotifyCanExecuteChanged();
            SendBugCommand.NotifyCanExecuteChanged();
        }
    }

    public string? ContactMessage { get => _contactMessage; private set { if (Set(ref _contactMessage, value)) Raise(nameof(HasContactMessage)); } }
    public bool HasContactMessage => _contactMessage is not null;
    public bool ContactIsError { get => _contactIsError; private set => Set(ref _contactIsError, value); }

    public IReadOnlyList<ContactMessageView> ContactMessages => _contactMessages;
    public bool HasContactMessages => _contactMessages.Count > 0;

    private bool CanSend(bool perk) => perk && !_contactBusy && ContactInput.Trim().Length > 0;

    internal async Task SendContactAsync(string kind)
    {
        var text = ContactInput.Trim();
        if (text.Length == 0 || !_state.HasKey) return;
        SetContactBusy(true);
        try
        {
            var name = _state.CreditName.Length > 0 ? _state.CreditName : _desktop?.Squad.PlayerName ?? "";
            var result = await new ContactClient(_server()).SendAsync(_state.Key, Device(), kind, text, name);
            ContactIsError = !result.Ok;
            if (result.Ok)
            {
                ContactInput = "";
                ContactMessage = kind == ContactClient.Bug ? "Bug report sent. Thank you!" : "Sent. The developer's answer shows up here.";
                ShowContact(result.Messages);
            }
            else ContactMessage = result.Message;
        }
        finally
        {
            SetContactBusy(false);
        }
    }

    // The messages sent from this key, with any answers. Checked when the app starts and the key changes.
    internal async Task LoadContactAsync()
    {
        if (!HasContact || !_state.HasKey) return;
        var result = await new ContactClient(_server()).ListAsync(_state.Key, Device());
        if (result.Ok) ShowContact(result.Messages);
    }

    private void ShowContact(IReadOnlyList<ContactMessage> messages)
    {
        _contactMessages = messages.Select(m => new ContactMessageView(m)).ToList();
        Raise(nameof(ContactMessages));
        Raise(nameof(HasContactMessages));
    }

    private void SetContactBusy(bool busy)
    {
        _contactBusy = busy;
        SendMessageCommand.NotifyCanExecuteChanged();
        SendBugCommand.NotifyCanExecuteChanged();
    }

    // This PC's id, which a special key counts against its limit of PCs.
    private string Device()
    {
        if (_desktop is null) return "";
        var had = _desktop.DeviceId.Length >= 8;
        var id = Online.DeviceId(_desktop);
        if (!had)
        {
            try
            {
                _desktop.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return id;
    }

    private string? CreditFor() => CanBeCredited && _state.InCredits && CreditInput.Trim().Length > 0 ? CreditInput.Trim() : "";

    private async Task SaveCreditAsync()
    {
        if (!_state.HasKey || !CanBeCredited) return;
        _state.CreditName = CreditInput.Trim();
        Save();
        IsBusy = true;
        try
        {
            var check = await new LicenseClient(_server()).CheckAsync(_state.Key, CreditFor(), Device());
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

    // force: the key was checked or changed, so its features may have too even when the plan is the same.
    private void SetPlan(Plan plan, bool force = false)
    {
        var changed = plan != _plan || force;
        _plan = plan;
        _active = LicenseRules.IsActive(_state, DateTimeOffset.UtcNow);
        Raise(string.Empty);
        RemoveCommand.NotifyCanExecuteChanged();
        ManageCommand.NotifyCanExecuteChanged();
        SaveCreditCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        SendBugCommand.NotifyCanExecuteChanged();
        foreach (var offer in Offers) offer.Refresh();
        UpdateThemes();
        RaiseLook();
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
        var allowed = theme.Id == AccentThemes.WheelId ? CanUseWheel : IsUnlocked(theme);
        if (!allowed) return;
        SavedThemeId = theme.Id;
        SaveDesktop();
        UpdateThemes();
        RaiseLook();
    }

    private void UpdateThemes()
    {
        var current = Theme;
        foreach (var option in Themes)
            option.Update(option.Theme.Id == current.Id, option.Theme.Id == AccentThemes.WheelId ? CanUseWheel : IsUnlocked(option.Theme));
        Raise(nameof(ThemeCaption));
        RaiseLookProperties();
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

// Tag: the role someone was thanked with (Content Creator, Special, Exclusive), or null for a subscriber.
public sealed record CreditName(string Name, bool Ultra, string? Tag = null)
{
    public bool HasTag => Tag is not null;
}

// One message or bug report sent to the developer, as the plan card lists it.
public sealed class ContactMessageView
{
    internal ContactMessageView(ContactMessage message)
    {
        Kind = message.Kind == ContactClient.Bug ? "BUG REPORT" : "MESSAGE";
        Text = message.Text;
        Sent = message.Sent.ToLocalTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        Reply = message.Reply;
    }

    public string Kind { get; }
    public string Text { get; }
    public string Sent { get; }
    public string? Reply { get; }
    public bool HasReply => Reply is not null;
    public string Status => HasReply ? "Answered" : "Waiting for an answer";
}

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
    public string Name => Theme.Id == AccentThemes.WheelId ? "Colour wheel (Ultra)"
        : Theme.Needs == Plan.Ultra ? $"{Theme.Name} (Ultra)" : Theme.Name;
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
