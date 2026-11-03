using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Cloud;
using ClipEngine.Desktop.Services.Squad;

namespace ClipEngine.Desktop.ViewModels;

// The Cloud page: an account on Clip Engine's online service, short share links for clips, the
// public profile with the clips picked for it, and what's in the cloud library.
public sealed class CloudViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DesktopSettings _desktop;
    private readonly Queue<(ClipItemViewModel Clip, bool Copy)> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private CloudApi? _api;
    private string _token = "";
    private CloudAccount? _account;
    private string _nameInput;
    private string _codeInput = "";
    private string _handleInput;
    private bool _busy;
    private string? _message;
    private bool _messageIsError;
    private string? _linkCode;
    private string? _uploadStatus;
    private bool _uploading;
    private bool _confirmSignOut;

    internal CloudViewModel(MainViewModel main, DesktopSettings desktop)
    {
        _main = main;
        _desktop = desktop;
        _nameInput = Settings.Name.Length > 0 ? Settings.Name : desktop.Squad.PlayerName.Length > 0 ? desktop.Squad.PlayerName : Environment.UserName;
        _handleInput = Settings.Handle;

        CreateCommand = new RelayCommand(() => _ = CreateAsync(), () => !IsBusy);
        SignInCommand = new RelayCommand(() => _ = SignInAsync(), () => !IsBusy);
        SignOutCommand = new RelayCommand(() => ConfirmSignOut = true);
        CancelSignOutCommand = new RelayCommand(() => ConfirmSignOut = false);
        ConfirmSignOutCommand = new RelayCommand(SignOut);
        SaveProfileCommand = new RelayCommand(() => _ = SaveProfileAsync(), () => !IsBusy);
        CopyProfileCommand = new RelayCommand(() => _ = CopyAsync(ProfileLink, "Profile link copied"));
        OpenProfileCommand = new RelayCommand(() => { if (HasProfile) Shell.Open(ProfileLink); });
        MakeLinkCodeCommand = new RelayCommand(() => _ = MakeLinkCodeAsync(), () => !IsBusy);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());

        if (main.IsLive && Settings.SignedIn) Start();
    }

    internal CloudSettings Settings => _desktop.Cloud;

    public ObservableCollection<CloudClipRow> Clips { get; } = [];

    public RelayCommand CreateCommand { get; }
    public RelayCommand SignInCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public RelayCommand CancelSignOutCommand { get; }
    public RelayCommand ConfirmSignOutCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand CopyProfileCommand { get; }
    public RelayCommand OpenProfileCommand { get; }
    public RelayCommand MakeLinkCodeCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public bool SignedIn => Settings.SignedIn;
    public bool NotSignedIn => !Settings.SignedIn;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            CreateCommand.NotifyCanExecuteChanged();
            SignInCommand.NotifyCanExecuteChanged();
            SaveProfileCommand.NotifyCanExecuteChanged();
            MakeLinkCodeCommand.NotifyCanExecuteChanged();
        }
    }

    public string? Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message is not null;
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    public string NameInput { get => _nameInput; set => Set(ref _nameInput, value ?? ""); }
    public string CodeInput { get => _codeInput; set => Set(ref _codeInput, value ?? ""); }
    public string HandleInput { get => _handleInput; set => Set(ref _handleInput, value ?? ""); }

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

    public string AccountName => _account?.Name ?? Settings.Name;
    public string PlanLabel => _account is null ? "" : $"{_account.PlanLabel} plan";
    public string StorageText => _account is { Limit: > 0 } a ? $"{Format.Bytes(a.Used)} of {Format.Bytes(a.Limit)} used" : "";
    public double StorageFraction => _account is { Limit: > 0 } a ? Math.Clamp((double)a.Used / a.Limit, 0, 1) * 100 : 0;
    public string ClipsText => _account is null ? "" : _account.Clips == 1 ? "1 clip in the cloud" : $"{_account.Clips} clips in the cloud";

    public string ProfileLink => _api is not null && (_account?.Handle ?? Settings.Handle) is { Length: > 0 } handle ? _api.ProfileLink(handle) : "";
    public bool HasProfile => ProfileLink.Length > 0;

    public string? LinkCode { get => _linkCode; private set { if (Set(ref _linkCode, value)) Raise(nameof(HasLinkCode)); } }
    public bool HasLinkCode => _linkCode is not null;

    public bool DefaultUnlisted { get => Settings.DefaultVisibility == "unlisted"; set { if (value) SetDefault("unlisted"); } }
    public bool DefaultPublic { get => Settings.DefaultVisibility == "public"; set { if (value) SetDefault("public"); } }
    public bool DefaultPrivate { get => Settings.DefaultVisibility == "private"; set { if (value) SetDefault("private"); } }

    public string DefaultCaption => Settings.DefaultVisibility switch
    {
        "public" => "Anyone with the link can watch, and the clip is listed on your profile.",
        "private" => "Kept in the cloud as a backup. Its link plays for no one.",
        _ => "Anyone with the link can watch. It isn't listed anywhere.",
    };

    public bool SyncStarred
    {
        get => Settings.SyncStarred;
        set
        {
            if (Settings.SyncStarred == value) return;
            Settings.SyncStarred = value;
            _main.SaveDesktopSettingsSoon();
            Raise();
            if (value) QueueStarred();
        }
    }

    public string? UploadStatus { get => _uploadStatus; private set { if (Set(ref _uploadStatus, value)) Raise(nameof(IsUploading)); } }
    public bool IsUploading => _uploadStatus is not null;
    public bool HasClips => Clips.Count > 0;
    public bool HasNoClips => Clips.Count == 0;

    public bool ConfirmSignOut
    {
        get => _confirmSignOut;
        private set { if (Set(ref _confirmSignOut, value)) Raise(nameof(NotConfirmingSignOut)); }
    }

    public bool NotConfirmingSignOut => !_confirmSignOut;

    // The preview tool: an account and its clips, without the service.
    internal void ShowSample(string server, CloudAccount account, IEnumerable<CloudClip> clips)
    {
        _api = new CloudApi(server);
        _account = account;
        Settings.AccountId = account.Id;
        Settings.Token = "preview";
        Settings.Name = account.Name;
        Settings.Handle = account.Handle ?? "";
        HandleInput = Settings.Handle;
        Clips.Clear();
        foreach (var clip in clips) Clips.Add(new CloudClipRow(clip, _api, this));
        RaiseAccount();
    }

    // ---- signing in ----

    private async Task CreateAsync()
    {
        if (NameInput.Trim().Length is 0 or > 32)
        {
            Fail("Pick a name of up to 32 characters. It's shown on your clips and profile.");
            return;
        }
        await RunAsync(async api =>
        {
            var (token, account) = await api.CreateAsync(NameInput.Trim(), Online.DeviceId(_desktop));
            SignedInAs(token, account);
        });
    }

    private async Task SignInAsync()
    {
        if (CodeInput.Trim().Length == 0)
        {
            Fail("On a PC that's signed in, press \"Link another PC\" on this page and type the code here.");
            return;
        }
        await RunAsync(async api =>
        {
            var (token, account) = await api.SignInAsync(CodeInput.Trim(), Online.DeviceId(_desktop));
            SignedInAs(token, account);
        });
    }

    private void SignedInAs(string token, CloudAccount account)
    {
        Settings.AccountId = account.Id;
        Settings.Token = Online.Protect(token);
        Settings.Name = account.Name;
        Settings.Handle = account.Handle ?? "";
        Settings.Uploaded.Clear();
        _main.SaveDesktopSettingsNow();
        CodeInput = "";
        Message = null;
        Start(token);
        _main.AddActivity(ActivityKind.Info, $"Signed in to the cloud library as {account.Name}");
    }

    private void SignOut()
    {
        ConfirmSignOut = false;
        Settings.SignOut();
        _main.SaveDesktopSettingsNow();
        _api = null;
        _token = "";
        _account = null;
        _queue.Clear();
        _queued.Clear();
        Clips.Clear();
        LinkCode = null;
        Message = "Signed out. Your clips stay in the cloud; sign in again with a code from another PC.";
        MessageIsError = false;
        RaiseAccount();
    }

    private void Start(string? token = null)
    {
        token ??= Online.Unprotect(Settings.Token);
        var server = Online.ServerUrl(_desktop);
        if (string.IsNullOrEmpty(token) || server.Length == 0)
        {
            Fail(server.Length == 0 ? "Add the address of Clip Engine's online service to reach your cloud library."
                                    : "This PC's cloud sign-in couldn't be read. Sign out and in again.");
            return;
        }
        _token = token;
        _api = new CloudApi(server);
        RaiseAccount();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var api = _api;
        if (api is null) return;
        try
        {
            var (clips, account) = await api.ClipsAsync(Settings.AccountId, _token);
            if (!ReferenceEquals(api, _api)) return;
            _account = account;
            Settings.Name = account.Name;
            Settings.Handle = account.Handle ?? "";
            HandleInput = Settings.Handle;
            Clips.Clear();
            foreach (var clip in clips) Clips.Add(new CloudClipRow(clip, api, this));
            // Codes of clips removed from the cloud on another PC are dropped here.
            var codes = clips.Select(c => c.Code).ToHashSet();
            foreach (var key in Settings.Uploaded.Where(p => !codes.Contains(p.Value)).Select(p => p.Key).ToList()) Settings.Uploaded.Remove(key);
            RaiseAccount();
            if (Settings.SyncStarred) QueueStarred();
        }
        catch (SquadException e) when (e.Status is System.Net.HttpStatusCode.Unauthorized)
        {
            SignOut();
            Fail("This PC was signed out of the cloud library. Sign in again with a code from another PC.");
        }
        catch (Exception e) when (e is SquadException or HttpRequestException or TaskCanceledException)
        {
            Fail(e.Message);
        }
    }

    private async Task SaveProfileAsync()
    {
        if (_api is not { } api) return;
        await RunAsync(async _ =>
        {
            var handle = HandleInput.Trim().TrimStart('@');
            var account = await api.UpdateAccountAsync(Settings.AccountId, _token, null, handle);
            _account = account;
            Settings.Handle = account.Handle ?? "";
            _main.SaveDesktopSettingsSoon();
            RaiseAccount();
            _main.ShowToast(Settings.Handle.Length > 0 ? $"Your profile is at {ProfileLink}" : "Profile turned off");
        });
    }

    private async Task MakeLinkCodeAsync()
    {
        if (_api is not { } api) return;
        await RunAsync(async _ =>
        {
            var code = await api.LinkCodeAsync(Settings.AccountId, _token);
            LinkCode = code;
        });
    }

    private async Task RunAsync(Func<CloudApi, Task> work)
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
            await work(_api ?? new CloudApi(server));
        }
        catch (Exception e) when (e is SquadException or HttpRequestException or TaskCanceledException)
        {
            Fail(e.Message);
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

    private void SetDefault(string visibility)
    {
        if (Settings.DefaultVisibility == visibility) return;
        Settings.DefaultVisibility = visibility;
        _main.SaveDesktopSettingsSoon();
        Raise(nameof(DefaultUnlisted));
        Raise(nameof(DefaultPublic));
        Raise(nameof(DefaultPrivate));
        Raise(nameof(DefaultCaption));
    }

    internal async Task CopyAsync(string text, string done)
    {
        if (text.Length == 0) return;
        if (_main.Dialogs is { } dialogs && await dialogs.CopyTextAsync(text)) _main.ShowToast(done);
        else _main.ShowToast("Couldn't put it on the clipboard.", isError: true);
    }

    private void RaiseAccount()
    {
        Raise(nameof(SignedIn));
        Raise(nameof(NotSignedIn));
        Raise(nameof(AccountName));
        Raise(nameof(PlanLabel));
        Raise(nameof(StorageText));
        Raise(nameof(StorageFraction));
        Raise(nameof(ClipsText));
        Raise(nameof(ProfileLink));
        Raise(nameof(HasProfile));
        Raise(nameof(HasClips));
        Raise(nameof(HasNoClips));
    }

    // ---- share links ----

    // A clip's cloud link, or null when it isn't in the cloud.
    internal string? LinkFor(ClipItemViewModel clip) =>
        _api is not null && Settings.Uploaded.TryGetValue(CloudApi.LocalId(clip.Path, clip.File.Bytes), out var code) ? _api.ShareLink(code) : null;

    // Copy link on a clip: uploads it if it isn't in the cloud yet, then copies its short link.
    internal async Task CopyLinkAsync(ClipItemViewModel clip)
    {
        if (!SignedIn || _api is null)
        {
            _main.ShowToast("Sign in on the Cloud page to make share links.", isError: true);
            _main.GoToCommand.Execute(Page.Cloud);
            return;
        }
        if (LinkFor(clip) is { } link)
        {
            await CopyAsync(link, "Link copied. It plays in any browser.");
            return;
        }
        Queue(clip, copy: true);
    }

    internal void OnFavoriteChanged(ClipItemViewModel clip)
    {
        if (SignedIn && Settings.SyncStarred && clip.IsFavorite && LinkFor(clip) is null) Queue(clip, copy: false);
    }

    private void QueueStarred()
    {
        foreach (var clip in _main.Clips.Where(c => c.IsFavorite && LinkFor(c) is null)) Queue(clip, copy: false);
    }

    private void Queue(ClipItemViewModel clip, bool copy)
    {
        if (!_queued.Add(clip.Path))
        {
            if (copy) _main.ShowToast("That clip is already on its way up. The link is copied when it's done.");
            return;
        }
        _queue.Enqueue((clip, copy));
        if (copy) _main.ShowToast("Uploading the clip for its link…");
        if (!_uploading) _ = RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        _uploading = true;
        try
        {
            while (_queue.TryDequeue(out var job))
            {
                var api = _api;
                if (api is null) break;
                try
                {
                    var code = await UploadAsync(api, job.Clip);
                    if (job.Copy) await CopyAsync(api.ShareLink(code), "Link copied. It plays in any browser, with the moments marked.");
                }
                catch (Exception e) when (e is SquadException or ShareFailedException or HttpRequestException or IOException
                                              or TaskCanceledException or UnauthorizedAccessException)
                {
                    if (job.Copy) _main.ShowToast($"Couldn't upload the clip: {e.Message}", isError: true);
                    else _main.AddActivity(ActivityKind.Warning, $"Couldn't sync a starred clip to the cloud: {e.Message}");
                }
                _queued.Remove(job.Clip.Path);
            }
        }
        finally
        {
            _uploading = false;
            UploadStatus = null;
            _ = RefreshAsync();
        }
    }

    private async Task<string> UploadAsync(CloudApi api, ClipItemViewModel clip)
    {
        var metadata = clip.File.Metadata;
        var seconds = clip.File.Duration?.TotalSeconds ?? metadata?.Seconds ?? 0;
        var recorded = metadata?.UtcStart is > 0 ? metadata.UtcStart : new DateTimeOffset(clip.File.Created).ToUnixTimeMilliseconds();
        var localId = CloudApi.LocalId(clip.Path, clip.File.Bytes);
        var markers = new JsonArray();
        foreach (var e in clip.Events) markers.Add(new JsonObject { ["type"] = e.Type, ["time"] = e.Time, ["text"] = e.Text });
        var body = new JsonObject
        {
            ["localId"] = localId,
            ["title"] = clip.HasSummary ? clip.Summary : clip.Title,
            ["game"] = clip.Game,
            ["recorded"] = recorded,
            ["seconds"] = Math.Max(seconds, 0.1),
            ["visibility"] = Settings.DefaultVisibility,
            ["markers"] = markers,
        };
        var cloud = await api.AddClipAsync(Settings.AccountId, _token, body);
        if (!cloud.Uploaded)
        {
            var limit = Math.Clamp(Settings.UploadLimitMb, 8, 98);
            UploadStatus = $"Making a copy of {clip.Title} under {limit} MB…";
            var (copy, _) = await ClipSharing.MakeShareCopyAsync(clip.Path, limit,
                new Progress<int>(p => UploadStatus = $"Making a copy of {clip.Title} under {limit} MB… {p}%"));
            UploadStatus = $"Uploading {clip.Title}…";
            await api.UploadAsync(Settings.AccountId, _token, cloud.Id, "video", copy, "video/mp4");
            if (await ThumbnailFile.MakeAsync(clip.Path, 640) is { } thumb)
            {
                try
                {
                    await api.UploadAsync(Settings.AccountId, _token, cloud.Id, "thumb", thumb, "image/png");
                }
                catch (SquadException)
                {
                }
                finally
                {
                    ThumbnailFile.Delete(thumb);
                }
            }
            _main.AddActivity(ActivityKind.Clip, $"Uploaded a {clip.Game} clip to the cloud");
        }
        Settings.Uploaded[localId] = cloud.Code;
        _main.SaveDesktopSettingsSoon();
        return cloud.Code;
    }

    internal async Task UpdateAsync(CloudClipRow row, JsonObject changes)
    {
        if (_api is null) return;
        try
        {
            row.Clip = await _api.UpdateClipAsync(Settings.AccountId, _token, row.Clip.Id, changes);
        }
        catch (Exception e) when (e is SquadException or HttpRequestException or TaskCanceledException)
        {
            _main.ShowToast($"Couldn't change the clip: {e.Message}", isError: true);
            _ = RefreshAsync();
        }
    }

    internal async Task DeleteAsync(CloudClipRow row)
    {
        if (_api is null) return;
        try
        {
            await _api.DeleteClipAsync(Settings.AccountId, _token, row.Clip.Id);
            Clips.Remove(row);
            foreach (var key in Settings.Uploaded.Where(p => p.Value == row.Clip.Code).Select(p => p.Key).ToList()) Settings.Uploaded.Remove(key);
            _main.SaveDesktopSettingsSoon();
            _main.ShowToast("Removed from the cloud. Its link no longer plays.");
            _ = RefreshAsync();
        }
        catch (Exception e) when (e is SquadException or HttpRequestException or TaskCanceledException)
        {
            _main.ShowToast($"Couldn't remove the clip: {e.Message}", isError: true);
        }
    }
}

