using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The plan card in Settings: which plan this PC is on, and entering or removing a license key.
public sealed class LicenseViewModel : ObservableObject, IDisposable
{
    private readonly LicenseState _state;
    private readonly string? _file;
    private readonly PolarLicenseClient? _client;
    private readonly Func<DateTimeOffset> _now;
    private DispatcherTimer? _recheck;
    private string _keyInput = "";
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;

    // file and client are null in the preview tool, which never saves or goes online.
    internal LicenseViewModel(LicenseState state, string? file, PolarLicenseClient? client, Func<DateTimeOffset>? now = null,
                              bool? purchasesOpen = null)
    {
        PurchasesOpen = purchasesOpen ?? LicenseConfig.IsConfigured;
        _state = state;
        _file = file;
        _client = client;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        ActivateCommand = new RelayCommand(() => _ = ActivateAsync(), () => CanActivate);
        DeactivateCommand = new RelayCommand(() => _ = DeactivateAsync(), () => HasKey && !IsBusy);
        GetProCommand = new RelayCommand(() => Shell.Open(LicenseConfig.PricingUrl));
        ManageCommand = new RelayCommand(() => Shell.Open(LicenseConfig.ManageUrl));
    }

    // Checks the saved key now and every few hours, so a cancelled subscription ends and a renewal is picked up.
    internal void StartRechecking()
    {
        if (_recheck is not null) return;
        _recheck = new DispatcherTimer { Interval = LicenseRules.RecheckEvery };
        _recheck.Tick += (_, _) => _ = RecheckAsync();
        _recheck.Start();
        _ = RecheckAsync();
    }

    public RelayCommand ActivateCommand { get; }
    public RelayCommand DeactivateCommand { get; }
    public RelayCommand GetProCommand { get; }
    public RelayCommand ManageCommand { get; }

    public event Action<Plan>? PlanChanged;

    public Plan Plan => LicenseRules.Effective(_state, _now());
    public bool IsPaid => Plan != Plan.Free;
    public bool IsFree => !IsPaid;
    public bool HasKey => _state.HasKey;
    public bool HasNoKey => !HasKey;
    public bool PurchasesOpen { get; }

    public string PlanName => Plan switch
    {
        Plan.Founder => "Founder",
        Plan.Pro => "Pro",
        _ => "Free",
    };

    public string PlanDetail
    {
        get
        {
            if (!PurchasesOpen && !HasKey) return "Clip Engine is free while it's in early access. Pro and Founder licenses are coming soon.";
            if (HasKey && Plan == Plan.Free)
            {
                return _state.Plan != Plan.Free && _state.LastChecked is not null
                    ? "Your license hasn't been checked in a while. Connect to the internet to keep it active."
                    : "This key isn't active right now.";
            }
            return Plan switch
            {
                Plan.Founder => $"Founder license for life · {_state.DisplayKey}",
                Plan.Pro when _state.ExpiresAt is { } expires => $"Pro until {expires.LocalDateTime:d MMM yyyy} · {_state.DisplayKey}",
                Plan.Pro => $"Pro subscription · {_state.DisplayKey}",
                _ => "Everything you need to record and clip. A Founder license unlocks the Pro features that run on your PC, for life, with one payment.",
            };
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

    public bool CanActivate => PurchasesOpen && !IsBusy && KeyInput.Trim().Length > 0;

    public string? Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message is not null;
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ActivateCommand.NotifyCanExecuteChanged();
            DeactivateCommand.NotifyCanExecuteChanged();
        }
    }

    internal async Task ActivateAsync()
    {
        if (_client is null || !CanActivate) return;
        var key = KeyInput.Trim();
        IsBusy = true;
        Show("Checking your key…", error: false);
        var check = await _client.ActivateAsync(key, Environment.MachineName);
        IsBusy = false;

        if (check.Outcome != LicenseOutcome.Valid)
        {
            Show(check.Message, error: true);
            return;
        }
        _state.Key = key;
        Apply(check);
        KeyInput = "";
        Show($"Welcome to Clip Engine {PlanName}. Thanks for supporting it!", error: false);
    }

    internal async Task DeactivateAsync()
    {
        if (!HasKey) return;
        IsBusy = true;
        if (_client is not null && _state.ActivationId.Length > 0)
        {
            await _client.DeactivateAsync(_state.Key, _state.ActivationId);
        }
        IsBusy = false;
        _state.Clear();
        Save();
        Changed();
        Show("License removed from this PC. You can use it on another one now.", error: false);
    }

    internal async Task RecheckAsync()
    {
        if (_client is null || !HasKey || IsBusy) return;
        var check = await _client.ValidateAsync(_state.Key, _state.ActivationId.Length > 0 ? _state.ActivationId : null);
        switch (check.Outcome)
        {
            case LicenseOutcome.Valid:
                Apply(check);
                break;
            case LicenseOutcome.Rejected:
                // Keep the key so the person sees what happened, but drop the paid plan.
                _state.Plan = Plan.Free;
                _state.LastChecked = _now();
                Save();
                Changed();
                Show(check.Message, error: true);
                break;
            case LicenseOutcome.Offline:
                // The plan keeps working through the offline grace period.
                break;
        }
    }

    private void Apply(LicenseCheck check)
    {
        var info = check.Info!;
        _state.Plan = check.Plan;
        _state.DisplayKey = info.DisplayKey;
        _state.ActivationId = check.ActivationId ?? "";
        _state.ExpiresAt = info.ExpiresAt;
        _state.LastChecked = _now();
        Save();
        Changed();
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

    private void Changed()
    {
        Raise(string.Empty);
        ActivateCommand.NotifyCanExecuteChanged();
        DeactivateCommand.NotifyCanExecuteChanged();
        PlanChanged?.Invoke(Plan);
    }

    private void Show(string text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }

    public void Dispose() => _recheck?.Stop();
}
