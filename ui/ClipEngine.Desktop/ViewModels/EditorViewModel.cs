using Avalonia.Media;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Editing;

namespace ClipEngine.Desktop.ViewModels;

// The video editor: a clip cut into parts on a timeline, played back as the edit will look, and
// exported as a new clip. The clip itself is never changed, and the edit is kept so it can be
// picked up again.
//
// This is the base the rest of the editor builds on: every change goes through EditTimeline, so
// new tools only add operations there and buttons here.
public sealed class EditorViewModel : ObservableObject
{
    private const double SkipSeconds = 5;
    // How close to the end of a part playback moves on to the next one.
    private const double CutLookAhead = 0.03;

    private readonly MainViewModel _main;
    private readonly object _returnTo;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveTimer;
    private readonly string _projectFile;
    private IVideoPlayer? _player;
    private int _playingIndex;
    private double _position;
    private bool _isPlaying;
    private bool _ended;
    private double _volume;
    private string? _error;
    private DateTime _holdPositionUntil;
    private int _selectedIndex;
    private double _zoom = 1;
    private bool _isExporting;
    private int _exportProgress;
    private CancellationTokenSource? _exportCancel;
    private ExportQualityOption _quality;
    private bool _resumed;

    internal EditorViewModel(MainViewModel main, ClipItemViewModel clip, object returnTo, double? keepFrom = null, double? keepTo = null)
    {
        _main = main;
        _returnTo = returnTo;
        Clip = clip;
        _volume = Math.Clamp(main.DesktopSettings.PlayerVolume, 0, 100);
        _quality = EditExport.Qualities.FirstOrDefault(q => q.Label == main.DesktopSettings.EditExportQuality) ?? EditExport.Qualities[0];
        var duration = clip.File.Duration?.TotalSeconds ?? clip.File.Metadata?.Seconds ?? 0;

        // Pick up the last edit of this clip, unless it was opened to keep a part picked in the player.
        _projectFile = EditProject.PathFor(AppPaths.DataDir, clip.Path);
        EditTimeline? timeline = null;
        if (keepFrom is null && main.IsLive) timeline = EditProject.Load(_projectFile)?.ToTimeline(clip.Path, duration);
        _resumed = timeline is not null && timeline.IsEdited;
        Timeline = timeline ?? EditTimeline.Whole(clip.Path, duration);
        if (keepFrom is { } from && keepTo is { } to) Timeline.KeepOnly(from, to);
        Timeline.Changed += OnTimelineChanged;

        PlayPauseCommand = new RelayCommand(PlayPause);
        BackCommand = new RelayCommand(Back);
        SkipBackCommand = new RelayCommand(() => SeekTo(_position - SkipSeconds, play: null));
        SkipForwardCommand = new RelayCommand(() => SeekTo(_position + SkipSeconds, play: null));
        PreviousCutCommand = new RelayCommand(PreviousCut);
        NextCutCommand = new RelayCommand(NextCut);
        SplitCommand = new RelayCommand(Split, () => !_isExporting);
        DeleteCommand = new RelayCommand(() => DeletePart(_selectedIndex), () => !_isExporting && Parts.Count > 1);
        DuplicateCommand = new RelayCommand(() => Edit(() => Timeline.Duplicate(_selectedIndex)), () => !_isExporting);
        MoveEarlierCommand = new RelayCommand(() => MovePart(_selectedIndex, _selectedIndex - 1),
                                              () => !_isExporting && _selectedIndex > 0);
        MoveLaterCommand = new RelayCommand(() => MovePart(_selectedIndex, _selectedIndex + 1),
                                            () => !_isExporting && _selectedIndex < Parts.Count - 1);
        TrimStartHereCommand = new RelayCommand(() => TrimHere(SegmentEdge.Start), () => !_isExporting);
        TrimEndHereCommand = new RelayCommand(() => TrimHere(SegmentEdge.End), () => !_isExporting);
        UndoCommand = new RelayCommand(() => Edit(Timeline.Undo), () => !_isExporting && Timeline.CanUndo);
        RedoCommand = new RelayCommand(() => Edit(Timeline.Redo), () => !_isExporting && Timeline.CanRedo);
        StartOverCommand = new RelayCommand(() => Edit(Timeline.Reset), () => !_isExporting && Timeline.IsEdited);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => !_isExporting && Timeline.Duration >= EditTimeline.MinSegment);
        CancelExportCommand = new RelayCommand(() => _exportCancel?.Cancel());
        ZoomInCommand = new RelayCommand(() => Zoom *= 1.5);
        ZoomOutCommand = new RelayCommand(() => Zoom /= 1.5);

