using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// Inviting friends, on the Friends page: your invite link, how many friends have installed Clip Engine from it, and the
// month of Pro that enough of them earn (server/src/referrals.js). It also tells the service when Clip Engine starts on
// a PC for the first time, which is what counts a friend, and has a box for a friend's invite code on a new install.
public sealed class InviteViewModel : ObservableObject
{
    private static readonly TimeSpan LookEvery = TimeSpan.FromHours(3);
    private static readonly TimeSpan CodeBoxFor = TimeSpan.FromDays(7);

    private readonly DesktopSettings _desktop;
    private readonly Func<string> _server;
    private readonly PlanViewModel? _plan;
    private readonly Func<string, string, Task>? _copy;
    private readonly Action<string>? _notify;
    private readonly Action? _save;
    private readonly DispatcherTimer? _timer;
    private readonly HttpClient? _http;
    private ReferralStatus? _status;
    private string _codeInput = "";
    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    // freshInstall: nothing of Clip Engine was on this PC when it started (MainViewModel). copy puts text on the
    // clipboard with a toast, notify adds a line to the activity feed, save writes desktop.json. Live only when save
    // is given; the preview tool and tests leave the service alone.
    internal InviteViewModel(DesktopSettings desktop, Func<string> server, PlanViewModel? plan, bool? freshInstall,
                             Func<string, string, Task>? copy = null, Action<string>? notify = null, Action? save = null,
                             HttpClient? http = null)
    {
        _http = http;
        _desktop = desktop;
        _server = server;
        _plan = plan;
        _copy = copy;
        _notify = notify;
        _save = save;
        var state = desktop.Referral;
        if (state.FirstStart is null && freshInstall is { } fresh)
        {
            state.FirstStart = DateTimeOffset.UtcNow;
            state.FreshInstall = fresh;
            Save();
        }

        GetLinkCommand = new RelayCommand(() => _ = GetLinkAsync(), () => !IsBusy);
        CopyLinkCommand = new RelayCommand(() => _ = CopyAsync(Link, "Invite link copied"), () => HasLink);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => HasLink && !IsBusy);
        AddCodeCommand = new RelayCommand(() => _ = AddCodeAsync(), () => CodeInput.Trim().Length > 0 && !IsBusy);
        UseRewardCommand = new RelayCommand(p => { if (p is string key) _ = UseRewardAsync(key); });
        CopyRewardCommand = new RelayCommand(p => { if (p is string key) _ = CopyAsync(key, "Pro key copied"); });

