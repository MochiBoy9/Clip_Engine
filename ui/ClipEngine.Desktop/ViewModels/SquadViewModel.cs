using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Squad;

namespace ClipEngine.Desktop.ViewModels;

// The Squad page: making or joining a squad, who's in it and who's playing together, what this
// PC shares, and the squad's library of moments with everyone's view of each.
public sealed class SquadViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan KeepCache = TimeSpan.FromDays(7);
    private const int MaxTiles = 4;

    private readonly MainViewModel _main;
    private readonly DesktopSettings _desktop;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _libraryTimer;
    private readonly Queue<LocalView> _uploads = new();
    private readonly HashSet<string> _queued = [];
    private readonly List<LocalView> _held = [];
    private SquadSync? _sync;
    private SquadApi? _api;
    private string _token = "";
    private bool _busy;
    private string? _message;
    private bool _messageIsError;
    private string _playerName;
    private string _squadNameInput = "";
    private string _inviteInput = "";
    private string? _uploadStatus;
    private bool _uploadRunning;
    private long? _next;
    private bool _libraryLoading;
    private string? _libraryMessage;
    private bool _confirmLeave;
    private bool _makingHighlight;
    private int _before = 10;
    private int _after = 5;
    private bool _loaded;

    internal SquadViewModel(MainViewModel main, DesktopSettings desktop)
    {
        _main = main;
        _desktop = desktop;
        _playerName = Settings.PlayerName.Length > 0 ? Settings.PlayerName : Environment.UserName;
        Index = new SquadViewIndex(main.IsLive ? Path.Combine(AppPaths.DataDir, "squad", "views.json") : null);

        CreateCommand = new RelayCommand(() => _ = CreateAsync(), () => !IsBusy);
        JoinCommand = new RelayCommand(() => _ = JoinAsync(), () => !IsBusy);
        CopyInviteCommand = new RelayCommand(() => _ = CopyAsync(InviteCode, "Invite code copied. Squadmates paste it on their Squad page."));
        CopyInviteLinkCommand = new RelayCommand(() => _ = CopyAsync(InviteLink, "Invite link copied. Opening it on a PC with Clip Engine joins the squad."));
        NewInviteCommand = new RelayCommand(() => _ = NewInviteAsync(), () => !IsBusy && IsOwner);
        LeaveCommand = new RelayCommand(() => ConfirmLeave = true);
        CancelLeaveCommand = new RelayCommand(() => ConfirmLeave = false);
        ConfirmLeaveCommand = new RelayCommand(() => _ = LeaveAsync(), () => !IsBusy);
        RenameCommand = new RelayCommand(() => _ = RenameAsync(), () => !IsBusy);
        RefreshLibraryCommand = new RelayCommand(() => _ = LoadLibraryAsync(false));
        LoadMoreCommand = new RelayCommand(() => _ = LoadLibraryAsync(true), () => _next is not null);

        foreach (var type in new[] { "kill", "assist", "revive", "death", "highlight", "match_end" })
            ShareMoments.Add(new EventToggle(type, EventKinds.Label(type), SaveShareMoments));
        foreach (var toggle in ShareMoments) toggle.IsOn = Settings.ShareMoments.Contains(toggle.Name);
        _loaded = true;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _sync?.Tick();
        _libraryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _libraryTimer.Tick += (_, _) =>
        {
            _libraryTimer.Stop();
            _ = LoadLibraryAsync(false);
        };

        if (!main.IsLive) return;
        _ = Task.Run(CleanCache);
        if (Settings.InSquad) Connect();
    }

    internal SquadSettings Settings => _desktop.Squad;
    internal SquadViewIndex Index { get; }
    internal static string CacheDir => Path.Combine(AppPaths.DataDir, "squad", "cache");

    public ObservableCollection<SquadMemberRow> Members { get; } = [];
    public ObservableCollection<SquadMomentRow> Moments { get; } = [];
    public ObservableCollection<EventToggle> ShareMoments { get; } = [];

    public RelayCommand CreateCommand { get; }
    public RelayCommand JoinCommand { get; }
    public RelayCommand CopyInviteCommand { get; }
    public RelayCommand CopyInviteLinkCommand { get; }
    public RelayCommand NewInviteCommand { get; }
    public RelayCommand LeaveCommand { get; }
    public RelayCommand CancelLeaveCommand { get; }
    public RelayCommand ConfirmLeaveCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand RefreshLibraryCommand { get; }
    public RelayCommand LoadMoreCommand { get; }

    public bool InSquad => Settings.InSquad;
    public bool NotInSquad => !Settings.InSquad;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            CreateCommand.NotifyCanExecuteChanged();
            JoinCommand.NotifyCanExecuteChanged();
            NewInviteCommand.NotifyCanExecuteChanged();
            ConfirmLeaveCommand.NotifyCanExecuteChanged();
            RenameCommand.NotifyCanExecuteChanged();
        }
    }

    public string? Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message is not null;
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    public string PlayerName { get => _playerName; set => Set(ref _playerName, value ?? ""); }
    public string SquadNameInput { get => _squadNameInput; set => Set(ref _squadNameInput, value ?? ""); }
    public string InviteInput { get => _inviteInput; set => Set(ref _inviteInput, value ?? ""); }

    // The online service: built into release builds, or one of the player's own.
    public string ServerAddress
    {
        get => _desktop.OnlineServer;
        set
        {
            if (_desktop.OnlineServer == (value ?? "").Trim()) return;
            _desktop.OnlineServer = (value ?? "").Trim();
            _main.SaveDesktopSettingsSoon();
            Raise();
            Raise(nameof(NeedsServer));
        }
    }

    public string ServerHint => Online.BuiltInServer.Length > 0 ? Online.BuiltInServer : "https://clip-engine.<you>.workers.dev";
    public bool NeedsServer => Online.ServerUrl(_desktop).Length == 0;

    public string SquadName => Settings.SquadName.Length > 0 ? Settings.SquadName : "Your squad";
    public string InviteCode => _sync?.Info?.Invite ?? "";
    public bool HasInvite => InviteCode.Length > 0;
    public string InviteLink => HasInvite ? $"{Online.ServerUrl(_desktop)}/s/{InviteCode}" : "";
    public bool IsOwner => _sync?.Members.FirstOrDefault(m => m.Id == Settings.MemberId)?.Owner == true;
    public bool IsConnected => _sync?.Connected == true;
    public string ConnectionText => _sync is null ? "Not connected" : _sync.Connected ? "Connected" : "Connecting…";

    public string MembersCaption
    {
        get
        {
            var online = _sync?.Members.Count(m => m.Online) ?? 0;
            var count = _sync?.Members.Count ?? 0;
            return count == 0 ? "" : $"{count} {(count == 1 ? "member" : "members")} · {online} online";
        }
    }

    // "Playing The Finals with Kai and Rin", or how linking works when nobody is.
    public string TogetherTitle
    {
        get
        {
            var with = _sync?.PlayingWithMe() ?? [];
            if (with.Count == 0) return "Not playing together right now";
            return $"Playing {_sync!.Game} with {Names(with.Select(m => m.Name).ToList())}";
        }
    }

    public string TogetherDetail => (_sync?.PlayingWithMe().Count ?? 0) > 0
        ? "Every moment you share is saved from their view too, and theirs from yours."
        : "Squadmates recording the same game, and starting the same match, are linked by themselves.";

    public bool IsTogether => (_sync?.PlayingWithMe().Count ?? 0) > 0;

    public string StorageText
    {
        get
        {
            if (_sync?.Info is not { StorageLimit: > 0 } info) return "";
            return $"{Format.Bytes(info.StorageUsed)} of {Format.Bytes(info.StorageLimit)} used by the shared library";
        }
    }

    public bool UploadsHighlights { get => Settings.Uploads == "highlights"; set { if (value) SetUploads("highlights"); } }
    public bool UploadsAll { get => Settings.Uploads == "all"; set { if (value) SetUploads("all"); } }
    public bool UploadsOff { get => Settings.Uploads == "off"; set { if (value) SetUploads("off"); } }

    public string UploadsCaption => Settings.Uploads switch
    {
        "all" => "Every view of yours goes up to the shared library as it's saved.",
        "off" => "Your views stay on this PC. Squadmates can't ask for them.",
        _ => "Highlights, match results and starred clips go up by themselves. Others go up when a squadmate asks.",
    };

    public bool JoinOnDiscord
    {
        get => Settings.JoinOnDiscord;
        set
        {
            if (Settings.JoinOnDiscord == value) return;
            Settings.JoinOnDiscord = value;
            _main.SaveDesktopSettingsSoon();
            _sync?.UpdateDiscord();
            Raise();
        }
    }

    public string? UploadStatus { get => _uploadStatus; private set { if (Set(ref _uploadStatus, value)) Raise(nameof(IsUploading)); } }
    public bool IsUploading => _uploadStatus is not null;

    public string? LibraryMessage { get => _libraryMessage; private set { if (Set(ref _libraryMessage, value)) Raise(nameof(HasLibraryMessage)); } }
    public bool HasLibraryMessage => _libraryMessage is not null;
    public bool HasMoments => Moments.Count > 0;
    public bool HasNoMoments => Moments.Count == 0 && !_libraryLoading;
    public bool HasMore => _next is not null;

    public bool ConfirmLeave
    {
        get => _confirmLeave;
        private set { if (Set(ref _confirmLeave, value)) Raise(nameof(NotConfirmingLeave)); }
    }

    public bool NotConfirmingLeave => !_confirmLeave;

    public string LeaveWarning => (_sync?.Members.Count ?? 1) <= 1
        ? "You're the only one in it, so the squad and its shared library are deleted. Your views stay on this PC."
        : "Your views stay on this PC, but the ones you sent leave the shared library. You can join again with an invite.";

    public bool IsMakingHighlight { get => _makingHighlight; private set => Set(ref _makingHighlight, value); }

    // ---- from the rest of the app ----

    internal void SetClipWindow(int before, int after)
    {
        _before = before;
        _after = after;
        if (_sync is null) return;
        _sync.BeforeSeconds = before;
        _sync.AfterSeconds = after;
    }

    internal void OnEngineEvent(EngineEvent e)
    {
        var sync = _sync;
        if (sync is null) return;
        switch (e)
        {
            case EngineStarted:
                sync.EngineRestarted();
                break;
            case RecordingStarted r:
                sync.OnRecordingStarted(r.Label);
                break;
            case RecordingStopped:
                sync.OnRecordingStopped();
                ReleaseHeldUploads();
                break;
            case MomentDetected m:
                sync.OnMomentDetected(m.Type, m.Game, m.Text);
                break;
            case MomentTime t:
                sync.OnMomentTime(t.Type, t.UnixMs);
                break;
            case ClipSaved s:
                sync.OnClipSaved(s.Path);
                break;
            case SquadViewSaved v:
                sync.OnSquadViewSaved(v.Moment, v.Path);
                break;
            case SquadViewSkipped k:
                _main.AddActivity(ActivityKind.Warning, $"A squad view couldn't be saved: {k.Reason}");
                break;
        }
    }

    internal void OnEngineStopped()
    {
        _sync?.OnRecordingStopped();
        ReleaseHeldUploads();
    }

    // clipengine://squad/join/<code>, from an invite link.
    internal void PrefillInvite(string invite)
    {
        if (InSquad)
        {
            Message = "You're already in a squad. Leave it first to join another.";
            MessageIsError = true;
            return;
        }
        InviteInput = invite;
        Message = "Check your name, then press Join.";
        MessageIsError = false;
    }

    // From a friend's right-click menu or Join button: straight in, no invite to paste.
    internal async Task JoinFromFriendAsync(string invite, string name, string friend)
    {
        if (InSquad)
        {
            Message = "You're already in a squad. Leave it first to join another.";
            MessageIsError = true;
            return;
        }
        if (PlayerName.Trim().Length == 0) PlayerName = name.Trim().Length > 32 ? name.Trim()[..32] : name.Trim();
        InviteInput = invite;
        await JoinAsync();
        if (InSquad) _main.ShowToast($"Joined {friend}'s squad");
    }

    // ---- making, joining and leaving ----

    private async Task CreateAsync()
    {
        if (!CheckName()) return;
        await RunAsync(async api =>
        {
            var (token, squad) = await api.CreateAsync(PlayerName.Trim(), Online.DeviceId(_desktop), SquadNameInput.Trim());
            Joined(token, squad);
            _main.AddActivity(ActivityKind.Info, $"Made the squad {squad.Name}");
        });
    }

    private async Task JoinAsync()
    {
        if (!CheckName()) return;
        if (InviteInput.Trim().Length == 0)
        {
            Fail("Paste the invite code or link a squadmate sent you.");
            return;
        }
        await RunAsync(async api =>
        {
            var (token, squad) = await api.JoinAsync(InviteInput.Trim(), PlayerName.Trim(), Online.DeviceId(_desktop));
            Joined(token, squad);
            _main.AddActivity(ActivityKind.Info, $"Joined the squad {squad.Name}");
        });
    }

    private bool CheckName()
    {
        if (PlayerName.Trim().Length is > 0 and <= 32) return true;
        Fail("Pick a name of up to 32 characters, so squadmates know who you are.");
        return false;
    }

    private void Joined(string token, SquadInfo squad)
    {
        Settings.SquadId = squad.Id;
        Settings.SquadName = squad.Name;
        Settings.MemberId = squad.You;
        Settings.PlayerName = PlayerName.Trim();
        Settings.Token = Online.Protect(token);
        _main.SaveDesktopSettingsNow();
        InviteInput = SquadNameInput = "";
        Message = null;
        Index.Clear();
        Connect(token);
        RaiseSquad();
    }

    private async Task LeaveAsync()
    {
        ConfirmLeave = false;
        var api = _api;
        var squad = Settings.SquadId;
        var token = _token;
        IsBusy = true;
        try
        {
            if (api is not null) await api.LeaveAsync(squad, token);
        }
        catch (SquadException e) when (!e.NotAMember)
        {
            Fail($"Couldn't leave the squad: {e.Message}");
            return;
        }
        finally
        {
            IsBusy = false;
        }
        _main.AddActivity(ActivityKind.Info, $"Left the squad {SquadName}");
        Disconnect();
        Message = "You left the squad.";
        MessageIsError = false;
    }

    private async Task NewInviteAsync()
    {
        await RunAsync(async api =>
        {
            var info = await api.RotateInviteAsync(Settings.SquadId, _token);
            _sync?.UpdateInfo(info);
            _main.ShowToast("New invite code made. The old one no longer works.");
        });
    }

    private async Task RenameAsync()
    {
        if (!CheckName() || PlayerName.Trim() == Settings.PlayerName) return;
        await RunAsync(async api =>
        {
            await api.RenameAsync(Settings.SquadId, _token, PlayerName.Trim());
            Settings.PlayerName = PlayerName.Trim();
            _main.SaveDesktopSettingsSoon();
            _main.ShowToast("Name changed");
        });
    }

    private async Task RunAsync(Func<SquadApi, Task> work)
    {
        var server = Online.ServerUrl(_desktop);
        if (server.Length == 0)
        {
            Fail("Add the address of Clip Engine's online service first.");
            return;
        }
        IsBusy = true;
        Message = null;
        try
        {
            await work(_api ?? new SquadApi(server));
        }
        catch (SquadException e)
        {
            if (e.NotAMember && InSquad) OnRejected();
            else Fail(e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Fail($"Couldn't reach the squad service: {e.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string text)
    {
        MessageIsError = true;
        Message = text;
    }

    private void Connect(string? token = null)
    {
        DisposeSync();
        token ??= Online.Unprotect(Settings.Token);
        var server = Online.ServerUrl(_desktop);
        if (string.IsNullOrEmpty(token) || server.Length == 0)
        {
            Fail(server.Length == 0 ? "Add the address of Clip Engine's online service to connect to your squad."
                                    : "This PC's squad sign-in couldn't be read. Leave the squad and join again.");
            return;
        }
        _token = token;
        _api = new SquadApi(server);
        var channel = new SquadConnection(server, Settings.SquadId, token);
        var sync = new SquadSync(Settings, channel, Index, _main.SendEngineCommand, ReadClip, post: a => Dispatcher.UIThread.Post(a))
        {
            Server = server,
            BeforeSeconds = _before,
            AfterSeconds = _after,
        };
        sync.Changed += RaiseSquad;
        sync.LibraryChanged += RefreshLibrarySoon;
        sync.Activity += text => _main.AddActivity(ActivityKind.Info, text);
        sync.MembersChanged += text => _main.Notify(NoticeKind.Squad, "Squad", text, "squad");
        sync.UploadWanted += QueueUpload;
        sync.Rejected += OnRejected;
        _sync = sync;
        sync.Start();
        if (_main.RecordingLabel is { } label) sync.OnRecordingStarted(label);
        _tick.Start();
        RefreshLibrarySoon();
        // Views this PC was asked for, or should have sent, before the app last closed.
        foreach (var view in Index.Views.Where(v => !v.Uploaded && SquadPlan.UploadsByItself(Settings.Uploads, v.Type, false)
                                                   && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - v.MomentAt < 86_400_000))
            QueueUpload(view);
    }

    // The preview tool: a squad over a channel it plays the service's side of, and a library.
    internal void ConnectForPreview(ISquadChannel channel, IEnumerable<SquadMoment> moments)
    {
        DisposeSync();
        _token = "preview";
        var sync = new SquadSync(Settings, channel, Index, _ => true, _ => null) { Server = Online.ServerUrl(_desktop) };
        sync.Changed += RaiseSquad;
        _sync = sync;
        sync.Start();
        Moments.Clear();
        foreach (var moment in moments) Moments.Add(new SquadMomentRow(moment, this));
        RaiseLibrary();
        RaiseSquad();
    }

    internal SquadSync? Sync => _sync;

    private void OnRejected()
    {
        var name = SquadName;
        Disconnect();
        Fail($"You're no longer in {name}. You may have been removed, or the squad was deleted. Join again with a new invite.");
    }

    private void Disconnect()
    {
        DisposeSync();
        Settings.Leave();
        Index.Clear();
        _uploads.Clear();
        _queued.Clear();
        _held.Clear();
        Moments.Clear();
        Members.Clear();
        _next = null;
        _main.SaveDesktopSettingsNow();
        RaiseSquad();
        RaiseLibrary();
    }

    private void DisposeSync()
    {
        _tick.Stop();
        _sync?.Dispose();
        _sync = null;
        _api = null;
    }

    private static LocalClip? ReadClip(string path)
    {
        var metadata = ClipMetadataFile.Read(path);
        return metadata is null || metadata.UtcStart <= 0
            ? null
            : new LocalClip(path, metadata.UtcStart, metadata.Seconds, metadata.Trigger, metadata.Favorite);
    }

    private void SetUploads(string policy)
    {
        if (Settings.Uploads == policy) return;
        Settings.Uploads = policy;
        _main.SaveDesktopSettingsSoon();
        Raise(nameof(UploadsHighlights));
        Raise(nameof(UploadsAll));
        Raise(nameof(UploadsOff));
        Raise(nameof(UploadsCaption));
    }

    private void SaveShareMoments()
    {
        if (!_loaded) return;
        Settings.ShareMoments = ShareMoments.Where(t => t.IsOn).Select(t => t.Name).ToList();
        _main.SaveDesktopSettingsSoon();
    }

    private async Task CopyAsync(string text, string done)
    {
        if (text.Length == 0) return;
        if (_main.Dialogs is { } dialogs && await dialogs.CopyTextAsync(text)) _main.ShowToast(done);
        else _main.ShowToast("Couldn't put it on the clipboard.", isError: true);
    }

    private void RaiseSquad()
    {
        var sync = _sync;
        var me = Settings.MemberId;
        var with = sync?.PlayingWithMe().Select(m => m.Id).ToHashSet() ?? [];
        var owner = IsOwner;
        Members.Clear();
        if (sync is not null)
        {
            foreach (var m in sync.Members.OrderByDescending(m => m.Id == me).ThenByDescending(m => m.Online)
                         .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
                Members.Add(new SquadMemberRow(m, m.Id == me, with.Contains(m.Id), sync.IsUnlinked(m.Id), owner && m.Id != me, this));
        }
        Raise(nameof(InSquad));
        Raise(nameof(NotInSquad));
        Raise(nameof(SquadName));
        Raise(nameof(InviteCode));
        Raise(nameof(HasInvite));
        Raise(nameof(InviteLink));
        Raise(nameof(IsOwner));
        Raise(nameof(IsConnected));
        Raise(nameof(ConnectionText));
        Raise(nameof(MembersCaption));
        Raise(nameof(TogetherTitle));
        Raise(nameof(TogetherDetail));
        Raise(nameof(IsTogether));
        Raise(nameof(StorageText));
        Raise(nameof(LeaveWarning));
        NewInviteCommand.NotifyCanExecuteChanged();
        _main.RaiseSquadStatus();
    }

    // For the sidebar: "Squad · 2 playing".
    internal string? SidebarText
    {
        get
        {
            if (_sync is null) return null;
            var with = _sync.PlayingWithMe().Count;
            return with > 0 ? $"With {with} squadmate{(with == 1 ? "" : "s")}" : null;
        }
    }

    internal void Unlink(SquadMemberRow row, bool undo) => _sync?.Unlink(row.Id, undo);

    internal async Task RemoveAsync(SquadMemberRow row)
    {
        await RunAsync(async api =>
        {
            await api.RemoveAsync(Settings.SquadId, _token, row.Id);
            _main.ShowToast($"{row.Name} was removed from the squad");
        });
    }

    // ---- the library ----

    private void RefreshLibrarySoon()
    {
        _libraryTimer.Stop();
        _libraryTimer.Start();
    }

    private async Task LoadLibraryAsync(bool more)
    {
        var api = _api;
        if (api is null || !InSquad || _libraryLoading || (more && _next is null)) return;
        _libraryLoading = true;
        try
        {
            var (moments, next) = await api.LibraryAsync(Settings.SquadId, _token, more ? _next : null);
            if (!ReferenceEquals(api, _api)) return;
            if (!more) Moments.Clear();
            foreach (var moment in moments) Moments.Add(new SquadMomentRow(moment, this));
            _next = next;
            LibraryMessage = null;
        }
        catch (SquadException e)
        {
            if (e.NotAMember) OnRejected();
            else LibraryMessage = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            LibraryMessage = $"Couldn't load the shared library: {e.Message}";
        }
        finally
        {
            _libraryLoading = false;
            RaiseLibrary();
        }
    }

    private void RaiseLibrary()
    {
        Raise(nameof(HasMoments));
        Raise(nameof(HasNoMoments));
        Raise(nameof(HasMore));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    internal string MemberId => Settings.MemberId;

    // This PC's copy of a view: its own clip, or a squadmate's downloaded before.
    internal string? LocalPath(string moment, SquadView view)
    {
        if (view.Mine) return Index.Find(moment)?.Path is { } own && File.Exists(own) ? own : null;
        var cached = CachePath(moment, view.Member);
        return File.Exists(cached) ? cached : null;
    }

    private static string CachePath(string moment, string member) => Path.Combine(CacheDir, $"{moment}_{member}.mp4");

    // Every view of a moment that can be played here, downloading squadmates' as needed, the
    // one who made the play first.
    internal async Task<IReadOnlyList<(SquadView View, string Path)>> GatherAsync(SquadMomentRow row)
    {
        var found = new List<(SquadView, string)>();
        var api = _api;
        var views = row.Moment.Views.OrderByDescending(v => v.Member == row.Moment.By).ThenByDescending(v => v.Mine).ToList();
        foreach (var view in views)
        {
            if (found.Count == MaxTiles) break;
            if (LocalPath(row.Id, view) is { } path)
            {
                found.Add((view, path));
                continue;
            }
            if (view.Mine || !view.Uploaded || api is null) continue;
            var file = CachePath(row.Id, view.Member);
            try
            {
                _main.ShowToast($"Getting {view.Name}'s view…");
                if (await api.DownloadAsync(Settings.SquadId, _token, row.Id, view.Member, "video", file)) found.Add((view, file));
            }
            catch (Exception e) when (e is SquadException or HttpRequestException or IOException or TaskCanceledException
                                          or UnauthorizedAccessException)
            {
                _main.AddActivity(ActivityKind.Warning, $"Couldn't download {view.Name}'s view: {e.Message}");
            }
        }
        return found;
    }

    internal async Task WatchAsync(SquadMomentRow row)
    {
        var views = await GatherAsync(row);
        if (views.Count == 0)
        {
            _main.ShowToast("None of the views can be played yet. Ask your squad to send theirs.", isError: true);
            return;
        }
        _main.ShowPage(new SquadPlayerViewModel(_main, this, row, views, _main.CurrentPage));
    }

    // Cuts the moment into one clip: the play from the view of whoever made it, then a replay
    // from each teammate's. Saved with the game's clips.
    internal async Task MakeHighlightAsync(SquadMomentRow row)
    {
        if (IsMakingHighlight)
        {
            _main.ShowToast("Still making the last highlight…");
            return;
        }
        if (!OperatingSystem.IsWindows() || !File.Exists(AppPaths.EngineExe))
        {
            _main.ShowToast("Making a highlight needs clip_engine.exe next to the app.", isError: true);
            return;
        }
        IsMakingHighlight = true;
        try
        {
            var views = await GatherAsync(row);
            var paths = views.ToDictionary(v => v.View.Member, v => v.Path);
            var parts = SquadPlan.Highlight(views.Select(v => v.View).ToList(), row.Moment.By, row.Moment.At);
            if (parts.Count == 0)
            {
                _main.ShowToast("None of the views show the moment, so there's nothing to cut.", isError: true);
                return;
            }
            var output = HighlightPath(row);
            var arguments = new List<string> { "--montage", output };
            foreach (var part in parts)
            {
                arguments.Add("--part");
                arguments.Add(string.Create(CultureInfo.InvariantCulture, $"{paths[part.View.Member]}|{part.From:0.00}|{part.To:0.00}"));
            }
            arguments.Add("--moment");
            arguments.Add(row.Id);

            _main.ShowToast("Making the squad highlight…");
            string? saved = null;
            var (exitCode, failure) = await EngineCommand.RunAsync(arguments, e =>
            {
                if (e is MontageProgress p) Dispatcher.UIThread.Post(() => _main.ShowToast($"Making the squad highlight… {p.Percent}%"));
                if (e is HighlightSaved h) saved = h.Path;
            });
            if (exitCode != 0 || saved is null)
            {
                _main.ShowToast(failure ?? "The highlight couldn't be made.", isError: true);
                return;
            }
            _main.AddActivity(ActivityKind.Clip, $"Made a squad highlight of {row.Title.ToLowerInvariant()} from {parts.Count} views");
            await _main.OpenSavedClipAsync(saved, "Squad highlight saved with your clips");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _main.ShowToast($"Couldn't make the highlight: {e.Message}", isError: true);
        }
        finally
        {
            IsMakingHighlight = false;
        }
    }

    private string HighlightPath(SquadMomentRow row)
    {
        var game = SafeName(row.Moment.Game.Length > 0 ? row.Moment.Game : "Squad");
        var when = DateTimeOffset.FromUnixTimeMilliseconds(row.Moment.At).LocalDateTime;
        var folder = Path.Combine(_main.ClipsFolder, game);
        Directory.CreateDirectory(folder);
        var stem = $"{game}_{when:yyyy-MM-dd_HH-mm-ss}_squad";
        var path = Path.Combine(folder, stem + ".mp4");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}_{n}.mp4");
        return path;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || c is '|' or ':' ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "Squad" : safe;
    }

    internal async Task StarAsync(SquadMomentRow row)
    {
        if (_api is null) return;
        var star = !row.IsStarred;
        row.IsStarred = star;
        try
        {
            await _api.StarAsync(Settings.SquadId, _token, row.Id, star);
            // A starred moment's views go up so the squad keeps them.
            if (star && Index.Find(row.Id) is { Uploaded: false } view && SquadPlan.UploadsOnRequest(Settings.Uploads)) QueueUpload(view);
        }
        catch (Exception e) when (e is SquadException or HttpRequestException or TaskCanceledException)
        {
            row.IsStarred = !star;
            _main.ShowToast($"Couldn't star it: {e.Message}", isError: true);
        }
    }

    internal void Ask(SquadMomentRow row)
    {
        if (_sync is not { Connected: true } sync)
        {
            _main.ShowToast("Not connected to the squad right now.", isError: true);
            return;
        }
        var asked = row.Moment.Views.Where(v => !v.Mine && !v.Uploaded).ToList();
        foreach (var view in asked) sync.Want(row.Id, view.Member);
        _main.ShowToast(asked.Count == 0 ? "Every view is already in the library."
            : $"Asked {Names(asked.Select(v => v.Name).ToList())} to send their view. It shows here once it's up.");
    }

    // ---- uploading this player's views ----

    private void QueueUpload(LocalView view)
    {
        if (!_queued.Add(view.Moment)) return;
        _uploads.Enqueue(view);
        if (!_uploadRunning) _ = RunUploadsAsync();
    }

    private void ReleaseHeldUploads()
    {
        if (_held.Count == 0) return;
        foreach (var view in _held) _uploads.Enqueue(view);
        _held.Clear();
        if (!_uploadRunning) _ = RunUploadsAsync();
    }

    private async Task RunUploadsAsync()
    {
        _uploadRunning = true;
        try
        {
            while (_uploads.TryDequeue(out var view))
            {
                var api = _api;
                if (api is null || !InSquad) break;
                if (!File.Exists(view.Path))
                {
                    _queued.Remove(view.Moment);
                    continue;
                }
                // Making a smaller copy takes the GPU's encoder, so it waits for the match to end.
                if (_main.IsRecording && new FileInfo(view.Path).Length > Settings.UploadLimitMb * 980_000L)
                {
                    _held.Add(view);
                    continue;
                }
                UploadStatus = $"Sending your view of a {SquadSync.EventLabel(view.Type)} to the squad…";
                try
                {
                    await UploadAsync(api, view);
                    Index.MarkUploaded(view.Moment);
                    RefreshLibrarySoon();
                }
                catch (Exception e) when (e is SquadException or ShareFailedException or HttpRequestException or IOException
                                              or TaskCanceledException or UnauthorizedAccessException)
                {
                    _main.AddActivity(ActivityKind.Warning, $"Couldn't send a squad view: {e.Message}");
                }
                _queued.Remove(view.Moment);
            }
        }
        finally
        {
            _uploadRunning = false;
            UploadStatus = null;
        }
    }

    private async Task UploadAsync(SquadApi api, LocalView view)
    {
        var (copy, _) = await ClipSharing.MakeShareCopyAsync(view.Path, Settings.UploadLimitMb, new Progress<int>(_ => { }));
        await api.UploadAsync(Settings.SquadId, _token, view.Moment, "video", copy, "video/mp4");
        if (await ThumbnailFile.MakeAsync(view.Path) is not { } thumb) return;
        try
        {
            await api.UploadAsync(Settings.SquadId, _token, view.Moment, "thumb", thumb, "image/png");
        }
        catch (SquadException)
        {
            // The video is what matters.
        }
        finally
        {
            ThumbnailFile.Delete(thumb);
        }
    }

    private static void CleanCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (var file in Directory.EnumerateFiles(CacheDir))
                if (DateTime.UtcNow - File.GetLastAccessTimeUtc(file) > KeepCache) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    public void Dispose()
    {
        _libraryTimer.Stop();
        DisposeSync();
    }
}

public sealed class SquadMemberRow
{
    internal SquadMemberRow(SquadMemberState member, bool isYou, bool playingWithYou, bool unlinked, bool canRemove, SquadViewModel owner)
    {
        Id = member.Id;
        Name = isYou ? $"{member.Name} (you)" : member.Name;
        Initial = Format.Initials(member.Name);
        IsOnline = member.Online;
        IsYou = isYou;
        IsOwner = member.Owner;
        PlayingWithYou = playingWithYou;
        IsUnlinked = unlinked;
        CanRemove = canRemove;
        Status = !member.Online ? "Offline"
            : member.Game.Length > 0 ? (playingWithYou ? $"Playing {member.Game} with you" : $"Playing {member.Game}")
            : "Online";
        UnlinkCommand = new RelayCommand(() => owner.Unlink(this, false));
        LinkCommand = new RelayCommand(() => owner.Unlink(this, true));
        RemoveCommand = new RelayCommand(() => _ = owner.RemoveAsync(this));
    }

    public string Id { get; }
    public string Name { get; }
    public string Initial { get; }
    public string Status { get; }
    public bool IsOnline { get; }
    public bool IsYou { get; }
    public bool IsOwner { get; }
    public bool PlayingWithYou { get; }
    public bool IsUnlinked { get; }
    public bool CanUnlink => PlayingWithYou && !IsYou;
    public bool CanLink => IsUnlinked && !IsYou;
    public bool CanRemove { get; }
    public RelayCommand UnlinkCommand { get; }
    public RelayCommand LinkCommand { get; }
    public RelayCommand RemoveCommand { get; }
}

public sealed class SquadMomentRow : ObservableObject
{
    private bool _isStarred;

    internal SquadMomentRow(SquadMoment moment, SquadViewModel owner)
    {
        Moment = moment;
        _isStarred = moment.Star;
        var mine = moment.By == owner.MemberId;
        var label = SquadSync.EventLabel(moment.Type);
        Title = mine ? $"Your {label}" : $"{moment.ByName}'s {label}";
        Brush = EventKinds.Brush(moment.Type);
        When = Format.When(DateTimeOffset.FromUnixTimeMilliseconds(moment.At).LocalDateTime);
        Views = moment.Views.Select(v => new SquadViewChip(v.Mine ? "You" : v.Name,
            owner.LocalPath(moment.Id, v) is not null ? "here" : v.Uploaded ? "in the library" : "not sent yet",
            owner.LocalPath(moment.Id, v) is not null || v.Uploaded)).ToList();
        Playable = Views.Count(v => v.Ready);
        CanAsk = moment.Views.Any(v => !v.Mine && !v.Uploaded);
        WatchCommand = new RelayCommand(() => _ = owner.WatchAsync(this));
        HighlightCommand = new RelayCommand(() => _ = owner.MakeHighlightAsync(this));
        StarCommand = new RelayCommand(() => _ = owner.StarAsync(this));
        AskCommand = new RelayCommand(() => owner.Ask(this));
    }

    internal SquadMoment Moment { get; }
    public string Id => Moment.Id;
    public string Title { get; }
    public string Game => Moment.Game;
    public string Text => Moment.Text;
    public bool HasText => Moment.Text.Length > 0;
    public string When { get; }
    public Avalonia.Media.IBrush Brush { get; }
    public IReadOnlyList<SquadViewChip> Views { get; }
    public int Playable { get; }
    public string ViewsText => Views.Count == 1 ? "1 view" : $"{Views.Count} views";
    public bool CanWatch => Playable > 0;
    public bool CanHighlight => Playable > 1;
    public bool CanAsk { get; }
    public string Caption => $"{Game} · {When} · {ViewsText}";

    public bool IsStarred
    {
        get => _isStarred;
        set { if (Set(ref _isStarred, value)) Raise(nameof(StarTip)); }
    }

    public string StarTip => IsStarred ? "Starred: kept when the library is full" : "Star it, so it's kept when the library is full";

    public RelayCommand WatchCommand { get; }
    public RelayCommand HighlightCommand { get; }
    public RelayCommand StarCommand { get; }
    public RelayCommand AskCommand { get; }
}

public sealed record SquadViewChip(string Name, string State, bool Ready);