public sealed class CloudClipRow : ObservableObject
{
    private readonly CloudApi _api;
    private readonly CloudViewModel _owner;
    private CloudClip _clip;
    private bool _confirmDelete;

    internal CloudClipRow(CloudClip clip, CloudApi api, CloudViewModel owner)
    {
        _clip = clip;
        _api = api;
        _owner = owner;
        CopyLinkCommand = new RelayCommand(() => _ = owner.CopyAsync(Link, "Link copied"));
        OpenCommand = new RelayCommand(() => Shell.Open(Link));
        FeatureCommand = new RelayCommand(() => _ = owner.UpdateAsync(this, new JsonObject { ["featured"] = !Clip.Featured }));
        DeleteCommand = new RelayCommand(() => ConfirmDelete = true);
        CancelDeleteCommand = new RelayCommand(() => ConfirmDelete = false);
        ConfirmDeleteCommand = new RelayCommand(() => _ = owner.DeleteAsync(this));
    }

    internal CloudClip Clip
    {
        get => _clip;
        set
        {
            _clip = value;
            Refresh();
        }
    }

    public string Title => Clip.Title.Length > 0 ? Clip.Title : Clip.Game;
    public string Link => _api.ShareLink(Clip.Code);
    public string Caption
    {
        get
        {
            var when = Format.When(DateTimeOffset.FromUnixTimeMilliseconds(Clip.Recorded).LocalDateTime);
            var views = Clip.Views == 1 ? "1 view" : $"{Clip.Views} views";
            return Clip.Uploaded ? $"{Clip.Game} · {when} · {views} · {Format.Bytes(Clip.Size)}" : $"{Clip.Game} · {when} · not uploaded yet";
        }
    }

