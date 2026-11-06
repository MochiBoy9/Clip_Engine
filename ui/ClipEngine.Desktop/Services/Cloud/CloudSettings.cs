using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services.Cloud;

// The cloud library account on this PC, kept in desktop.json; the token is encrypted.
internal sealed class CloudSettings
{
    public string AccountId { get; set; } = "";
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public string Handle { get; set; } = "";

    // New uploads: "unlisted" (anyone with the link), "public" (also on your profile and in the hub) or "private".
    public string DefaultVisibility { get; set; } = "unlisted";

    // Starred clips go up to the cloud by themselves.
    public bool SyncStarred { get; set; }

    // Clips on this PC already in the cloud: CloudApi.LocalId(clip) to the clip's short code.
    public Dictionary<string, string> Uploaded { get; set; } = [];

    [JsonIgnore]
    public bool SignedIn => AccountId.Length > 0 && Token.Length > 0;

    public static readonly IReadOnlyList<string> Visibilities = ["unlisted", "public", "private"];

    public void SignOut()
    {
        AccountId = Token = Name = Handle = "";
        Uploaded.Clear();
    }
}
