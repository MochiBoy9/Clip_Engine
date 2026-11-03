using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services.Squad;

// Squad sync's settings, kept in desktop.json with the app's other settings. The token is
// encrypted for this Windows account, like the update token.
internal sealed class SquadSettings
{
    public string SquadId { get; set; } = "";
    public string SquadName { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string Token { get; set; } = "";

    // The name squadmates see.
    public string PlayerName { get; set; } = "";

    // What goes up to the shared library by itself: "highlights" (multi-kills, match wins and
    // starred clips), "all" or "off". Anything else uploads when a squadmate asks for it, unless off.
    public string Uploads { get; set; } = "highlights";

    // Which of your moments squadmates save their view of.
    public List<string> ShareMoments { get; set; } = [.. DefaultShareMoments];

    // A "Join my squad" button on your Discord status, for friends who aren't in the squad yet.
    // Anyone who can see your status can use it, so it's off until you turn it on.
    public bool JoinOnDiscord { get; set; }

    // Uploads are share-size copies of at most this many MB.
    public int UploadLimitMb { get; set; } = 25;

    public static readonly IReadOnlyList<string> DefaultShareMoments = ["kill", "revive", "highlight", "match_end"];
    public static readonly IReadOnlyList<string> UploadChoices = ["highlights", "all", "off"];

    [JsonIgnore]
    public bool InSquad => SquadId.Length > 0 && Token.Length > 0;

    public void Leave()
    {
        SquadId = SquadName = MemberId = Token = "";
    }
}
