using System.Text.Json.Nodes;

namespace ClipEngine.Desktop.Services.Squad;

internal sealed record SquadMemberState(string Id, string Name, bool Owner, bool Online, string Game);

internal sealed record SquadGroup(string Game, IReadOnlyList<string> Members);

// Squad sync on this PC. It tells the squad what this player is recording and when each match
// started, sends their moments, saves this player's own view of each squadmate's moment by
// asking the engine for that stretch of its replay buffer, and reports every view to the squad.
// Everything runs on one thread (the UI's); the channel's messages are posted to it.
internal sealed class SquadSync : IDisposable
{
    // Pings at connect to find the clock quickly, then now and then to follow it.
    private static readonly long[] PingSchedule = [0, 1000, 2500, 5000];
    private const long PingEvery = 30_000;
    private const long OwnClipGrace = 8000;  // how long past a moment's window an automatic clip may take to save

    private readonly SquadSettings _settings;
    private readonly ISquadChannel _channel;
    private readonly Func<string, bool> _sendEngineCommand;
    private readonly Func<long> _now;
    private readonly Func<string, LocalClip?> _readClip;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, SquadMemberState> _members = [];
    private readonly Dictionary<string, (string Type, string Game, long LocalMs, string Text)> _sent = [];
    private readonly Dictionary<string, OwnMoment> _own = [];
    private readonly Dictionary<string, (string Type, string Game, long At, string By)> _theirs = [];
    private readonly List<LocalClip> _recentClips = [];
    private readonly Dictionary<long, long> _pings = [];
    private (string Type, string Game, string Text)? _lastDetected;
    private long _connectedAt;
    private int _pingsSent;
    private long _nextPing;
    private int _refs;
    private string? _game;
    private long? _matchStart;  // server ms

    private sealed class OwnMoment(string type, string game, long localMs, long deadline)
    {
        public string Type { get; } = type;
        public string Game { get; } = game;
        public long LocalMs { get; } = localMs;
        public long Deadline { get; } = deadline;
        public bool Done { get; set; }
    }

    public SquadSync(SquadSettings settings, ISquadChannel channel, SquadViewIndex index, Func<string, bool> sendEngineCommand,
                     Func<string, LocalClip?> readClip, Func<long>? now = null, Action<Action>? post = null)
    {
        _settings = settings;
        _channel = channel;
        Index = index;
        _sendEngineCommand = sendEngineCommand;
        _readClip = readClip;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _post = post ?? (a => a());
        _channel.Received += m => _post(() => Handle(m));
        _channel.ConnectedChanged += c => _post(() => OnConnected(c));
        _channel.Rejected += () => _post(() => Rejected?.Invoke());
    }

    public SquadClock Clock { get; } = new();
    public SquadViewIndex Index { get; }
    public SquadInfo? Info { get; private set; }
    public bool Connected => _channel.Connected;
    public IReadOnlyCollection<SquadMemberState> Members => _members.Values;
    public IReadOnlyList<SquadGroup> Groups { get; private set; } = [];
    public string? Game => _game;

    // The clip window around a squadmate's moment, from the engine's automatic clip settings.
    public int BeforeSeconds { get; set; } = 10;
    public int AfterSeconds { get; set; } = 5;

    // Something changed on the Squad page: members, presence, the connection.
    public event Action? Changed;
    // The library changed: a view was saved, uploaded, removed or starred.
    public event Action? LibraryChanged;
    // A line for the activity feed, like "Saving your view of Kai's kill".
    public event Action<string>? Activity;
    // A view to upload: the moment id and the clip on this PC.
    public event Action<LocalView>? UploadWanted;
    // The squad turned this PC away.
    public event Action? Rejected;

    public void Start() => _channel.Start();

    // The teammates playing with this player right now.
    public IReadOnlyList<SquadMemberState> PlayingWithMe()
    {
        var me = _settings.MemberId;
        var group = Groups.FirstOrDefault(g => g.Members.Contains(me));
        if (group is null) return [];
        return group.Members.Where(m => m != me && _members.ContainsKey(m)).Select(m => _members[m]).ToList();
    }

    public void Unlink(string member, bool undo = false) =>
        _channel.Send(new JsonObject { ["t"] = "unlink", ["member"] = member, ["undo"] = undo });

    public void Want(string moment, string member) =>
        _channel.Send(new JsonObject { ["t"] = "want", ["moment"] = moment, ["member"] = member });

    // ---- from the engine ----

    public void OnRecordingStarted(string label)
    {
        _game = label == "Desktop" ? null : label;
        _matchStart = null;
        SendPresence();
    }

    public void OnRecordingStopped()
    {
        _game = null;
        _matchStart = null;
        SendPresence();
    }

    public void OnMomentDetected(string type, string game, string text) => _lastDetected = (type, game, text);

