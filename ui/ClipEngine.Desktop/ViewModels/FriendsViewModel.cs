using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Squad;

namespace ClipEngine.Desktop.ViewModels;

// The Friends page: people added by their profile name or friend code, who's online and what they're playing, and a
// Join button for the squad a friend shares. Friends live on the cloud account (server/src/library.js), so the page
// asks to sign in to the cloud first. While the app runs it tells the service every minute that you're online.
public sealed class FriendsViewModel : ObservableObject
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);

    private readonly MainViewModel _main;
    private readonly DesktopSettings _desktop;
    private readonly DispatcherTimer? _timer;
    private string _addInput = "";
    private string _friendCode = "";
    private bool _busy;
    private string? _message;
    private bool _messageIsError;
    private bool _loaded;

    internal FriendsViewModel(MainViewModel main, DesktopSettings desktop)
    {
        _main = main;
        _desktop = desktop;
        AddCommand = new RelayCommand(() => _ = AddAsync(), () => !IsBusy && AddInput.Trim().Length > 0);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        CopyCodeCommand = new RelayCommand(() => _ = _main.Cloud.CopyAsync(FriendCode, "Friend code copied"));
        GoToCloudCommand = new RelayCommand(() => _main.GoToCommand.Execute(Page.Cloud));
        main.Cloud.AccountChanged += OnAccountChanged;

        if (!main.IsLive) return;
        _timer = new DispatcherTimer { Interval = Heartbeat };
        _timer.Tick += (_, _) => _ = TickAsync();
        _timer.Start();
        _ = TickAsync();
    }

    public ObservableCollection<FriendRow> Friends { get; } = [];
    public ObservableCollection<FriendRow> Received { get; } = [];
    public ObservableCollection<FriendRow> Sent { get; } = [];

    public RelayCommand AddCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CopyCodeCommand { get; }
    public RelayCommand GoToCloudCommand { get; }

    public bool SignedIn => _main.Cloud.SignedIn;
    public bool NotSignedIn => !SignedIn;

    public string AddInput
    {
        get => _addInput;
        set
        {
            if (Set(ref _addInput, value ?? "")) AddCommand.NotifyCanExecuteChanged();
        }
    }

    public string FriendCode { get => _friendCode; private set { if (Set(ref _friendCode, value)) Raise(nameof(HasFriendCode)); } }
    public bool HasFriendCode => _friendCode.Length > 0;

    public bool ShareSquad
    {
        get => _desktop.ShareSquadWithFriends;
        set
        {
            if (_desktop.ShareSquadWithFriends == value) return;
            _desktop.ShareSquadWithFriends = value;
            _main.SaveDesktopSettingsSoon();
            Raise();
            _ = SendPresenceAsync();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) AddCommand.NotifyCanExecuteChanged();
        }
    }

    public string? Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message is not null;
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    public int OnlineCount => Friends.Count(f => f.Online);
    public string FriendsCaption => Friends.Count == 0 ? "" : $"{Friends.Count} friends · {OnlineCount} online";
    public bool HasFriends => Friends.Count > 0;
    public bool HasNoFriends => Friends.Count == 0;
    public bool HasReceived => Received.Count > 0;
    public bool HasSent => Sent.Count > 0;

    // What this PC is playing, as friends see it.
    private string Game => _main.RecordingLabel is { } label && label != "Desktop" ? label : "";

    // The squad invite friends can use, when there is one and it's shared.
    private string? Invite => ShareSquad && _main.Squad.InSquad && _main.Squad.InviteCode.Length > 0 ? _main.Squad.InviteCode : null;

    // The preview tool: friends without the service.
    internal void ShowSample(string code, IEnumerable<FriendRow> friends, IEnumerable<FriendRow> received)
    {
        FriendCode = code;
        Friends.Clear();
        foreach (var f in friends) Friends.Add(f);
        Received.Clear();
        foreach (var f in received) Received.Add(f);
        RaiseLists();
    }

    internal FriendRow SampleFriend(string account, string name, string? handle, bool online, string game, string? invite) =>
        new(account, name, handle, online, game, invite, FriendState.Friends, this);

    internal FriendRow SampleRequest(string account, string name, string? handle) =>
        new(account, name, handle, false, "", null, FriendState.Received, this);

    private void OnAccountChanged()
    {
        Raise(nameof(SignedIn));
        Raise(nameof(NotSignedIn));
        if (!SignedIn)
        {
            _loaded = false;
            Friends.Clear();
            Received.Clear();
            Sent.Clear();
            FriendCode = "";
            RaiseLists();
        }
        else if (!_loaded && _main.IsLive)
        {
            _ = TickAsync();
        }
    }

    // Every minute: say this PC is online, and see who else is.
    private async Task TickAsync()
    {
        if (!SignedIn) return;
        await SendPresenceAsync();
        await RefreshAsync(quiet: true);
    }

    private async Task SendPresenceAsync()
    {
        if (_main.Cloud.Api is not { } api || !SignedIn) return;
        var body = new JsonObject { ["game"] = Game };
        if (Invite is { } invite) body["invite"] = invite;
        try
        {
            await api.CallAsync(HttpMethod.Post, $"/v1/accounts/{_desktop.Cloud.AccountId}/presence", _main.Cloud.Token, body);
        }
        catch (Exception e) when (e is SquadException or HttpRequestException or TaskCanceledException)
        {
        }
    }

    // Tells friends this PC went offline, as the app closes.
    internal async Task SignOffAsync()
    {
        if (_main.Cloud.Api is not { } api || !SignedIn) return;
        try
        {
            await api.CallAsync(HttpMethod.Delete, $"/v1/accounts/{_desktop.Cloud.AccountId}/presence", _main.Cloud.Token, null);
        }
        catch (Exception e) when (e is SquadException or HttpRequestException or TaskCanceledException)
        {
        }
    }

    private Task RefreshAsync() => RefreshAsync(quiet: false);

    private async Task RefreshAsync(bool quiet)
    {
        if (_main.Cloud.Api is null || !SignedIn) return;
        await CallAsync(HttpMethod.Get, "", null, quiet);
    }

    private async Task AddAsync()
    {
        var text = AddInput.Trim();
        // A friend code is 8 letters and digits, with or without its dash; anything else is a profile name.
        var compact = text.Replace("-", "").Replace(" ", "");
        var body = compact.Length == 8 && !text.StartsWith('@') && compact.All(char.IsLetterOrDigit)
            ? new JsonObject { ["code"] = text }
            : new JsonObject { ["handle"] = text };
        if (await CallAsync(HttpMethod.Post, "", body))
        {
            AddInput = "";
            Message = Sent.Count > 0 || Friends.Count > 0 ? "Friend request sent. They'll see it on their Friends page." : null;
            MessageIsError = false;
        }
    }

    internal Task AcceptAsync(FriendRow row) => CallAsync(HttpMethod.Post, $"/{row.Account}/accept", null);

    internal Task RemoveAsync(FriendRow row) => CallAsync(HttpMethod.Delete, $"/{row.Account}", null);

    internal void Join(FriendRow row)
    {
        if (row.Invite is not { } invite) return;
        _main.Squad.PrefillInvite(invite);
        _main.GoToCommand.Execute(Page.Squad);
    }

    private async Task<bool> CallAsync(HttpMethod method, string path, JsonObject? body, bool quiet = false)
    {
        if (_main.Cloud.Api is not { } api) return false;
        if (!quiet) IsBusy = true;
        try
        {
            var reply = await api.CallAsync(method, $"/v1/accounts/{_desktop.Cloud.AccountId}/friends{path}", _main.Cloud.Token, body);
            Apply(reply);
            if (!quiet && method != HttpMethod.Post) Message = null;
            return true;
        }
        catch (Exception e) when (e is SquadException or HttpRequestException or TaskCanceledException)
        {
            if (!quiet)
            {
                Message = e.Message;
                MessageIsError = true;
            }
            return false;
        }
        finally
        {
            if (!quiet) IsBusy = false;
        }
    }

    private void Apply(JsonNode? reply)
    {
        if (reply is null) return;
        _loaded = true;
        FriendCode = reply["friendCode"]?.GetValue<string>() ?? FriendCode;
        Fill(Friends, reply["friends"], FriendState.Friends);
        Fill(Received, reply["received"], FriendState.Received);
        Fill(Sent, reply["sent"], FriendState.Sent);
        RaiseLists();
    }

    private void Fill(ObservableCollection<FriendRow> list, JsonNode? rows, FriendState state)
    {
        var items = (rows?.AsArray() ?? []).Select(n => new FriendRow(
                n?["account"]?.GetValue<string>() ?? "",
                n?["name"]?.GetValue<string>() ?? "",
                n?["handle"]?.GetValue<string>(),
                n?["online"]?.GetValue<bool>() ?? false,
                n?["game"]?.GetValue<string>() ?? "",
                n?["invite"]?.GetValue<string>(),
                state,
                this))
            .OrderByDescending(f => f.Online).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        list.Clear();
        foreach (var item in items) list.Add(item);
    }

    private void RaiseLists()
    {
        Raise(nameof(OnlineCount));
        Raise(nameof(FriendsCaption));
        Raise(nameof(HasFriends));
        Raise(nameof(HasNoFriends));
        Raise(nameof(HasReceived));
        Raise(nameof(HasSent));
    }
}