    public bool IsUnlisted { get => Clip.Visibility == "unlisted"; set { if (value) SetVisibility("unlisted"); } }
    public bool IsPublic { get => Clip.Visibility == "public"; set { if (value) SetVisibility("public"); } }
    public bool IsPrivate { get => Clip.Visibility == "private"; set { if (value) SetVisibility("private"); } }
    public bool IsFeatured => Clip.Featured;
    public string FeatureTip => Clip.Featured ? "Pinned to the top of your profile" : "Pin to the top of your profile (public clips only)";
    public bool ConfirmDelete { get => _confirmDelete; private set { if (Set(ref _confirmDelete, value)) Raise(nameof(NotConfirmingDelete)); } }
    public bool NotConfirmingDelete => !_confirmDelete;

    public RelayCommand CopyLinkCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand FeatureCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }
    public RelayCommand ConfirmDeleteCommand { get; }

    private void SetVisibility(string visibility)
    {
        if (Clip.Visibility == visibility) return;
        _clip = _clip with { Visibility = visibility };
        Refresh();
        _ = _owner.UpdateAsync(this, new JsonObject { ["visibility"] = visibility });
    }

    internal void Refresh()
    {
        Raise(nameof(Title));
        Raise(nameof(Caption));
        Raise(nameof(IsUnlisted));
        Raise(nameof(IsPublic));
        Raise(nameof(IsPrivate));
        Raise(nameof(IsFeatured));
        Raise(nameof(FeatureTip));
    }
}