        if (save is null) return;
        _timer = new DispatcherTimer { Interval = LookEvery };
        _timer.Tick += (_, _) => _ = RefreshAsync(quiet: true);
        _timer.Start();
        _ = StartAsync();
    }

    public RelayCommand GetLinkCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddCodeCommand { get; }
    public RelayCommand UseRewardCommand { get; }
    public RelayCommand CopyRewardCommand { get; }

    private ReferralSettings State => _desktop.Referral;

    public bool HasLink => State.Code.Length > 0;
    public bool NoLink => !HasLink;
    public string Link => State.Link.Length > 0 ? State.Link : State.Code;
    public string Code => State.Code;
    public string CodeText => $"Your invite code is {State.Code}, if a friend would rather type it in the app.";

    public int Friends => _status?.Friends ?? State.Friends;
    public int Needed => _status?.Needed ?? 3;
    public int RewardDays => _status?.RewardDays ?? 30;

    // The bar toward the next reward.
    public double Progress => _status is null ? 0 : _status.Done ? 1 : Math.Clamp((Needed - _status.ToGo) / (double)Needed, 0, 1);

    public string Headline => $"Invite {Needed} friends, get {MonthText} of Pro";

    private string MonthText => RewardDays is 30 or 31 ? "a month" : $"{RewardDays} days";

    public string Caption =>
        $"Send your link to friends. When {Needed} of them install Clip Engine on their own PC and start it, you get {MonthText} of Pro free. " +
        "Each friend needs their own PC and their own internet connection, so a second PC at home doesn't count.";

    public string ProgressText
    {
        get
        {
            if (_status is null) return Friends == 1 ? "1 friend so far" : $"{Friends} friends so far";
            if (_status.Done) return _status.Rewards.Count > 0 ? "You earned your free Pro. Thanks for spreading the word!" : "All rewards earned.";
            var have = Needed - _status.ToGo;
            var line = $"{have} of {Needed} friends";
            if (_status.NotCounted > 0) line += $" · {_status.NotCounted} didn't count";
            return line;
        }
    }

    public ObservableCollection<string> Rewards { get; } = [];
    public bool HasRewards => Rewards.Count > 0;

    // A friend's code goes in for a week after installing, if this install hasn't counted for anyone yet.
    public bool ShowCodeBox => State.FreshInstall && State.InvitedBy.Length == 0 && State.InviteCode.Length == 0
                               && State.FirstStart is { } first && DateTimeOffset.UtcNow - first < CodeBoxFor;

    public string InvitedText => State.InviteCode.Length == 0 ? ""
        : State.InvitedBy.Length > 0 ? $"You joined from {State.InvitedBy}'s invite." : "You joined from a friend's invite.";
    public bool HasInvitedText => InvitedText.Length > 0;

    public string CodeInput
    {
        get => _codeInput;
        set
        {
            if (Set(ref _codeInput, value ?? "")) AddCodeCommand.NotifyCanExecuteChanged();
        }
    }

    public string? Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message is not null;
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            GetLinkCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
            AddCodeCommand.NotifyCanExecuteChanged();
        }
    }

    private string Pc => MachineId.Get() is { Length: > 0 } id ? id : Device;
    private string Device => Online.DeviceId(_desktop);

    private ReferralClient Client() => new(_server(), _http);

    // The first start on this PC is announced once, so a friend's link opened before installing is credited; then
    // the invite link's friends are looked at.
    private async Task StartAsync()
    {
        if (!State.Announced)
        {
            var answer = await Client().ActivateAsync(Pc, Device, State.FreshInstall, null);
            if (answer.Ok)
            {
                State.Announced = true;
                TakeActivation(answer, typed: false);
                Save();
            }
        }
        if (HasLink) await RefreshAsync(quiet: true);
    }

    internal async Task GetLinkAsync()
    {
        IsBusy = true;
        try
        {
            var (status, error) = await Client().CreateAsync(Pc, Device, _desktop.Squad.PlayerName.Trim());
            if (status is null)
            {
                Show(error, true);
                return;
            }
            State.Code = status.Code;
            State.Link = status.Link;
            if (status.Secret.Length > 0) State.Secret = Online.Protect(status.Secret);
            Save();
            await TakeStatusAsync(status);
            Show(null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task RefreshAsync(bool quiet = false)
    {
        if (!HasLink) return;
        var secret = Online.Unprotect(State.Secret) ?? "";
        var (status, error) = await Client().StatusAsync(State.Code, secret, Pc, Device);
        if (status is null)
        {
            // The secret was lost (another Windows account, a copied desktop.json): asking again from this PC gives it back.
            if (error.Contains("isn't yours", StringComparison.Ordinal))
            {
                await GetLinkAsync();
                return;
            }
            if (!quiet) Show(error, true);
            return;
        }
        await TakeStatusAsync(status);
        if (!quiet) Show(null);
    }

    internal async Task AddCodeAsync()
    {
        var code = CodeInput.Trim();
        if (code.Length == 0) return;
        IsBusy = true;
        try
        {
            var answer = await Client().ActivateAsync(Pc, Device, State.FreshInstall, code);
            if (!answer.Ok)
            {
                Show(answer.Error, true);
                return;
            }
            State.Announced = true;
            TakeActivation(answer, typed: true);
            Save();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // The service's answer to this PC's start: counted for a friend's invite, or why not.
    private void TakeActivation(ReferralActivation answer, bool typed)
    {
        if (answer.Code.Length == 0) return;
        if (answer.Counted)
        {
            State.InviteCode = answer.Code;
            State.InvitedBy = answer.Inviter;
            CodeInput = "";
            var who = answer.Inviter.Length > 0 ? answer.Inviter : "your friend";
            Show($"Thanks for joining! {who} gets credit for inviting you.");
            _notify?.Invoke($"You joined Clip Engine from {who}'s invite. Welcome!");
        }
        else if (typed)
        {
            Show(answer.Reason.Length > 0 ? answer.Reason : "That invite didn't count for this PC.", true);
        }
        Raise(nameof(ShowCodeBox));
        Raise(nameof(InvitedText));
        Raise(nameof(HasInvitedText));
    }

    // New friends are announced, and a reward key goes on this PC when it has no working key of its own; with one
    // (a subscription, say) the key waits on the card to be used later.
    internal async Task TakeStatusAsync(ReferralStatus status)
    {
        var before = State.Friends;
        _status = status;
        State.Friends = status.Friends;
        if (status.Link.Length > 0) State.Link = status.Link;
        if (status.Friends > before)
        {
            var added = status.Friends - before;
            _notify?.Invoke(added == 1
                ? $"A friend installed Clip Engine from your invite ({ProgressText})."
                : $"{added} friends installed Clip Engine from your invite ({ProgressText}).");
        }
        Rewards.Clear();
        foreach (var key in status.Rewards) Rewards.Add(key);
        foreach (var key in status.Rewards.Where(k => !State.SeenRewards.Contains(k)).ToList())
        {
            State.SeenRewards.Add(key);
            if (_plan is not null && !_plan.IsActive && await _plan.UseKeyAsync(key))
            {
                _notify?.Invoke($"Your friends earned you {MonthText} of Pro. It's on now!");
                Show($"Pro is on for {MonthText}, thanks to your friends.");
            }
            else
            {
                _notify?.Invoke($"Your friends earned you {MonthText} of Pro. Your key is on the Friends page.");
            }
        }
        Save();
        Raise(string.Empty);
        CopyLinkCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private async Task UseRewardAsync(string key)
    {
        if (_plan is null) return;
        if (await _plan.UseKeyAsync(key)) Show($"Pro is on for {MonthText}. The key's month started now.");
        else Show(_plan.Message ?? "That key couldn't be turned on.", true);
    }

    // clipengine://invite/<code>, from the invite page's Open in Clip Engine link.
    internal void PrefillCode(string code)
    {
        if (!ShowCodeBox) return;
        CodeInput = code;
    }

    private async Task CopyAsync(string text, string done)
    {
        if (_copy is not null && text.Length > 0) await _copy(text, done);
    }

    private void Show(string? text, bool error = false)
    {
        MessageIsError = error;
        Message = string.IsNullOrEmpty(text) ? null : text;
    }

    private void Save() => _save?.Invoke();

    // The preview tool: a link with two friends so far.
    internal void ShowSample()
    {
        State.Code = "K7QM-2X9D";
        State.Link = "https://clip-engine.workers.dev/r/K7QM2X9D";
        _ = TakeStatusAsync(new ReferralStatus("K7QM-2X9D", State.Link, 2, 0, 3, 1, 30, [], false));
    }
}
