using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

// The kinds of pop-up. Each one can be turned off on its own in Settings > Notifications, and a switch there turns
// them all off.
public enum NoticeKind { FriendRequest, Friends, Clip, Moment, Squad, Subscription, Update }

internal static class NoticeKinds
{
    public static readonly IReadOnlyList<NoticeKind> All =
        [NoticeKind.FriendRequest, NoticeKind.Friends, NoticeKind.Clip, NoticeKind.Moment, NoticeKind.Squad,
         NoticeKind.Subscription, NoticeKind.Update];

    // How a kind is kept in desktop.json and notifications.json.
    public static string Id(NoticeKind kind) => kind switch
    {
        NoticeKind.FriendRequest => "friend_request",
        NoticeKind.Friends => "friends",
        NoticeKind.Clip => "clip",
        NoticeKind.Moment => "moment",
        NoticeKind.Squad => "squad",
        NoticeKind.Subscription => "subscription",
        _ => "update",
    };

    public static NoticeKind? FromId(string? id)
    {
        foreach (var kind in All)
        {
            if (Id(kind) == id) return kind;
        }
        return null;
    }

    public static string Label(NoticeKind kind) => kind switch
    {
        NoticeKind.FriendRequest => "Friend requests",
        NoticeKind.Friends => "Friends online",
        NoticeKind.Clip => "Clips saved",
        NoticeKind.Moment => "Moments over the game",
        NoticeKind.Squad => "Squad",
        NoticeKind.Subscription => "Subscription",
        _ => "Updates",
    };

    public static string Caption(NoticeKind kind) => kind switch
    {
        NoticeKind.FriendRequest => "Someone wants to be friends, or said yes to your request.",
        NoticeKind.Friends => "A friend comes online or starts playing something.",
        NoticeKind.Clip => "After each clip and long recording is saved, and when a clip reaches the cloud.",
        NoticeKind.Moment => "Each kill, multi-kill, revive and assist as it's spotted, and a new personal best. Only over the game.",
        NoticeKind.Squad => "Someone joins or leaves your squad.",
        NoticeKind.Subscription => "A few days before your subscription renews or ends, and if your plan changes.",
        _ => "A new version of Clip Engine is out.",
    };
}

// What pops up, kept in desktop.json: everything (On), and the kinds turned off one by one.
internal sealed class NoticeSettings
{
    public bool On { get; set; } = true;
    public List<string> Muted { get; set; } = [];

    // Friend requests already announced, by account, so each pops up once, even one sent while the app was closed.
    public List<string> AnnouncedRequests { get; set; } = [];

    // The renewal (or end) date already announced, in Unix milliseconds, so it pops up once.
    public long? RenewalNoticed { get; set; }

    public bool IsOn(NoticeKind kind) => !Muted.Contains(NoticeKinds.Id(kind));

    public bool Allows(NoticeKind kind) => On && IsOn(kind);

    public void Set(NoticeKind kind, bool on)
    {
        var id = NoticeKinds.Id(kind);
        Muted.RemoveAll(m => m == id);
        if (!on) Muted.Add(id);
    }
}

// One notification as the history keeps it.
internal sealed class NoticeRecord
{
    public DateTimeOffset Time { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    // Where clicking it goes: "friends", "squad", "plan", "updates", "cloud" or "clip:<path>".
    public string? Target { get; set; }
    public bool Read { get; set; }
}

// The notifications history, newest first, in %LOCALAPPDATA%\Clip_Engine\notifications.json. It stays on this PC.
internal static class NoticeHistory
{
    public const int MaxKept = 60;

    public static List<NoticeRecord> Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var list = JsonSerializer.Deserialize(File.ReadAllText(file), NoticeJson.Default.ListNoticeRecord) ?? [];
                return Trim(list.Where(r => NoticeKinds.FromId(r.Kind) is not null).OrderByDescending(r => r.Time).ToList());
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    public static void Save(string file, IEnumerable<NoticeRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(Trim(records.ToList()), NoticeJson.Default.ListNoticeRecord));
    }

    public static List<NoticeRecord> Trim(List<NoticeRecord> records)
    {
        if (records.Count > MaxKept) records.RemoveRange(MaxKept, records.Count - MaxKept);
        return records;
    }
}

// A few days before a subscription renews, or ends after it was cancelled, a notice says so: once for each date.
internal static class RenewalNotice
{
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(3);

    // The title and text to show, or null when there's nothing to say yet (or it was said for this date already).
    public static (string Title, string Text)? For(DateTimeOffset? renews, bool ending, string plan, DateTimeOffset now, long? noticed)
    {
        if (renews is not { } at || at <= now || at - now > Ahead) return null;
        if (noticed == at.ToUnixTimeMilliseconds()) return null;
        var when = Day(at, now);
        return ending
            ? ("Subscription ending", $"Your {plan} subscription ends {when}. Renew it to keep {plan}.")
            : ("Subscription renews soon", $"Your {plan} subscription renews {when}. Change it with Manage in Settings > Plan.");
    }

    // "today", "tomorrow" or "on Friday, Nov 13", in this PC's time.
    internal static string Day(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (at.ToLocalTime().Date - now.ToLocalTime().Date).Days;
        return days switch
        {
            <= 0 => "today",
            1 => "tomorrow",
            _ => "on " + at.ToLocalTime().ToString("dddd, MMM d", CultureInfo.CurrentCulture),
        };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<NoticeRecord>))]
internal sealed partial class NoticeJson : JsonSerializerContext;