    // The wall-clock time of the moment just detected; the engine logs it right after it.
    public void OnMomentTime(string type, long unixMs)
    {
        var detected = _lastDetected;
        _lastDetected = null;
        if (detected is not { } d || d.Type != type) return;
        if (type == "match_start")
        {
            _matchStart = Clock.ToServer(unixMs);
            SendPresence();
        }
        if (!SquadPlan.ShouldShare(type, _settings.ShareMoments) || !_channel.Connected || !Clock.Synced) return;
        var reference = $"r{++_refs}";
        _sent[reference] = (type, d.Game, unixMs, d.Text);
        _channel.Send(new JsonObject
        {
            ["t"] = "moment", ["ref"] = reference, ["type"] = type, ["at"] = Clock.ToServer(unixMs), ["game"] = d.Game, ["text"] = d.Text,
        });
    }

    public void OnClipSaved(string path)
    {
        if (_readClip(path) is not { } clip || clip.Trigger == "squad") return;
        _recentClips.Add(clip);
        _recentClips.RemoveAll(c => c.UtcStartMs + (long)(c.Seconds * 1000) < _now() - 180_000);
        foreach (var (id, own) in _own)
        {
            if (own.Done || SquadPlan.OwnClipFor([clip], own.LocalMs) is null) continue;
            own.Done = true;
            AddView(id, own.Type, own.Game, Clock.ToServer(own.LocalMs), clip);
        }
    }

    // The engine saved this player's view of a squad moment.
    public void OnSquadViewSaved(string moment, string path)
    {
        if (_readClip(path) is not { } clip) return;
        string type, game;
        long at;
        if (_theirs.TryGetValue(moment, out var theirs)) (type, game, at) = (theirs.Type, theirs.Game, theirs.At);
        else if (_own.TryGetValue(moment, out var own)) (type, game, at) = (own.Type, own.Game, Clock.ToServer(own.LocalMs));
        else return;
        AddView(moment, type, game, at, clip);
    }

    // Once a second: pings, and this player's own moments no automatic clip covered get a clip of their own.
    public void Tick()
    {
        var now = _now();
        if (_channel.Connected && now >= _nextPing)
        {
            _pings[now] = now;
            _channel.Send(new JsonObject { ["t"] = "ping", ["c"] = now });
            _pingsSent++;
            _nextPing = _pingsSent < PingSchedule.Length ? _connectedAt + PingSchedule[_pingsSent] : now + PingEvery;
        }
        foreach (var (id, own) in _own)
        {
            if (own.Done || now < own.Deadline) continue;
            own.Done = true;
            var (start, end) = SquadPlan.SaveWindow(own.LocalMs, BeforeSeconds, AfterSeconds);
            _sendEngineCommand($"squad-save {id} {start} {end} {own.Game}");
        }
        foreach (var id in _own.Where(o => o.Value.Done && now - o.Value.LocalMs > 300_000).Select(o => o.Key).ToList()) _own.Remove(id);
    }

    // ---- from the squad ----

    internal void Handle(JsonObject m)
    {
        switch (m["t"]?.GetValue<string>())
        {
            case "welcome":
                Info = SquadInfo.From(m["squad"]);
                _members.Clear();
                foreach (var member in Info.Members) _members[member.Id] = new(member.Id, member.Name, member.Owner, member.Online, "");
                foreach (var p in m["presences"]?.AsArray() ?? [])
                    SetPresence(p?["member"]?.GetValue<string>(), true, p?["game"]?.GetValue<string>() ?? "");
                Groups = ReadGroups(m["groups"]);
                if (Info.Name.Length > 0) _settings.SquadName = Info.Name;
                Changed?.Invoke();
                break;
            case "pong":
                if (m["c"]?.GetValue<long>() is { } sent && _pings.Remove(sent))
                {
                    Clock.AddSample(sent, _now(), m["s"]?.GetValue<long>() ?? 0);
                    foreach (var old in _pings.Keys.Where(k => k < _now() - 60_000).ToList()) _pings.Remove(old);
                }
                break;
            case "presence":
                SetPresence(m["member"]?.GetValue<string>(), m["online"]?.GetValue<bool>() ?? false, m["game"]?.GetValue<string>() ?? "");
                Groups = ReadGroups(m["groups"]);
                Changed?.Invoke();
                break;
            case "joined":
                if (m["member"]?.GetValue<string>() is { } joined)
                {
                    var name = m["name"]?.GetValue<string>() ?? "";
                    _members[joined] = new(joined, name, false, false, "");
                    Activity?.Invoke($"{name} joined your squad");
                    Changed?.Invoke();
                }
                break;
            case "member":
                if (m["member"]?.GetValue<string>() is { } renamed && _members.TryGetValue(renamed, out var was))
                {
                    _members[renamed] = was with { Name = m["name"]?.GetValue<string>() ?? was.Name };
                    Changed?.Invoke();
                }
                break;
            case "left":
                if (m["member"]?.GetValue<string>() is { } left && _members.Remove(left, out var gone))
                {
                    if (m["owner"]?.GetValue<string>() is { } owner && _members.TryGetValue(owner, out var next))
                        _members[owner] = next with { Owner = true };
                    Activity?.Invoke($"{gone.Name} left your squad");
                    Changed?.Invoke();
                }
                break;
            case "moment-ok":
                OnMomentAccepted(m);
                break;
            case "moment":
                OnSquadmateMoment(m);
                break;
            case "upload-request":
                if (m["moment"]?.GetValue<string>() is { } wanted && Index.Find(wanted) is { } view && !view.Uploaded
                    && SquadPlan.UploadsOnRequest(_settings.Uploads))
                    UploadWanted?.Invoke(view);
                break;
            case "view" or "uploaded" or "removed" or "star":
                LibraryChanged?.Invoke();
                break;
        }
    }