public enum FriendState { Friends, Received, Sent }

public sealed class FriendRow
{
    internal FriendRow(string account, string name, string? handle, bool online, string game, string? invite, FriendState state,
                       FriendsViewModel owner)
    {
        Account = account;
        Name = name;
        Handle = handle;
        Online = online;
        Game = game;
        Invite = invite;
        State = state;
        AcceptCommand = new RelayCommand(() => _ = owner.AcceptAsync(this));
        RemoveCommand = new RelayCommand(() => _ = owner.RemoveAsync(this));
        JoinCommand = new RelayCommand(() => owner.Join(this), () => CanJoin);
    }

    public string Account { get; }
    public string Name { get; }
    public string? Handle { get; }
    public bool Online { get; }
    public string Game { get; }
    public string? Invite { get; }
    public FriendState State { get; }

    public RelayCommand AcceptCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand JoinCommand { get; }

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string HandleText => Handle is { Length: > 0 } h ? $"@{h}" : "";
    public bool CanJoin => Online && Invite is not null;
    public bool IsPlaying => Online && Game.Length > 0;

    public string StatusText => State switch
    {
        FriendState.Received => "Wants to be friends",
        FriendState.Sent => "Request sent",
        _ when !Online => "Offline",
        _ when Game.Length > 0 => Invite is not null ? $"Playing {Game} · in a squad" : $"Playing {Game}",
        _ => Invite is not null ? "Online · in a squad" : "Online",
    };

    public string RemoveText => State switch
    {
        FriendState.Received => "Decline",
        FriendState.Sent => "Cancel",
        _ => "Remove",
    };
}