        Rebuild();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Poll();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveProject();
    }

    public MainViewModel Main => _main;
    internal object ReturnTo => _returnTo;
    public ClipItemViewModel Clip { get; }
    internal EditTimeline Timeline { get; }

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand SkipBackCommand { get; }
    public RelayCommand SkipForwardCommand { get; }
    public RelayCommand PreviousCutCommand { get; }
    public RelayCommand NextCutCommand { get; }
    public RelayCommand SplitCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand MoveEarlierCommand { get; }
    public RelayCommand MoveLaterCommand { get; }
    public RelayCommand TrimStartHereCommand { get; }
    public RelayCommand TrimEndHereCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand StartOverCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CancelExportCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }

    // The preview tool has no video window, so it shows the thumbnail.
    public bool IsLive => _main.IsLive;

    public string Title => Clip.HasSummary ? Clip.Summary : $"{Clip.Game} clip";
    public string Subtitle => $"{Clip.Game} · {Clip.When} · original {Format.Duration(TimeSpan.FromSeconds(Timeline.SourceDuration(Clip.Path)))}";
    public string BackLabel => _returnTo is PlayerViewModel ? "Clip" : _returnTo is HomePage ? "Home" : "Clips";

    // Shown when the editor opened on the edit left last time.
    public bool IsResumed { get => _resumed; private set => Set(ref _resumed, value); }

    public IReadOnlyList<EditPartViewModel> Parts { get; private set; } = [];
    public IReadOnlyList<EditorMarker> Markers { get; private set; } = [];
    public IReadOnlyList<double> Cuts { get; private set; } = [];

    public double Duration => Timeline.Duration;
    public string DurationText => Format.Duration(TimeSpan.FromSeconds(Duration));
    public string PositionText => Format.Duration(TimeSpan.FromSeconds(_position));
    public string Summary
    {
        get
        {
            var original = Timeline.SourceDuration(Clip.Path);
            var parts = Parts.Count == 1 ? "1 part" : $"{Parts.Count} parts";
            var cut = original - Duration;
            return cut > 0.05
                ? $"{parts} · {Duration:0.0} s · {cut:0.0} s cut"
                : $"{parts} · {Duration:0.0} s";
        }
    }

    // Seconds into the edit. The timeline sets it to seek.
    public double Position
    {
        get => _position;
        set => SeekTo(value, play: null);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) Raise(nameof(IsPaused)); }
    }

    public bool IsPaused => !_isPlaying;

    // 0 to 100, shared with the player.
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

    // The part picked on the timeline or in the list, which Delete, Duplicate and Move act on.
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, Parts.Count - 1));
            if (!Set(ref _selectedIndex, value)) return;
            for (var i = 0; i < Parts.Count; i++) Parts[i].IsSelected = i == value;
            Raise(nameof(SelectedPart));
            RefreshCommands();
        }
    }

    public EditPartViewModel? SelectedPart => _selectedIndex < Parts.Count ? Parts[_selectedIndex] : null;

    // How far the timeline is zoomed in: 1 fits the whole edit, up to 40 times closer.
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (Set(ref _zoom, Math.Clamp(value, 1, 40))) Raise(nameof(ZoomText));
        }
    }

    public string ZoomText => _zoom < 1.05 ? "Fit" : $"{_zoom:0.#}×";

    public IReadOnlyList<ExportQualityOption> Qualities => EditExport.Qualities;

    public ExportQualityOption Quality
    {
        get => _quality;
        set
        {
            if (value is null || !Set(ref _quality, value)) return;
            _main.DesktopSettings.EditExportQuality = value.Label;
            _main.SaveDesktopSettingsSoon();
        }
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (!Set(ref _isExporting, value)) return;
            Raise(nameof(IsNotExporting));
            RefreshCommands();
        }
    }

    public bool IsNotExporting => !_isExporting;
    public int ExportProgress { get => _exportProgress; private set { if (Set(ref _exportProgress, value)) Raise(nameof(ExportText)); } }
    public string ExportText => $"Exporting… {_exportProgress}%";
    public string ExportHint =>
        $"Saved next to the original as a new clip, with its moments. {(_quality.Quality == ExportQuality.Original ? "Re-encoded at the clip's own size." : _quality.Detail + ".")}";

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
        // Start on the first part, which may not be the start of the clip.
        SeekTo(0, play: null, force: true);
        _timer.Start();
    }

    internal void Pause()
    {
        if (_player is null || !IsPlaying) return;
        _player.Pause();
        IsPlaying = false;
    }

    // Leaving keeps the edit: it's saved now rather than after the usual pause.
    private void Back()
    {
        Pause();
        if (_saveTimer.IsEnabled) SaveProject();
        _main.ShowPage(_returnTo);
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
            if (_ended || _position >= Duration - 0.05) SeekTo(0, play: true);
            else _player.Play();
            IsPlaying = true;
        }
    }

    // play: true starts playback, null keeps whatever it was doing.
    private void SeekTo(double seconds, bool? play, bool force = false)
    {
        seconds = Math.Clamp(seconds, 0, Duration);
        if (!force && Math.Abs(seconds - _position) < 0.005 && play is null) return;
        SetPosition(seconds);
        var (index, source) = Timeline.Locate(seconds);
        _playingIndex = Math.Max(0, index);
        _holdPositionUntil = DateTime.UtcNow.AddMilliseconds(400);
        if (_player is null) return;

        _player.Seek(TimeSpan.FromSeconds(source));
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

    // Follows the player through the clip, jumping over what was cut at each cut.
    private void Poll()
    {
        if (_player is null || Timeline.Segments.Count == 0) return;
        var state = _player.State;
        var segments = Timeline.Segments;
        _playingIndex = Math.Clamp(_playingIndex, 0, segments.Count - 1);
        var part = segments[_playingIndex];

        if (state == PlaybackState.Stopped && _isPlaying)
        {
            // The clip itself ended, which can only be the end of the last part.
            FinishPlayback();
            return;
        }
        if (state is not (PlaybackState.Playing or PlaybackState.Paused)) return;
        IsPlaying = state == PlaybackState.Playing;
        if (DateTime.UtcNow < _holdPositionUntil) return;

        var source = _player.Position.TotalSeconds;
        if (_isPlaying && source >= part.To - CutLookAhead)
        {
            if (_playingIndex + 1 >= segments.Count)
            {
                FinishPlayback();
                return;
            }
            _playingIndex++;
            var next = segments[_playingIndex];
            // Parts that follow on in the clip play straight through; anything else is a jump.
            if (!string.Equals(next.Source, part.Source, StringComparison.OrdinalIgnoreCase) || Math.Abs(next.From - part.To) > 0.05)
            {
                _player.Seek(TimeSpan.FromSeconds(next.From));
                _holdPositionUntil = DateTime.UtcNow.AddMilliseconds(120);
            }
            SetPosition(Timeline.StartOf(_playingIndex));
            return;
        }
        var into = Math.Clamp(source - part.From, 0, part.Length);
        SetPosition(Math.Min(Timeline.StartOf(_playingIndex) + into, Duration));
    }

    private void FinishPlayback()
    {
        _player?.Pause();
        _ended = true;
        IsPlaying = false;
        SetPosition(Duration);
    }

    private void PreviousCut()
    {
        var starts = Cuts.Prepend(0).Where(c => c < _position - 0.25).ToList();
        SeekTo(starts.Count > 0 ? starts[^1] : 0, play: null);
    }

    private void NextCut()
    {
        var next = Cuts.Append(Duration).FirstOrDefault(c => c > _position + 0.05, Duration);
        SeekTo(next, play: null);
    }

    // Cuts the part under the playhead in two, at the playhead.
    private void Split()
    {
        if (Timeline.Split(_position, out var after))
        {
            SelectedIndex = after;
            return;
        }
        _main.ShowToast($"Move the playhead at least {EditTimeline.MinSegment:0.##} s from a cut to split there.");
    }

    internal void DeletePart(int index)
    {
        if (Parts.Count <= 1)
        {
            _main.ShowToast("An edit needs at least one part. Trim it instead, or start over.");
            return;
        }
        var start = Timeline.StartOf(index);
        if (!Timeline.Remove(index)) return;
        SelectedIndex = Math.Min(index, Parts.Count - 1);
        SeekTo(Math.Min(start, Duration), play: null, force: true);
    }

    internal void MovePart(int from, int to)
    {
        if (!Timeline.Move(from, to)) return;
        SelectedIndex = to;
        SeekTo(Timeline.StartOf(to), play: null, force: true);
    }

    // I and O: the part under the playhead starts or ends at the playhead.
    private void TrimHere(SegmentEdge edge)
    {
        var (index, source) = Timeline.Locate(_position);
        if (index < 0) return;
        if (!Timeline.Resize(index, edge, source)) return;
        SelectedIndex = index;
        // Trimming a start pulls everything after it earlier, so the playhead goes with the part.
        SeekTo(edge == SegmentEdge.Start ? Timeline.StartOf(index) : Math.Min(_position, Timeline.EndOf(index)), play: null, force: true);
    }

    // Selecting a part from the list jumps to it.
    internal void SelectPart(int index)
    {
        SelectedIndex = index;
        SeekTo(Timeline.StartOf(index), play: null, force: true);
    }

    // Dragging the end of a part on the timeline. Begin and End make the drag one undo step.
    internal void BeginResize(int index)
    {
        Pause();
        SelectedIndex = index;
        Timeline.BeginGesture();
    }

    // Moves that end to the source time under the pointer, and shows that frame.
    internal void ResizeTo(int index, SegmentEdge edge, double sourceSeconds)
    {
        Timeline.Resize(index, edge, sourceSeconds);
        var part = Timeline.Segments[index];
        var at = edge == SegmentEdge.Start ? Timeline.StartOf(index) : Timeline.EndOf(index) - 0.001;
        SetPosition(Math.Clamp(at, 0, Duration));
        _playingIndex = index;
        _holdPositionUntil = DateTime.UtcNow.AddMilliseconds(400);
        _player?.Seek(TimeSpan.FromSeconds(edge == SegmentEdge.Start ? part.From : part.To));
    }

    internal void EndResize() => Timeline.EndGesture();

    private void Edit(Func<bool> change)
    {
        if (!change()) return;
        SeekTo(Math.Min(_position, Duration), play: null, force: true);
    }

    private void OnTimelineChanged()
    {
        Rebuild();
        IsResumed = false;
        _saveTimer.Stop();
        if (IsLive) _saveTimer.Start();
    }

    private void Rebuild()
    {
        var segments = Timeline.Segments;
        Parts = segments.Select((s, i) => new EditPartViewModel(this, i, s, Timeline.StartOf(i)) { IsSelected = i == _selectedIndex }).ToList();
        if (_selectedIndex >= Parts.Count) _selectedIndex = Parts.Count - 1;
        if (_selectedIndex >= 0 && _selectedIndex < Parts.Count) Parts[_selectedIndex].IsSelected = true;
        Cuts = Timeline.Cuts();
        Markers = Clip.Events
            .SelectMany(e => Timeline.EditTimesOf(Clip.Path, e.Time).Select(t => new EditorMarker(e.Type, t)))
            .OrderBy(m => m.Seconds)
            .ToList();
        Raise(nameof(Parts));
        Raise(nameof(Cuts));
        Raise(nameof(Markers));
        Raise(nameof(Duration));
        Raise(nameof(DurationText));
        Raise(nameof(Summary));
        Raise(nameof(SelectedIndex));
        Raise(nameof(SelectedPart));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        foreach (var command in new[] { SplitCommand, DeleteCommand, DuplicateCommand, MoveEarlierCommand, MoveLaterCommand,
                                        TrimStartHereCommand, TrimEndHereCommand, UndoCommand, RedoCommand, StartOverCommand,
                                        ExportCommand })
            command.NotifyCanExecuteChanged();
    }

    // Keeps the edit for next time; a clip put back the way it was has nothing to keep.
    private void SaveProject()
    {
        _saveTimer.Stop();
        try
        {
            if (Timeline.IsEdited) EditProject.From(Timeline, DateTimeOffset.Now).Save(_projectFile);
            else EditProject.Delete(_projectFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The edit still works; it just won't be there next time.
        }
    }

    private async Task ExportAsync()
    {
        Pause();
        if (_saveTimer.IsEnabled) SaveProject();
        IsExporting = true;
        ExportProgress = 0;
        _exportCancel = new CancellationTokenSource();
        try
        {
            var path = await EditExport.RunAsync(Timeline, _quality.Quality,
                percent => Dispatcher.UIThread.Post(() => ExportProgress = Math.Max(ExportProgress, percent)),
                _exportCancel.Token);
            _main.AddActivity(ActivityKind.Clip,
                $"Edited a {Clip.Game} clip into {Format.Duration(TimeSpan.FromSeconds(Duration))} from {Parts.Count} {(Parts.Count == 1 ? "part" : "parts")}");
            if (ReferenceEquals(_main.CurrentPage, this)) await _main.OpenSavedClipAsync(path, "Edit saved next to the original");
            else
            {
                await _main.RefreshClipsAsync();
                _main.ShowToast("Edit saved next to the original");
            }
        }
        catch (OperationCanceledException)
        {
            _main.ShowToast("Export stopped");
        }
        catch (EditFailedException e)
        {
            _main.ShowToast(e.Message, isError: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _main.ShowToast($"Couldn't export the edit: {e.Message}", isError: true);
        }
        finally
        {
            _exportCancel.Dispose();
            _exportCancel = null;
            IsExporting = false;
        }
    }

    // For the preview tool's screenshot: a few cuts and the playhead partway in.
    internal void ShowSample()
    {
        var source = Timeline.SourceDuration(Clip.Path);
        Timeline.KeepOnly(2, Math.Min(source, 29));
        Timeline.Split(6.5, out _);
        Timeline.Split(13, out _);
        Timeline.Remove(1);
        Timeline.Split(15.5, out _);
        Zoom = 1;
        SelectedIndex = 1;
        SetPosition(9.2);
        IsPlaying = false;
    }
}

// One part in the editor's list and on its timeline.
public sealed class EditPartViewModel : ObservableObject
{
    private bool _isSelected;

    internal EditPartViewModel(EditorViewModel editor, int index, EditSegment segment, double start)
    {
        Index = index;
        Segment = segment;
        Start = start;
        SelectCommand = new RelayCommand(() => editor.SelectPart(index));
        RemoveCommand = new RelayCommand(() => editor.DeletePart(index));
        MoveEarlierCommand = new RelayCommand(() => editor.MovePart(index, index - 1));
        MoveLaterCommand = new RelayCommand(() => editor.MovePart(index, index + 1));
    }

    internal EditSegment Segment { get; }
    public int Index { get; }
    public double Start { get; }
    public double Length => Segment.Length;
    public string Name => $"Part {Index + 1}";
    public string RangeText => $"{Format.Duration(TimeSpan.FromSeconds(Segment.From))} – {Format.Duration(TimeSpan.FromSeconds(Segment.To))} of the clip";
    public string LengthText => $"{Segment.Length:0.0} s";
    public RelayCommand SelectCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand MoveEarlierCommand { get; }
    public RelayCommand MoveLaterCommand { get; }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

// A detected moment where it lands in the edit.
public sealed record EditorMarker(string Type, double Seconds)
{
    public IBrush Brush => EventKinds.Brush(Type);
    public string Label => EventKinds.Label(Type);
}
