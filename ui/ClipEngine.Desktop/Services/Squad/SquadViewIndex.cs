using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services.Squad;

// One of this player's views of a squad moment: which clip on this PC shows it.
internal sealed class LocalView
{
    public string Moment { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public string Game { get; set; } = "";
    public long MomentAt { get; set; }      // server ms
    public long UtcStartMs { get; set; }    // the clip's first frame, server ms
    public double Seconds { get; set; }
    public bool Uploaded { get; set; }
}

// Which clips are this player's views of squad moments, so a squadmate asking for one can be
// answered and the squad library can play it from this PC. Kept in squad\views.json.
internal sealed class SquadViewIndex(string? file)
{
    private const int Keep = 2000;
    private readonly List<LocalView> _views = Load(file);

    public IReadOnlyList<LocalView> Views => _views;

    public LocalView? Find(string moment) => _views.FirstOrDefault(v => v.Moment == moment);

    public void Add(LocalView view)
    {
        _views.RemoveAll(v => v.Moment == view.Moment);
        _views.Add(view);
        if (_views.Count > Keep) _views.RemoveRange(0, _views.Count - Keep);
        Save();
    }

    public void MarkUploaded(string moment)
    {
        if (Find(moment) is not { } view) return;
        view.Uploaded = true;
        Save();
    }

    public void Clear()
    {
        _views.Clear();
        Save();
    }

    private static List<LocalView> Load(string? file)
    {
        if (file is null || !File.Exists(file)) return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), SquadViewJson.Default.ListLocalView) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save()
    {
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(_views, SquadViewJson.Default.ListLocalView));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSerializable(typeof(List<LocalView>))]
internal sealed partial class SquadViewJson : JsonSerializerContext;
