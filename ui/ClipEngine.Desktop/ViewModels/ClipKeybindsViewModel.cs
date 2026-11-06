using System.Collections.ObjectModel;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// A list of keys that each save a clip of their own length: the extra ones in Settings, or a
// game's own set in Detection Studio.
public sealed class ClipKeybindsViewModel : ObservableObject
{
    // What Add picks first: keys near the usual save hotkey, then lengths that differ from it.
    private static readonly string[] SuggestedKeys = ["Alt+F8", "Shift+F8", "Ctrl+F8", "F7", "Alt+F7", "Shift+F7", "F6", "Alt+F6"];
    private static readonly int[] SuggestedSeconds = [15, 60, 120, 10, 45, 90, 20, 30];

    private readonly Func<IEnumerable<string>> _otherKeys;
    private readonly Action _changed;
    private double _maxSeconds;
    private bool _loading;

    // otherKeys are the keys taken by something else (the save or long recording hotkey), which
    // these can't share. changed runs on every edit, not on Load.
    internal ClipKeybindsViewModel(Func<IEnumerable<string>> otherKeys, Action changed, double maxSeconds)
    {
        _otherKeys = otherKeys;
        _changed = changed;
        _maxSeconds = Math.Max(ClipKeybinds.MinSeconds, maxSeconds);
        AddCommand = new RelayCommand(Add, () => CanAdd);
    }

    public ObservableCollection<ClipKeybindViewModel> Rows { get; } = [];
    public RelayCommand AddCommand { get; }
    public bool CanAdd => Rows.Count < ClipKeybinds.MaxKeys;
    public bool HasRows => Rows.Count > 0;

    // The longest a clip can be: the length of the replay buffer.
    public double MaxSeconds
    {
        get => _maxSeconds;
        set
        {
            if (!Set(ref _maxSeconds, Math.Max(ClipKeybinds.MinSeconds, value))) return;
            // Whatever changed the limit counts as the change; shortening the keys to fit isn't another.
            var loading = _loading;
            _loading = true;
            foreach (var row in Rows) row.Seconds = Math.Min(row.Seconds, _maxSeconds);
            _loading = loading;
            Recheck();
        }
    }

    // The first thing that keeps these from being saved, or null.
    public string? FirstProblem => Rows.Select(r => r.Problem is { } p ? $"{(string.IsNullOrWhiteSpace(r.Hotkey) ? "A clip key" : r.Hotkey.Trim())}: {p}" : null)
                                       .FirstOrDefault(p => p is not null);

    internal void Load(IEnumerable<ClipKeybind> keys)
    {
        _loading = true;
        Rows.Clear();
        foreach (var key in keys) Rows.Add(new ClipKeybindViewModel(this, key.Hotkey, Math.Clamp(key.Seconds, ClipKeybinds.MinSeconds, (int)_maxSeconds)));
        _loading = false;
        ListChanged(notify: false);
    }

    internal List<ClipKeybind> ToKeybinds() =>
        Rows.Where(r => !string.IsNullOrWhiteSpace(r.Hotkey)).Select(r => new ClipKeybind(r.Hotkey.Trim(), (int)r.Seconds)).ToList();

    // The keys taken elsewhere changed (the save hotkey was edited), so a clash may have come or gone.
    internal void Recheck()
    {
        foreach (var row in Rows) row.RaiseProblem();
        Raise(nameof(FirstProblem));
    }

    internal void RowChanged()
    {
        if (_loading) return;
        Recheck();
        _changed();
    }

    internal void Remove(ClipKeybindViewModel row)
    {
        if (!Rows.Remove(row)) return;
        ListChanged(notify: true);
    }

    // Why this row's key can't be used, or null.
    internal string? ProblemFor(ClipKeybindViewModel row)
    {
        if (ClipKeybinds.Problem(row.Hotkey) is { } problem) return problem;
        var key = ClipKeybinds.Normalize(row.Hotkey);
        if (_otherKeys().Where(ClipKeybinds.IsKey).Any(k => ClipKeybinds.Normalize(k) == key)) return "Already used by another hotkey.";
        if (Rows.TakeWhile(r => !ReferenceEquals(r, row)).Any(r => ClipKeybinds.IsKey(r.Hotkey) && ClipKeybinds.Normalize(r.Hotkey) == key))
            return "Already used by another clip key.";
        return null;
    }

    private void Add()
    {
        if (!CanAdd) return;
        var taken = _otherKeys().Concat(Rows.Select(r => r.Hotkey)).Where(ClipKeybinds.IsKey).Select(ClipKeybinds.Normalize).ToHashSet();
        var hotkey = SuggestedKeys.FirstOrDefault(k => !taken.Contains(ClipKeybinds.Normalize(k))) ?? "";
        var lengths = Rows.Select(r => (int)r.Seconds).ToHashSet();
        var seconds = SuggestedSeconds.Select(s => Math.Min(s, (int)_maxSeconds)).FirstOrDefault(s => !lengths.Contains(s), (int)_maxSeconds);
        Rows.Add(new ClipKeybindViewModel(this, hotkey, Math.Max(ClipKeybinds.MinSeconds, seconds)));
        ListChanged(notify: true);
    }

    private void ListChanged(bool notify)
    {
        AddCommand.NotifyCanExecuteChanged();
        Raise(nameof(CanAdd));
        Raise(nameof(HasRows));
        Recheck();
        if (notify) _changed();
    }
}

// One key and the length of the clip it saves.
public sealed class ClipKeybindViewModel : ObservableObject
{
    private readonly ClipKeybindsViewModel _list;
    private string _hotkey;
    private double _seconds;

