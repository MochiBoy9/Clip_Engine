using Avalonia.Threading;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Squad;

namespace ClipEngine.Desktop.ViewModels;

// Watch together: every view of one squad moment side by side, lined up by when each frame was
// recorded, on one timeline. One view is heard at a time; clicking another switches to it.
public sealed class SquadPlayerViewModel : ObservableObject
{
    private const double Lead = 4;          // seconds shown before the moment when it starts or replays
    private const double Drift = 0.2;       // how far a view may wander from the one heard before it's put back
    private static readonly TimeSpan ResyncEvery = TimeSpan.FromSeconds(1.5);

    private readonly MainViewModel _main;
    private readonly SquadViewModel _squad;
    private readonly DispatcherTimer _timer;
    private readonly double _momentAt;
    private double _position;
    private bool _isPlaying;
    private bool _started;
    private DateTime _holdPositionUntil;

    internal SquadPlayerViewModel(MainViewModel main, SquadViewModel squad, SquadMomentRow row,
                                  IReadOnlyList<(SquadView View, string Path)> views, object returnTo)
    {
        _main = main;
        _squad = squad;
        Row = row;
        ReturnTo = returnTo;
        var offsets = SquadPlan.SyncOffsets(views.Select(v => v.View).ToList());
        var latest = views.Max(v => v.View.UtcStartMs);
        Tiles = views.Select((v, i) => new SquadTile(this, v.View, v.Path, offsets[v.View.Member], i == 0)).ToList();
        // The stretch every view covers.
        Duration = Math.Max(1, Tiles.Min(t => t.Seconds - t.Offset));
        _momentAt = Math.Clamp((row.Moment.At - latest) / 1000.0, 0, Duration);
        StartAt = Math.Max(0, _momentAt - Lead);
        _position = StartAt;

        PlayPauseCommand = new RelayCommand(PlayPause);
        ReplayCommand = new RelayCommand(() => SeekTo(StartAt, play: true));
        SkipBackCommand = new RelayCommand(() => SeekTo(Position - 5, play: null));
        SkipForwardCommand = new RelayCommand(() => SeekTo(Position + 5, play: null));
        BackCommand = new RelayCommand(Back);
        HighlightCommand = new RelayCommand(() => _ = squad.MakeHighlightAsync(row), () => Tiles.Count > 1);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Poll();
    }

    internal SquadMomentRow Row { get; }
    internal object ReturnTo { get; }
    internal bool IsLive => _main.IsLive;

    public IReadOnlyList<SquadTile> Tiles { get; }
    public int Columns => Tiles.Count <= 1 ? 1 : 2;
    public string Title => Row.Title;
    public string Caption => $"{Row.Game} · {Row.When} · {Tiles.Count} {(Tiles.Count == 1 ? "view" : "views")}, lined up by when each frame was recorded";
    public double Duration { get; }
    public double StartAt { get; }
    public double MomentAt => _momentAt;
    public bool CanHighlight => Tiles.Count > 1;

    public double Position
    {
        get => _position;
        set => SeekTo(value, play: null);
    }

