using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// Notifications: pop-ups for notable events (a friend request, a clip saved, the subscription about to renew, a new
// version...) and the history of them, which the bell in the sidebar opens and Home's recent events can show. Each kind
// can be turned off in Settings, or all of them at once; the history keeps everything either way.
public sealed class NotificationsViewModel : ObservableObject
{
    private static readonly TimeSpan PopUpFor = TimeSpan.FromSeconds(6);
    private const int MaxPopUps = 3;
    private const int LatestCount = 4;

    private readonly DesktopSettings _desktop;
    private readonly string? _file;
    private readonly Action _saveSettings;
    // Made when first needed, so the tests never touch the dispatcher.
    private DispatcherTimer? _popUpTimer;
    private DispatcherTimer? _saveTimer;
    private bool _isOpen;

    // file is null in the preview tool and tests, which keep the history in memory.
    internal NotificationsViewModel(DesktopSettings desktop, string? file, Action saveSettings)
    {
        _desktop = desktop;
        _file = file;
        _saveSettings = saveSettings;
        if (file is not null)
        {
            foreach (var record in NoticeHistory.Load(file)) History.Add(new NoticeItem(record, this));
        }
        Filters = NoticeKinds.All.Select((k, i) => new NoticeFilter(k, i == 0, this)).ToList();

        TogglePanelCommand = new RelayCommand(() => IsOpen = !IsOpen);
        MarkAllReadCommand = new RelayCommand(MarkAllRead, () => Unread > 0);
        ClearCommand = new RelayCommand(Clear, () => History.Count > 0);
        RefreshLatest();
    }

    public ObservableCollection<NoticeItem> History { get; } = [];
    public ObservableCollection<NoticeItem> PopUps { get; } = [];
    // The newest few, for the side panel over the game.
    public ObservableCollection<NoticeItem> Latest { get; } = [];
    public IReadOnlyList<NoticeFilter> Filters { get; }

    public RelayCommand TogglePanelCommand { get; }
    public RelayCommand MarkAllReadCommand { get; }
    public RelayCommand ClearCommand { get; }

    // Clicking a notification: the app goes where it points (a clip, the Friends page...).
    internal event Action<NoticeItem>? Opened;

    // Every pop-up, or none.
    public bool On
    {
        get => _desktop.Notices.On;
        set
        {
            if (_desktop.Notices.On == value) return;
            _desktop.Notices.On = value;
            if (!value) PopUps.Clear();
            _saveSettings();
            Raise();
            foreach (var filter in Filters) filter.Refresh();
        }
    }

    // Pop-ups over the game too, while one is recorded.
    public bool OverGame
    {
        get => _desktop.GameNotifications;
        set
        {
            if (_desktop.GameNotifications == value) return;
            _desktop.GameNotifications = value;
            _saveSettings();
            Raise();
        }
    }

    internal bool Allows(NoticeKind kind) => _desktop.Notices.Allows(kind);

    internal bool IsKindOn(NoticeKind kind) => _desktop.Notices.IsOn(kind);

    internal void SetKind(NoticeKind kind, bool on)
    {
        if (_desktop.Notices.IsOn(kind) == on) return;
        _desktop.Notices.Set(kind, on);
        if (!on)
        {
            foreach (var item in PopUps.Where(p => p.Kind == kind).ToList()) PopUps.Remove(item);
        }
        _saveSettings();
        KindChanged?.Invoke(kind);
    }

    // A kind was turned on or off (the Friends page has its own switch for friends coming online).
    internal event Action<NoticeKind>? KindChanged;