    internal ClipKeybindViewModel(ClipKeybindsViewModel list, string hotkey, double seconds)
    {
        _list = list;
        _hotkey = hotkey;
        _seconds = seconds;
        RemoveCommand = new RelayCommand(() => _list.Remove(this));
    }

    public RelayCommand RemoveCommand { get; }

    public string Hotkey
    {
        get => _hotkey;
        set
        {
            if (Set(ref _hotkey, value ?? "")) _list.RowChanged();
        }
    }

    public double Seconds
    {
        get => _seconds;
        set
        {
            var seconds = Math.Clamp(Math.Round(value), ClipKeybinds.MinSeconds, _list.MaxSeconds);
            if (!Set(ref _seconds, seconds)) return;
            Raise(nameof(SecondsText));
            _list.RowChanged();
        }
    }

    public double MaxSeconds => _list.MaxSeconds;
    public string SecondsText => SettingsViewModel.FormatSeconds(_seconds);
    public string? Problem => _list.ProblemFor(this);
    public bool HasProblem => Problem is not null;

    internal void RaiseProblem()
    {
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
        Raise(nameof(MaxSeconds));
    }
}

// A game's own clip keys, in Detection Studio: used instead of the ones from Settings while
// that game is recorded. Saved on their own, straight to clip_engine.ini, and handed to the
// engine at once, so there's no restart.
public sealed class GameClipKeysViewModel : ObservableObject
{
    private readonly bool _live;
    private readonly Func<string, bool>? _sendEngineCommand;
    private EngineSettings _settings = new();
    private string? _gameId;
    private string _gameName = "";
    private bool _useOwn;
    private bool _savedUseOwn;
    private List<ClipKeybind> _saved = [];
    private bool _hasChanges;
    private string? _message;

    internal GameClipKeysViewModel(bool live, Func<string, bool>? sendEngineCommand)
    {
        _live = live;
        _sendEngineCommand = sendEngineCommand;
        Keys = new ClipKeybindsViewModel(() => [_settings.RecordHotkey], Changed, _settings.BufferSeconds);
        SaveCommand = new RelayCommand(Save, () => _hasChanges);
        RevertCommand = new RelayCommand(() => { if (_gameId is not null) Load(_gameId, _gameName); }, () => _hasChanges);
    }

    public ClipKeybindsViewModel Keys { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }

    // Whether this game has its own keys. Turning it on starts from the ones in Settings.
    public bool UseOwn
    {
        get => _useOwn;
        set
        {
            if (!Set(ref _useOwn, value)) return;
            if (value && !Keys.HasRows) Keys.Load(UsualKeys());
            Raise(nameof(Summary));
            Changed();
        }
    }

    public string Summary => _useOwn
        ? $"While {_gameName} is recorded, only these keys save clips."
        : $"{_gameName} uses the clip keys from Settings: {Describe(UsualKeys())}.";

    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            if (!Set(ref _hasChanges, value)) return;
            SaveCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
        }
    }

    public string? Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message is not null;

    internal void Load(string gameId, string gameName)
    {
        _gameId = gameId;
        _gameName = gameName;
        List<ClipKeybind>? own = null;
        if (_live)
        {
            try
            {
                _settings = EngineSettings.Load(AppPaths.EngineSettingsFile);
                own = ClipKeybinds.LoadForGame(AppPaths.EngineSettingsFile, gameId);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        Keys.MaxSeconds = Math.Clamp(_settings.BufferSeconds, 10, ReplayMemory.MaxSeconds);
        _saved = own ?? [];
        _savedUseOwn = _useOwn = own is not null;
        Keys.Load(own ?? []);
        Message = null;
        HasChanges = false;
        Raise(nameof(UseOwn));
        Raise(nameof(Summary));
    }

    private void Changed()
    {
        Message = null;
        HasChanges = _useOwn != _savedUseOwn || (_useOwn && !Keys.ToKeybinds().SequenceEqual(_saved));
    }

    private void Save()
    {
        if (_gameId is null) return;
        var keys = Keys.ToKeybinds();
        if (_useOwn && Keys.FirstProblem is { } problem)
        {
            Message = $"Fix the clip keys first. {problem}";
            return;
        }
        if (_useOwn && keys.Count == 0)
        {
            Message = "Add a clip key, or turn off own keys to use the ones from Settings.";
            return;
        }
        if (_live)
        {
            try
            {
                ClipKeybinds.SaveForGame(AppPaths.EngineSettingsFile, _gameId, _useOwn ? keys : null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Message = $"Couldn't save the clip keys: {e.Message}";
                return;
            }
            // A running engine picks them up now if it's recording this game, and otherwise
            // reads them when the game starts.
            _sendEngineCommand?.Invoke("reload-clip-keys");
        }
        _savedUseOwn = _useOwn;
        _saved = _useOwn ? keys : [];
        HasChanges = false;
        Message = _useOwn ? $"Saved. These keys save clips in {_gameName}." : $"Saved. {_gameName} uses the clip keys from Settings.";
    }

    private List<ClipKeybind> UsualKeys() =>
        [new ClipKeybind(_settings.SaveHotkey, Math.Clamp(_settings.ClipSeconds, ClipKeybinds.MinSeconds, Math.Max(ClipKeybinds.MinSeconds, _settings.BufferSeconds))),
         .. _settings.MoreClipHotkeys];

    internal static string Describe(IEnumerable<ClipKeybind> keys) =>
        string.Join(", ", keys.Select(k => $"{k.Hotkey} saves {SettingsViewModel.FormatSeconds(k.Seconds)}"));
}
