using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// Plays one clip inside the app, with its detected moments on the timeline.
public sealed class PlayerViewModel : ObservableObject
{
    private const double SkipSeconds = 5;

    private readonly MainViewModel _main;
    private readonly object _returnTo;
    private readonly DispatcherTimer _timer;
    private IVideoPlayer? _player;
    private double _position;
    private double _duration;
    private bool _isPlaying;
    private bool _ended;
    private double _volume;
    private string? _error;
    private DateTime _holdPositionUntil;
    private bool _isTrimming;
    private double _trimStart;
    private double _trimEnd;
    private bool _isSavingTrim;
    private bool _isChecking;

    internal PlayerViewModel(MainViewModel main, ClipItemViewModel clip, object returnTo)
    {
        _main = main;
        _returnTo = returnTo;
        Clip = clip;
        _duration = clip.File.Duration?.TotalSeconds ?? clip.File.Metadata?.Seconds ?? 0;
        _volume = Math.Clamp(main.DesktopSettings.PlayerVolume, 0, 100);

        var markers = new List<MarkerViewModel>();
        foreach (var e in clip.Events.OrderBy(e => e.Time))
        {
            MarkerViewModel? marker = null;
            marker = new MarkerViewModel(e.Type, e.Time, e.Text, new RelayCommand(() =>
            {
                Select(marker!);
                SeekTo(marker!.SeekSeconds, play: true);
            }))
            {
                FoundBy = e.FoundBy,
                StudioCommand = new RelayCommand(() => OpenStudioAt(marker!)),
            };
            markers.Add(marker);
        }
        Markers = markers;
        Counts = Markers.GroupBy(m => m.Type)
            .Select(g => new MarkerCount(EventKinds.Count(g.Key, g.Count()), g.First().Brush))
            .ToList();

        PlayPauseCommand = new RelayCommand(PlayPause);
        BackCommand = new RelayCommand(() => _main.ShowPage(_returnTo));
        SkipBackCommand = new RelayCommand(() => SeekTo(Position - SkipSeconds, play: null));
        SkipForwardCommand = new RelayCommand(() => SeekTo(Position + SkipSeconds, play: null));
        PreviousMarkerCommand = new RelayCommand(PreviousMarker, () => HasMarkers);
        NextMarkerCommand = new RelayCommand(NextMarker, () => HasMarkers);
        ShareCommand = new RelayCommand(() => _main.ShareClipCommand.Execute(Clip));
        CopyCommand = new RelayCommand(() => _main.CopyClipCommand.Execute(Clip));
        FavoriteCommand = new RelayCommand(() => _main.FavoriteClipCommand.Execute(Clip));
        TrimCommand = new RelayCommand(() => { if (IsTrimming) CancelTrim(); else StartTrim(); });
        SetTrimStartCommand = new RelayCommand(() => SetTrimFromPosition(start: true));
        SetTrimEndCommand = new RelayCommand(() => SetTrimFromPosition(start: false));
        CancelTrimCommand = new RelayCommand(CancelTrim);
        SaveTrimCommand = new RelayCommand(() => _ = SaveTrimAsync(), () => IsTrimming && !IsSavingTrim && TrimLength >= 0.5);
        ShowCommand = new RelayCommand(() => _main.ShowClipCommand.Execute(Clip));
        // The editor starts on the part being trimmed, if any, so a trim can grow into an edit.
        EditCommand = new RelayCommand(() =>
        {
            _player?.Pause();
            IsPlaying = false;
            var trimming = IsTrimming;
            IsTrimming = false;
            _main.OpenEditor(Clip, this, trimming ? _trimStart : null, trimming ? _trimEnd : null);
        });
        StudioCommand = new RelayCommand(() =>
        {
            _player?.Pause();
            IsPlaying = false;
            _main.OpenStudio(clip: Clip, seconds: Position);
        });
        OpenExternallyCommand = new RelayCommand(() =>
        {
            _player?.Pause();
            Shell.Open(Clip.Path);
        });
        DeleteCommand = new RelayCommand(() =>
        {
            // Let go of the file first, or Windows won't move it to the Recycle Bin.
            _player?.Close();
            _main.DeleteClipCommand.Execute(Clip);
            _main.ShowPage(_returnTo);
        });

        Accuracy = new AccuracyViewModel(main, clip, () => Position, seconds => SeekTo(seconds, play: true));
        CheckCommand = new RelayCommand(() =>
        {
            if (!IsChecking) IsTrimming = false;
            IsChecking = !IsChecking;
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => Poll();
    }

    public MainViewModel Main => _main;
    internal object ReturnTo => _returnTo;
    public ClipItemViewModel Clip { get; }
    public IReadOnlyList<MarkerViewModel> Markers { get; }
    public IReadOnlyList<MarkerCount> Counts { get; }
    public bool HasMarkers => Markers.Count > 0;
    public bool HasNoMarkers => Markers.Count == 0;

    // The preview tool has no video window, so it shows the thumbnail.
    public bool IsLive => _main.IsLive;

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand StudioCommand { get; }
    public RelayCommand SkipBackCommand { get; }
    public RelayCommand SkipForwardCommand { get; }
    public RelayCommand PreviousMarkerCommand { get; }
    public RelayCommand NextMarkerCommand { get; }
    public RelayCommand ShareCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand FavoriteCommand { get; }
    public RelayCommand TrimCommand { get; }
    public RelayCommand SetTrimStartCommand { get; }
    public RelayCommand SetTrimEndCommand { get; }
    public RelayCommand CancelTrimCommand { get; }
    public RelayCommand SaveTrimCommand { get; }
    public RelayCommand ShowCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand OpenExternallyCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand CheckCommand { get; }

    // Checking detection on this clip: marking what really happened, and scoring the rules on it.
    public AccuracyViewModel Accuracy { get; }

    public bool IsChecking
    {
        get => _isChecking;
        private set { if (Set(ref _isChecking, value)) Raise(nameof(IsNotChecking)); }
    }

    public bool IsNotChecking => !_isChecking;

    public string Title => Clip.HasSummary ? Clip.Summary : $"{Clip.Game} clip";
    public string ShareTip => $"Make a copy under {_main.DesktopSettings.ShareLimitMb} MB and copy it, ready to paste into Discord";
    public string Subtitle => $"{Clip.Game} · {Clip.When} · {Clip.Size}";
    public string BackLabel => _returnTo is HomePage ? "Home" : "Clips";

    // Seconds from the start. The timeline sets it to seek.
    public double Position
    {
        get => _position;
        set => SeekTo(value, play: null);
    }

    public double Duration { get => _duration; private set { if (Set(ref _duration, value)) Raise(nameof(DurationText)); } }
    public string PositionText => Format.Duration(TimeSpan.FromSeconds(_position));
    public string DurationText => Format.Duration(TimeSpan.FromSeconds(_duration));

    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) Raise(nameof(IsPaused)); }
    }

    public bool IsPaused => !_isPlaying;

    // 0 to 100.
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(Math.Round(value), 0, 100))) return;
            if (_player is not null) _player.Volume = (float)(_volume / 100);
            _main.DesktopSettings.PlayerVolume = (int)_volume;
            _main.SaveDesktopSettingsSoon();
        }
    }

    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    // Called by the view once its video window exists, and with null when it goes away.
    internal void AttachPlayer(IVideoPlayer? player)
    {
        _timer.Stop();
        _player = player;
        if (player is null) return;
        try
        {
            player.Volume = (float)(_volume / 100);
            player.Open(Clip.Path);
            Error = null;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException
                                      or UnauthorizedAccessException or DllNotFoundException)
        {
            Error = $"Windows couldn't play this clip ({e.Message.TrimEnd('.')}). Try opening it in another player.";
            return;
        }
        _timer.Start();
    }

    internal void Pause()
    {
        if (_player is null || !IsPlaying) return;
        _player.Pause();
        IsPlaying = false;
    }

    private void PlayPause()
    {
        if (_player is null) return;
        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }
        else
        {
            if (_ended) SeekTo(0, play: true);
            else _player.Play();
            IsPlaying = true;
        }
    }

    // The moment picked in the list, which shows how it was found.
    public MarkerViewModel? SelectedMarker { get; private set; }

    private void Select(MarkerViewModel marker)
    {
        if (SelectedMarker is not null && SelectedMarker != marker) SelectedMarker.IsSelected = false;
        marker.IsSelected = true;
        SelectedMarker = marker;
        Raise(nameof(SelectedMarker));
    }

    // Detection Studio on the frame where the moment was seen, with the rule that counted it.
    private void OpenStudioAt(MarkerViewModel marker)
    {
        _player?.Pause();
        IsPlaying = false;
        var studio = _main.OpenStudio(clip: Clip, seconds: marker.Seconds);
        var rule = marker.Witnesses.FirstOrDefault(w => w.Counted) ?? marker.Witnesses.FirstOrDefault();
        if (rule is not null) studio.SelectRule(rule.Rule);
    }

    private void PreviousMarker()
    {
        // Going back from just after a marker's lead-in replays that marker; tap again for the one before.
        var target = Markers.LastOrDefault(m => m.SeekSeconds < _position - 1.5);
        if (target is not null) Select(target);
        SeekTo(target?.SeekSeconds ?? 0, play: true);
    }

    private void NextMarker()
    {
        var target = Markers.FirstOrDefault(m => m.SeekSeconds > _position + 0.25);
        if (target is null) return;
        Select(target);
        SeekTo(target.SeekSeconds, play: true);
    }

    // play: true starts playback, null keeps whatever it was doing.
    private void SeekTo(double seconds, bool? play)
    {
        seconds = Math.Clamp(seconds, 0, _duration > 0 ? _duration : double.MaxValue);
        if (Math.Abs(seconds - _position) < 0.01 && play is null) return;
        SetPosition(seconds);
        _holdPositionUntil = DateTime.UtcNow.AddMilliseconds(400);
        if (_player is null) return;

        _player.Seek(TimeSpan.FromSeconds(seconds));
        // A clip that played to the end is stopped, and only plays again from Play.
        if (play == true || _ended)
        {
            _player.Play();
            IsPlaying = true;
        }
        _ended = false;
    }

    private void SetPosition(double seconds)
    {
        if (!Set(ref _position, seconds, nameof(Position))) return;
        Raise(nameof(PositionText));
    }

    private void Poll()
    {
        if (_player is null) return;
        var state = _player.State;
        if (_duration <= 0 && _player.Duration is { } d) Duration = d.TotalSeconds;

        if (state == PlaybackState.Stopped && _isPlaying)
        {
            // Played to the end.
            _ended = true;
            IsPlaying = false;
            SetPosition(_duration);
            return;
        }
        if (state is PlaybackState.Playing or PlaybackState.Paused)
        {
            IsPlaying = state == PlaybackState.Playing;
            if (DateTime.UtcNow >= _holdPositionUntil) SetPosition(Math.Min(_player.Position.TotalSeconds, _duration > 0 ? _duration : double.MaxValue));
        }
    }

    // Trimming: pick a start and an end on the timeline (drag the handles, or I and O at the
    // playhead), then save that part as a new clip. The original stays as it is.
    public bool IsTrimming
    {
        get => _isTrimming;
        private set
        {
            if (!Set(ref _isTrimming, value)) return;
            Raise(nameof(IsNotTrimming));
            SaveTrimCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsNotTrimming => !_isTrimming;

    public double TrimStart
    {
        get => _trimStart;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, _trimEnd - MinTrim));
            if (Set(ref _trimStart, value)) TrimChanged();
        }
    }

    public double TrimEnd
    {
        get => _trimEnd;
        set
        {
            value = Math.Clamp(value, Math.Min(_duration, _trimStart + MinTrim), _duration);
            if (Set(ref _trimEnd, value)) TrimChanged();
        }
    }

    public double TrimLength => Math.Max(0, _trimEnd - _trimStart);
    public string TrimText =>
        $"{Format.Duration(TimeSpan.FromSeconds(_trimStart))} – {Format.Duration(TimeSpan.FromSeconds(_trimEnd))} · {TrimLength:0.0} s";

    public bool IsSavingTrim
    {
        get => _isSavingTrim;
        private set
        {
            if (Set(ref _isSavingTrim, value)) SaveTrimCommand.NotifyCanExecuteChanged();
        }
    }

    private const double MinTrim = 0.5;

    private void StartTrim()
    {
        if (_duration <= 0) return;
        IsChecking = false;
        // Start with the moments if there are any, with a little room either side; otherwise the whole clip.
        _trimStart = HasMarkers ? Math.Max(0, Markers[0].Seconds - 3) : 0;
        _trimEnd = HasMarkers ? Math.Min(_duration, Markers[^1].Seconds + 3) : _duration;
        if (_trimEnd - _trimStart < MinTrim) (_trimStart, _trimEnd) = (0, _duration);
        IsTrimming = true;
        Raise(nameof(TrimStart));
        Raise(nameof(TrimEnd));
        TrimChanged();
    }

    private void CancelTrim() => IsTrimming = false;

    private void SetTrimFromPosition(bool start)
    {
        if (!IsTrimming) StartTrim();
        if (!IsTrimming) return;
        if (start)
        {
            // Setting a start past the end moves the end out of the way.
            if (_position > _trimEnd - MinTrim) TrimEnd = _duration;
            TrimStart = _position;
        }
        else
        {
            if (_position < _trimStart + MinTrim) TrimStart = 0;
            TrimEnd = _position;
        }
    }

    private void TrimChanged()
    {
        Raise(nameof(TrimLength));
        Raise(nameof(TrimText));
        SaveTrimCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveTrimAsync()
    {
        IsSavingTrim = true;
        _player?.Pause();
        IsPlaying = false;
        var trimmed = await _main.TrimClipAsync(Clip, _trimStart, _trimEnd);
        IsSavingTrim = false;
        if (trimmed is null) return;
        IsTrimming = false;
        _main.OpenPlayer(trimmed);
    }

    // For the preview tool's screenshot.
    internal void ShowAt(double seconds, bool playing)
    {
        SetPosition(seconds);
        IsPlaying = playing;
    }

    // For the preview tool: how one of the moments was found.
    internal void ShowWhy(int index) => Select(Markers[index]);

    // For the preview tool: the Check detection panel with some marks and a result.
    internal void ShowCheck()
    {
        IsChecking = true;
        foreach (var (type, at) in new[] { ("kill", 6.2), ("kill", 9.8), ("death", 15.1), ("kill", 21.4), ("revive", 25.0) })
            Accuracy.AddMark(type, at);
        Accuracy.ShowResult(DetectionAccuracy.Compare(
            Accuracy.Marks.Select(m => new ClipEvent { Type = m.Event.Type, Time = m.Event.Time }),
            [new ClipEvent { Type = "kill", Time = 6.4 }, new ClipEvent { Type = "kill", Time = 10.1 },
             new ClipEvent { Type = "death", Time = 15.3 }, new ClipEvent { Type = "revive", Time = 25.2 },
             new ClipEvent { Type = "kill", Time = 18.0, Text = "ELIMINATED BY VEX" }]));
    }

    internal void ShowTrim(double start, double end)
    {
        StartTrim();
        TrimEnd = end;
        TrimStart = start;
    }
}

public sealed record MarkerCount(string Label, Avalonia.Media.IBrush Brush);