    // The list that drops down from the bell in the sidebar. Closing it marks everything read.
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value)) return;
            if (!value) MarkAllRead();
        }
    }

    public int Unread => History.Count(n => n.IsUnread);
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 9 ? "9+" : Unread.ToString(CultureInfo.InvariantCulture);
    public bool HasHistory => History.Count > 0;
    public bool HasNoHistory => History.Count == 0;
    public string BellTip => HasUnread ? $"Notifications · {Unread} new" : "Notifications";

    // Keeps a notification in the history. It's read already when the list is open to see it come in.
    internal NoticeItem Add(NoticeKind kind, string title, string text, string? target = null, DateTimeOffset? at = null)
    {
        var record = new NoticeRecord
        {
            Time = at ?? DateTimeOffset.Now,
            Kind = NoticeKinds.Id(kind),
            Title = title,
            Text = text,
            Target = target,
            Read = _isOpen,
        };
        var item = new NoticeItem(record, this);
        History.Insert(0, item);
        while (History.Count > NoticeHistory.MaxKept) History.RemoveAt(History.Count - 1);
        Changed();
        return item;
    }

    // Shows a notification as a pop-up in the window's corner for a few seconds.
    internal void PopUp(NoticeItem item)
    {
        item.Until = DateTime.UtcNow + PopUpFor;
        PopUps.Remove(item);
        PopUps.Insert(0, item);
        while (PopUps.Count > MaxPopUps) PopUps.RemoveAt(PopUps.Count - 1);
        if (_popUpTimer is null)
        {
            _popUpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _popUpTimer.Tick += (_, _) => ExpirePopUps();
        }
        _popUpTimer.Start();
    }

    // The pointer is over the pop-ups: they stay while they're read, and go a few seconds after it leaves.
    internal void KeepPopUps()
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        foreach (var item in PopUps)
        {
            if (item.Until < until) item.Until = until;
        }
    }

    internal void Dismiss(NoticeItem item) => PopUps.Remove(item);

    internal void ClearPopUps() => PopUps.Clear();

    // After notifications were marked read or unread one by one (the preview tool does).
    internal void Recount() => Changed();

    internal void Open(NoticeItem item)
    {
        PopUps.Remove(item);
        item.IsUnread = false;
        IsOpen = false;
        Changed();
        Opened?.Invoke(item);
    }

    private void ExpirePopUps()
    {
        var now = DateTime.UtcNow;
        foreach (var item in PopUps.Where(p => p.Until <= now).ToList()) PopUps.Remove(item);
        if (PopUps.Count == 0) _popUpTimer?.Stop();
    }

    internal void MarkAllRead()
    {
        if (!History.Any(n => n.IsUnread)) return;
        foreach (var item in History) item.IsUnread = false;
        Changed();
    }

    private void Clear()
    {
        History.Clear();
        PopUps.Clear();
        Changed();
    }

    private void Changed()
    {
        RefreshLatest();
        Raise(nameof(Unread));
        Raise(nameof(HasUnread));
        Raise(nameof(UnreadText));
        Raise(nameof(HasHistory));
        Raise(nameof(HasNoHistory));
        Raise(nameof(BellTip));
        MarkAllReadCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        if (_file is null) return;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveHistory();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void RefreshLatest()
    {
        var latest = History.Take(LatestCount).ToList();
        if (latest.SequenceEqual(Latest)) return;
        Latest.Clear();
        foreach (var item in latest) Latest.Add(item);
    }

    internal void SaveHistory()
    {
        if (_file is null) return;
        try
        {
            NoticeHistory.Save(_file, History.Select(n => n.Record));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class NoticeItem : ObservableObject
{
    internal NoticeItem(NoticeRecord record, NotificationsViewModel owner)
    {
        Record = record;
        Kind = NoticeKinds.FromId(record.Kind) ?? NoticeKind.Update;
        OpenCommand = new RelayCommand(() => owner.Open(this));
        DismissCommand = new RelayCommand(() => owner.Dismiss(this));
    }


    internal NoticeRecord Record { get; }
    // While it's a pop-up: when it goes away.
    internal DateTime Until { get; set; }

    public NoticeKind Kind { get; }
    public string Title => Record.Title;
    public string Text => Record.Text;
    public bool HasText => Record.Text.Length > 0;
    public string? Target => Record.Target;

    public string KindLabel => Kind switch
    {
        NoticeKind.FriendRequest => "FRIEND REQUEST",
        NoticeKind.Friends => "FRIENDS",
        NoticeKind.Clip => "CLIP",
        NoticeKind.Moment => "MOMENT",
        NoticeKind.Squad => "SQUAD",
        NoticeKind.Subscription => "SUBSCRIPTION",
        _ => "UPDATE",
    };

    // "4:05 PM" today, "Nov 3 · 4:05 PM" before.
    public string Time
    {
        get
        {
            var local = Record.Time.ToLocalTime();
            var time = local.ToString("t", CultureInfo.CurrentCulture);
            return local.Date == DateTime.Today ? time : $"{local.ToString("MMM d", CultureInfo.CurrentCulture)} · {time}";
        }
    }

    public bool IsUnread
    {
        get => !Record.Read;
        set
        {
            if (Record.Read == !value) return;
            Record.Read = !value;
            Raise();
        }
    }

    public StreamGeometry? Icon => Avalonia.Application.Current?.FindResource(Kind switch
    {
        NoticeKind.FriendRequest or NoticeKind.Friends => "IconFriends",
        NoticeKind.Clip => "IconBolt",
        NoticeKind.Moment => "IconSparkle",
        NoticeKind.Squad => "IconSquad",
        NoticeKind.Subscription => "IconStar",
        _ => "IconRefresh",
    }) as StreamGeometry;

    public RelayCommand OpenCommand { get; }
    public RelayCommand DismissCommand { get; }
}

// One kind's switch in Settings > Notifications.
public sealed class NoticeFilter : ObservableObject
{
    private readonly NotificationsViewModel _owner;

    internal NoticeFilter(NoticeKind kind, bool isFirst, NotificationsViewModel owner)
    {
        Kind = kind;
        IsFirst = isFirst;
        _owner = owner;
        owner.KindChanged += k =>
        {
            if (k == kind) Raise(nameof(IsOn));
        };
    }

    public NoticeKind Kind { get; }
    public bool IsFirst { get; }
    public bool IsNotFirst => !IsFirst;
    public string Label => NoticeKinds.Label(Kind);
    public string Caption => NoticeKinds.Caption(Kind);

    public bool IsOn
    {
        get => _owner.IsKindOn(Kind);
        set
        {
            _owner.SetKind(Kind, value);
            Raise();
        }
    }

    // Greyed out while every pop-up is off.
    public bool IsEnabled => _owner.On;

    internal void Refresh() => Raise(nameof(IsEnabled));
}