    public string PositionText => $"{Format.Duration(TimeSpan.FromSeconds(_position))} / {Format.Duration(TimeSpan.FromSeconds(Duration))}";

    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) Raise(nameof(IsPaused)); }
    }

    public bool IsPaused => !_isPlaying;

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand ReplayCommand { get; }
    public RelayCommand SkipBackCommand { get; }
    public RelayCommand SkipForwardCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand HighlightCommand { get; }

    private SquadTile? Heard => Tiles.FirstOrDefault(t => t.IsListening && t.Player is not null) ?? Tiles.FirstOrDefault(t => t.Player is not null);

    internal void Attach(SquadTile tile, IVideoPlayer? player)
    {
        tile.Player = player;
        if (player is null)
        {
            if (Tiles.All(t => t.Player is null)) _timer.Stop();
            return;
        }
        try
        {
            player.Volume = tile.IsListening ? 1 : 0;
            player.Open(tile.Path);
            tile.Error = null;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException
                                      or UnauthorizedAccessException or DllNotFoundException)
        {
            tile.Player = null;
            tile.Error = $"Windows couldn't play this view ({e.Message.TrimEnd('.')}).";
            return;
        }
        _timer.Start();
    }

    internal void Listen(SquadTile tile)
    {
        foreach (var t in Tiles)
        {
            t.IsListening = ReferenceEquals(t, tile);
            if (t.Player is not null) t.Player.Volume = t.IsListening ? 1 : 0;
        }
    }

    internal void Pause()
    {
        foreach (var tile in Tiles) tile.Player?.Pause();
        IsPlaying = false;
    }

    private void PlayPause()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (_position >= Duration - 0.1) SeekTo(StartAt, play: true);
        else
        {
            foreach (var tile in Tiles) tile.Player?.Play();
            IsPlaying = true;
        }
    }

    private void SeekTo(double seconds, bool? play)
    {
        seconds = Math.Clamp(seconds, 0, Duration);
        foreach (var tile in Tiles)
        {
            if (tile.Player is null) continue;
            tile.Player.Seek(TimeSpan.FromSeconds(Math.Min(seconds + tile.Offset, tile.Seconds)));
            tile.LastSeek = DateTime.UtcNow;
        }
        _holdPositionUntil = DateTime.UtcNow.AddMilliseconds(400);
        SetPosition(seconds);
        if (play == true)
        {
            foreach (var tile in Tiles) tile.Player?.Play();
            IsPlaying = true;
        }
        else if (play == false) Pause();
    }

    private void SetPosition(double seconds)
    {
        if (!Set(ref _position, seconds, nameof(Position))) return;
        Raise(nameof(PositionText));
    }

    private void Poll()
    {
        var ready = Tiles.Where(t => t.Player is not null).ToList();
        if (ready.Count == 0) return;
        if (!_started)
        {
            // Each player opens on its own; once they all have, they start together just before the moment.
            if (ready.Any(t => t.Player!.Duration is null) || ready.Count < Tiles.Count(t => t.Error is null)) return;
            _started = true;
            SeekTo(StartAt, play: true);
            return;
        }
        if (Heard is not { Player: { } heard } master) return;
        var state = heard.State;
        if (state == PlaybackState.Stopped && IsPlaying)
        {
            Pause();
            SetPosition(Duration);
            return;
        }
        if (state is not (PlaybackState.Playing or PlaybackState.Paused)) return;
        IsPlaying = state == PlaybackState.Playing;
        var at = heard.Position.TotalSeconds - master.Offset;
        if (DateTime.UtcNow >= _holdPositionUntil) SetPosition(Math.Clamp(at, 0, Duration));
        if (at >= Duration && IsPlaying)
        {
            Pause();
            return;
        }
        if (!IsPlaying || DateTime.UtcNow < _holdPositionUntil) return;
        // Keep the others with the one being heard.
        foreach (var tile in ready)
        {
            if (ReferenceEquals(tile, master) || DateTime.UtcNow - tile.LastSeek < ResyncEvery) continue;
            var expected = at + tile.Offset;
            if (expected >= tile.Seconds) continue;
            if (Math.Abs(tile.Player!.Position.TotalSeconds - expected) <= Drift) continue;
            tile.Player.Seek(TimeSpan.FromSeconds(expected));
            if (tile.Player.State != PlaybackState.Playing) tile.Player.Play();
            tile.LastSeek = DateTime.UtcNow;
        }
    }

    private void Back()
    {
        Pause();
        _timer.Stop();
        _main.ShowPage(ReturnTo);
    }
}

public sealed class SquadTile : ObservableObject
{
    private readonly SquadPlayerViewModel _owner;
    private bool _isListening;
    private string? _error;

    internal SquadTile(SquadPlayerViewModel owner, SquadView view, string path, double offset, bool listening)
    {
        _owner = owner;
        Name = view.Mine ? "You" : view.Name;
        Path = path;
        Offset = offset;
        Seconds = view.Seconds;
        _isListening = listening;
        ListenCommand = new RelayCommand(() => owner.Listen(this));
    }

    public string Name { get; }
    internal string Path { get; }
    internal double Offset { get; }
    internal double Seconds { get; }
    internal IVideoPlayer? Player { get; set; }
    internal DateTime LastSeek { get; set; }

    public bool IsListening
    {
        get => _isListening;
        set { if (Set(ref _isListening, value)) Raise(nameof(ListenText)); }
    }

    public string ListenText => IsListening ? "Listening" : "Listen";
    public string? Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    public RelayCommand ListenCommand { get; }

    internal bool IsLive => _owner.IsLive;
    public bool IsPreview => !_owner.IsLive;

    // The preview tool has no video windows, so each tile shows a still.
    public Avalonia.Media.Imaging.Bitmap? Still { get; internal set; }

    internal void Attach(IVideoPlayer? player) => _owner.Attach(this, player);
}
