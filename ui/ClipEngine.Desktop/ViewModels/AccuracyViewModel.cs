using System.Collections.ObjectModel;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The player's "Check detection" panel: mark what really happened in a clip (each kill, death
// and so on, at the playhead), then run the game's rules over the whole clip the way the engine
// reads the game, and see what they caught, missed and found that wasn't there. The marks are
// kept in the clip's .json, so a rule changed in Detection Studio can be checked again on the
// same clips.
public sealed class AccuracyViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ClipItemViewModel _clip;
    private readonly Func<double> _position;
    private readonly Action<double> _seek;
    private EventOption _markType;
    private bool _isScanning;
    private int _progress;
    private string? _status;
    private bool _statusIsError;
    private AccuracyResult? _result;
    private CancellationTokenSource? _cancel;

    internal AccuracyViewModel(MainViewModel main, ClipItemViewModel clip, Func<double> position, Action<double> seek)
    {
        _main = main;
        _clip = clip;
        _position = position;
        _seek = seek;
        Events = EngineSettings.AllEvents.Select(e => new EventOption(e, EventKinds.Label(e), EventKinds.Brush(e))).ToList();
        _markType = Events[0];
        foreach (var mark in (clip.File.Metadata?.Labels ?? []).OrderBy(m => m.Time)) Marks.Add(MakeMark(mark));

        MarkCommand = new RelayCommand(() => AddMark(_markType.Name, _position()));
        StartFromDetectedCommand = new RelayCommand(StartFromDetected, () => _clip.Events.Count > 0);
        ClearCommand = new RelayCommand(() => SetMarks([]), () => Marks.Count > 0);
        RunCommand = new RelayCommand(() => _ = RunAsync(), () => !IsScanning && CanScan);
        TeachCommand = new RelayCommand(() => _ = TeachAsync(), () => !IsScanning && CanScan && Marks.Count > 0);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsScanning);
    }

    public IReadOnlyList<EventOption> Events { get; }
    public ObservableCollection<MarkViewModel> Marks { get; } = [];
    public ObservableCollection<MarkerViewModel> Missed { get; } = [];
    public ObservableCollection<MarkerViewModel> Extra { get; } = [];
    public ObservableCollection<AccuracyRow> Rows { get; } = [];

    public RelayCommand MarkCommand { get; }
    public RelayCommand StartFromDetectedCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand TeachCommand { get; }
    public RelayCommand CancelCommand { get; }

    // What the next mark is: picked here, or with the number keys in the player.
    public EventOption MarkType { get => _markType; set { if (value is not null) Set(ref _markType, value); } }

    public bool HasMarks => Marks.Count > 0;
    public string MarksCaption => Marks.Count switch
    {
        0 => "Play the clip and press Mark at each moment the game shows: every kill, death, revive and so on.",
        1 => "1 moment marked.",
        var n => $"{n} moments marked.",
    };

    private bool CanScan => StudioTools.Available || !_main.IsLive;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            RunCommand.NotifyCanExecuteChanged();
            TeachCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    public int Progress { get => _progress; private set => Set(ref _progress, value); }

    public string? Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status is not null;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public bool HasResult => _result is not null;
    public string ResultSummary => _result?.Summary ?? "";
    public bool HasMissed => Missed.Count > 0;
    public bool HasExtra => Extra.Count > 0;

    internal void AddMark(string type, double seconds)
    {
        var marks = Marks.Select(m => m.Event).Append(new ClipEvent { Type = type, Time = Math.Round(seconds, 2) }).ToList();
        SetMarks(marks);
    }

    internal void RemoveMark(MarkViewModel mark) => SetMarks(Marks.Where(m => m != mark).Select(m => m.Event).ToList());

    // The moments detection saved with the clip, as a start: remove the wrong ones, mark the missed.
    private void StartFromDetected() =>
        SetMarks(_clip.Events.Select(e => new ClipEvent { Type = e.Type, Time = e.Time, Text = e.Text }).ToList());

    private void SetMarks(IReadOnlyList<ClipEvent> marks)
    {
        var metadata = _clip.File.Metadata ?? new ClipMetadata
        {
            Game = _clip.Game,
            Trigger = "hotkey",
            Seconds = _clip.File.Duration?.TotalSeconds ?? 0,
        };
        var old = metadata.Labels;
        metadata.Labels = marks.OrderBy(m => m.Time).ToList();
        if (_main.IsLive)
        {
            try
            {
                ClipMetadataFile.WriteSidecar(_clip.Path, metadata);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                metadata.Labels = old;
                ShowStatus($"Couldn't save the marks: {e.Message}", error: true);
                return;
            }
            _main.ClipMetadataChanged(_clip, metadata);
        }
        Marks.Clear();
        foreach (var mark in metadata.Labels) Marks.Add(MakeMark(mark));
        Raise(nameof(HasMarks));
        Raise(nameof(MarksCaption));
        ClearCommand.NotifyCanExecuteChanged();
        TeachCommand.NotifyCanExecuteChanged();
        // An old result no longer matches the marks.
        if (_result is not null) ShowResult(null);
    }

    private MarkViewModel MakeMark(ClipEvent mark) => new(mark, this, new RelayCommand(() => _seek(Math.Max(0, mark.Time - 2))));

    private async Task RunAsync()
    {
        if (!_main.IsLive) return;
        IsScanning = true;
        Progress = 0;
        ShowStatus("Reading the clip the way the engine reads the game…");
        _cancel = new CancellationTokenSource();
        try
        {
            var quality = EngineSettings.Load(AppPaths.EngineSettingsFile).ReadingQuality;
            var found = await StudioTools.ScanAsync(_clip.Path, _clip.Game, quality,
                p => Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = p), _cancel.Token);
            ShowResult(DetectionAccuracy.Compare(Marks.Select(m => m.Event), found));
            ShowStatus(Marks.Count == 0 ? "Mark what really happened to see what the rules missed." : null);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(null);
        }
        catch (Exception e) when (e is StudioFailedException or IOException or System.ComponentModel.Win32Exception)
        {
            ShowStatus($"Couldn't check the clip: {e.Message}", error: true);
        }
        finally
        {
            IsScanning = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    // Teaches the universal HUD reader from the marks (clip_engine.exe --learn), then checks the
    // clip again so the difference shows.
    private async Task TeachAsync()
    {
        if (!_main.IsLive || Marks.Count == 0) return;
        IsScanning = true;
        Progress = 0;
        ShowStatus("Reading the clip to learn from your marks…");
        _cancel = new CancellationTokenSource();
        try
        {
            var quality = EngineSettings.Load(AppPaths.EngineSettingsFile).ReadingQuality;
            var learned = await StudioTools.LearnAsync(_clip.Path, _clip.Game, Marks.Select(m => m.Event), quality,
                p => Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = p), _cancel.Token);
            ShowStatus(TeachSummary(learned, _clip.Game));
        }
        catch (OperationCanceledException)
        {
            ShowStatus(null);
        }
        catch (Exception e) when (e is StudioFailedException or IOException or System.ComponentModel.Win32Exception)
        {
            ShowStatus($"Couldn't learn from the clip: {e.Message}", error: true);
        }
        finally
        {
            IsScanning = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    internal static string TeachSummary(IReadOnlyList<LearnExample> learned, string game)
    {
        if (learned.Count == 0) return "Nothing new to learn: the universal HUD reader already reads this clip the way you marked it.";
        var moments = learned.Count(e => e.Label != "none");
        var wrong = learned.Count - moments;
        var parts = new List<string>();
        if (moments > 0) parts.Add(moments == 1 ? "1 line that shows a moment" : $"{moments} lines that show a moment");
        if (wrong > 0) parts.Add(wrong == 1 ? "1 line that means nothing" : $"{wrong} lines that mean nothing");
        return $"Learned {string.Join(" and ", parts)} for {game}. It reads {game} this way from the next time you play it, " +
               "and you can share it with the game's detection pack.";
    }

    internal void ShowResult(AccuracyResult? result)
    {
        _result = result;
        Missed.Clear();
        Extra.Clear();
        Rows.Clear();
        if (result is not null)
        {
            foreach (var e in result.Missed) Missed.Add(Marker(e));
            foreach (var e in result.Extra) Extra.Add(Marker(e));
            foreach (var (type, marked, caught, extra) in result.ByType) Rows.Add(new AccuracyRow(type, marked, caught, extra));
        }
        Raise(nameof(HasResult));
        Raise(nameof(ResultSummary));
        Raise(nameof(HasMissed));
        Raise(nameof(HasExtra));
    }

    private MarkerViewModel Marker(ClipEvent e)
    {
        MarkerViewModel? marker = null;
        marker = new MarkerViewModel(e.Type, e.Time, e.Text, new RelayCommand(() => _seek(marker!.SeekSeconds)));
        return marker;
    }

    private void ShowStatus(string? text, bool error = false)
    {
        StatusIsError = error;
        Status = text;
    }
}

// One mark in the list, with a button to take it off.
public sealed class MarkViewModel
{
    internal MarkViewModel(ClipEvent mark, AccuracyViewModel owner, RelayCommand seek)
    {
        Event = mark;
        SeekCommand = seek;
        RemoveCommand = new RelayCommand(() => owner.RemoveMark(this));
    }

    internal ClipEvent Event { get; }
    public string Label => EventKinds.Label(Event.Type);
    public string TimeText => Format.Duration(TimeSpan.FromSeconds(Event.Time)) + $".{(int)(Event.Time * 10) % 10}";
    public Avalonia.Media.IBrush Brush => EventKinds.Brush(Event.Type);
    public RelayCommand SeekCommand { get; }
    public RelayCommand RemoveCommand { get; }
}

// How one kind of moment did: "Kills 7 of 8 · 1 extra".
public sealed record AccuracyRow(string Type, int Marked, int Caught, int Extra)
{
    public string Label => EventKinds.Label(Type);
    public Avalonia.Media.IBrush Brush => EventKinds.Brush(Type);
    public string Text => (Marked > 0 ? $"{Caught} of {Marked}" : "none marked") + (Extra > 0 ? $" · {Extra} extra" : "");
}