    private void OnMomentAccepted(JsonObject m)
    {
        var reference = m["ref"]?.GetValue<string>() ?? "";
        var id = m["id"]?.GetValue<string>();
        if (id is null || !_sent.Remove(reference, out var sent)) return;
        var deadline = sent.LocalMs + AfterSeconds * 1000L + OwnClipGrace;
        var own = new OwnMoment(sent.Type, sent.Game, sent.LocalMs, deadline);
        _own[id] = own;
        var linked = m["linked"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [];
        if (linked.Count > 0)
        {
            var names = linked.Select(l => _members.TryGetValue(l, out var x) ? x.Name : "a squadmate");
            Activity?.Invoke($"Your {EventLabel(sent.Type)} is being saved by {string.Join(", ", names)} too");
        }
        // An automatic clip may already have been saved around it.
        if (_recentClips.Select(c => SquadPlan.OwnClipFor([c], own.LocalMs) is null ? null : c).OfType<LocalClip>()
                .OrderBy(c => c.Seconds).FirstOrDefault() is { } clip)
        {
            own.Done = true;
            AddView(id, own.Type, own.Game, Clock.ToServer(own.LocalMs), clip);
        }
    }

    private void OnSquadmateMoment(JsonObject m)
    {
        var id = m["id"]?.GetValue<string>();
        var game = m["game"]?.GetValue<string>() ?? "";
        var at = m["at"]?.GetValue<long>() ?? 0;
        var type = m["type"]?.GetValue<string>() ?? "";
        var by = m["byName"]?.GetValue<string>() ?? "A squadmate";
        if (id is null || at == 0) return;
        // Only while recording the same game: anything else isn't playing together.
        if (_game is null || !string.Equals(_game, game, StringComparison.OrdinalIgnoreCase)) return;
        _theirs[id] = (type, game, at, by);
        if (_theirs.Count > 500) _theirs.Remove(_theirs.Keys.First());
        var (start, end) = SquadPlan.SaveWindow(Clock.ToLocal(at), BeforeSeconds, AfterSeconds);
        if (_sendEngineCommand($"squad-save {id} {start} {end} {_game}"))
            Activity?.Invoke($"Saving your view of {by}'s {EventLabel(type)}");
    }

    private void AddView(string moment, string type, string game, long momentAt, LocalClip clip)
    {
        var view = new LocalView
        {
            Moment = moment, Path = clip.Path, Type = type, Game = game, MomentAt = momentAt,
            UtcStartMs = Clock.ToServer(clip.UtcStartMs), Seconds = clip.Seconds,
        };
        Index.Add(view);
        _channel.Send(new JsonObject { ["t"] = "view", ["moment"] = moment, ["utc_start"] = view.UtcStartMs, ["seconds"] = view.Seconds });
        LibraryChanged?.Invoke();
        if (SquadPlan.UploadsByItself(_settings.Uploads, type, clip.Favorite)) UploadWanted?.Invoke(view);
    }

    private void OnConnected(bool connected)
    {
        if (connected)
        {
            _connectedAt = _now();
            _pingsSent = 0;
            _nextPing = _connectedAt;
            _channel.Send(new JsonObject { ["t"] = "hello", ["app"] = typeof(SquadSync).Assembly.GetName().Version?.ToString(3) ?? "" });
            SendPresence();
            Tick();
        }
        else
        {
            foreach (var id in _members.Keys.ToList()) _members[id] = _members[id] with { Online = false, Game = "" };
            Groups = [];
        }
        Changed?.Invoke();
    }

    private void SendPresence()
    {
        if (!_channel.Connected) return;
        var message = new JsonObject { ["t"] = "presence", ["game"] = _game ?? "" };
        if (_matchStart is { } match) message["match"] = match;
        _channel.Send(message);
    }

    private void SetPresence(string? member, bool online, string game)
    {
        if (member is null || !_members.TryGetValue(member, out var state)) return;
        _members[member] = state with { Online = online, Game = online ? game : "" };
    }

    private static IReadOnlyList<SquadGroup> ReadGroups(JsonNode? groups) =>
        (groups?.AsArray() ?? []).Select(g => new SquadGroup(
            g?["game"]?.GetValue<string>() ?? "",
            g?["members"]?.AsArray().Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? [])).ToList();

    internal static string EventLabel(string type) => type switch
    {
        "match_end" => "match result",
        "match_start" => "match start",
        "highlight" => "highlight",
        _ => type,
    };

    public void Dispose() => _channel.Dispose();
}
