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
    // Who was online, and in what game, at the last look: the next look tells you who came on.
    private Dictionary<string, FriendSeen>? _seen;

    internal FriendsViewModel(MainViewModel main, DesktopSettings desktop)
    {
        _main = main;
        _desktop = desktop;
        AddCommand = new RelayCommand(() => _ = AddAsync(), () => !IsBusy && AddInput.Trim().Length > 0);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        CopyCodeCommand = new RelayCommand(() => _ = _main.Cloud.CopyAsync(FriendCode, "Friend code copied"));
        CopyUsernameCommand = new RelayCommand(() => _ = _main.Cloud.CopyAsync(MyHandleText, "Username copied"), () => MyHandleText.Length > 0);
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
    public RelayCommand CopyUsernameCommand { get; }
    public RelayCommand GoToCloudCommand { get; }

    // Inviting friends to Clip Engine itself, which needs no cloud account.
    public InviteViewModel Invite => _main.Invite;

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

    // Your username: the one friends see and the one on your cloud profile and posts. Changed on the Cloud page.
    public string MyHandleText => _main.Cloud.Account?.Handle is { Length: > 0 } h ? $"@{h}" : "";
    public bool HasMyHandle => MyHandleText.Length > 0;
    public string MyHandleCaption => HasMyHandle
        ? "Friends see you by this name, the same one on your cloud profile and clips. Change it on the Cloud page."
        : "Pick a username on the Cloud page so friends can find you by name.";

    // A small notice when a friend comes online or starts a game.
    public bool FriendNotices
    {
        get => _desktop.FriendNotices;
        set
        {
            if (_desktop.FriendNotices == value) return;
            _desktop.FriendNotices = value;
            _main.SaveDesktopSettingsSoon();
            Raise();
        }
    }

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

    internal FriendRow SampleFriend(string account, string name, string? handle, bool online, string game, string? invite,
                                    TimeSpan? onlineFor = null, TimeSpan? playingFor = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new(account, name, handle, online, game, invite, FriendState.Friends, this,
                   onlineFor is { } o ? now - o : null, playingFor is { } p ? now - p : null);
    }

    internal FriendRow SampleRequest(string account, string name, string? handle) =>
        new(account, name, handle, false, "", null, FriendState.Received, this);

    private void OnAccountChanged()
    {
        Raise(nameof(SignedIn));
        Raise(nameof(NotSignedIn));
        Raise(nameof(MyHandleText));
        Raise(nameof(HasMyHandle));
        Raise(nameof(MyHandleCaption));
        CopyUsernameCommand.NotifyCanExecuteChanged();
        if (!SignedIn)
        {
            _loaded = false;
            _seen = null;
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

    // Straight into the friend's squad, under your squad name (or your cloud name if you have none yet).
    internal async Task JoinAsync(FriendRow row)
    {
        if (row.Invite is not { } invite) return;
        var name = _main.Squad.PlayerName.Trim().Length > 0 ? _main.Squad.PlayerName : _main.Cloud.AccountName;
        await _main.Squad.JoinFromFriendAsync(invite, name, row.DisplayName);
        _main.GoToCommand.Execute(Page.Squad);
    }

    internal Task CopyUsernameAsync(FriendRow row) =>
        _main.Cloud.CopyAsync(row.HandleText.Length > 0 ? row.HandleText : row.Name, "Username copied");

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
        Announce();
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
                this,
                Time(n?["onlineSince"]),
                Time(n?["gameSince"])))
            .OrderByDescending(f => f.Online).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        list.Clear();
        foreach (var item in items) list.Add(item);
    }

    private static DateTimeOffset? Time(JsonNode? ms) =>
        ms is JsonValue v && v.TryGetValue<long>(out var t) && t > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t) : null;

    // Compares this look with the last one; the first look after signing in only remembers.
    private void Announce()
    {
        var now = Friends.ToDictionary(f => f.Account, f => new FriendSeen(f.DisplayName, f.Online, f.Game));
        var before = _seen;
        _seen = now;
        if (before is null) return;
        foreach (var text in FriendAlerts.Changes(before, now))
        {
            _main.AddActivity(ActivityKind.Info, text);
            if (FriendNotices) _main.ShowFriendNotice(text);
        }
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

internal sealed record FriendSeen(string Name, bool Online, string Game);

// What changed between two looks at the roster, said the way a notice says it.
internal static class FriendAlerts
{
    public static IReadOnlyList<string> Changes(IReadOnlyDictionary<string, FriendSeen> before, IReadOnlyDictionary<string, FriendSeen> now)
    {
        var lines = new List<string>();
        foreach (var (account, friend) in now)
        {
            if (!friend.Online) continue;
            before.TryGetValue(account, out var was);
            var wasOnline = was?.Online == true;
            if (!wasOnline)
                lines.Add(friend.Game.Length > 0 ? $"{friend.Name} came online · playing {friend.Game}" : $"{friend.Name} came online");
            else if (friend.Game.Length > 0 && !string.Equals(friend.Game, was!.Game, StringComparison.OrdinalIgnoreCase))
                lines.Add($"{friend.Name} started playing {friend.Game}");
        }
        return lines;
    }

    // "25 min", "2 h 5 min", "just now".
    public static string Since(DateTimeOffset start, DateTimeOffset now)
    {
        var t = now - start;
        if (t < TimeSpan.FromMinutes(1)) return "just now";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes} min";
        var m = t.Minutes;
        return m == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {m} min";
    }
}

public sealed class FriendRow
{
    internal FriendRow(string account, string name, string? handle, bool online, string game, string? invite, FriendState state,
                       FriendsViewModel owner, DateTimeOffset? onlineSince = null, DateTimeOffset? gameSince = null)
    {
        OnlineSince = onlineSince;
        GameSince = gameSince;
        Account = account;
        Name = name;
        Handle = handle;
        Online = online;
        Game = game;
        Invite = invite;
        State = state;
        AcceptCommand = new RelayCommand(() => _ = owner.AcceptAsync(this));
        RemoveCommand = new RelayCommand(() => _ = owner.RemoveAsync(this));
        JoinCommand = new RelayCommand(() => _ = owner.JoinAsync(this), () => CanJoin);
        CopyUsernameCommand = new RelayCommand(() => _ = owner.CopyUsernameAsync(this));
    }

    public string Account { get; }
    public string Name { get; }
    public string? Handle { get; }
    public bool Online { get; }
    public string Game { get; }
    public string? Invite { get; }
    public FriendState State { get; }
    public DateTimeOffset? OnlineSince { get; }
    public DateTimeOffset? GameSince { get; }

    public RelayCommand AcceptCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand JoinCommand { get; }
    public RelayCommand CopyUsernameCommand { get; }

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string HandleText => Handle is { Length: > 0 } h ? $"@{h}" : "";
    // A friend's username is their cloud username; their display name is shown beside it.
    public string DisplayName => HandleText.Length > 0 ? HandleText : Name;
    public string SecondaryName => HandleText.Length > 0 && !string.Equals(Name, Handle, StringComparison.OrdinalIgnoreCase) ? Name : "";
    public bool CanJoin => Online && Invite is not null;
    public bool IsPlaying => Online && Game.Length > 0;

    public string StatusText => State switch
    {
        FriendState.Received => "Wants to be friends",
        FriendState.Sent => "Request sent",
        _ when !Online => "Offline",
        _ when Game.Length > 0 => $"Playing {Game}{For(GameSince)}{(Invite is not null ? " · in a squad" : "")}",
        _ => $"Online{For(OnlineSince)}{(Invite is not null ? " · in a squad" : "")}",
    };

    private static string For(DateTimeOffset? since) => since is { } s ? $" · {FriendAlerts.Since(s, DateTimeOffset.UtcNow)}" : "";

    public string RemoveText => State switch
    {
        FriendState.Received => "Decline",
        FriendState.Sent => "Cancel",
        _ => "Remove",
    };
}
