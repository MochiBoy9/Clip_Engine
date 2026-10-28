using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// A game Detection Studio can make rules for: one Clip Engine knows, or one added in detection.ini.
public sealed record StudioGameOption(string Id, string Name, bool IsCustom, IReadOnlyList<string> Executables)
{
    public override string ToString() => Name;
}

// A program with a window, offered when adding a game.
public sealed record RunningProgram(string Title, string Exe)
{
    public override string ToString() => $"{Title} ({Exe})";
}

// Detection Studio: make or fix the rules that spot kills and other moments in any game. Pick a
// frame from one of your clips (or a screenshot), draw a box where the game shows the moment,
// type the words it shows, test it on the frame, and save.
public sealed class StudioViewModel : ObservableObject
{
    // The games built into the engine (src/game/GameDetector.cpp).
    private static readonly StudioGameOption[] BuiltIn =
    [
        new("the_finals", "The Finals", false, ["Discovery.exe"]),
        new("overwatch", "Overwatch", false, ["Overwatch.exe"]),
        new("battlefield_6", "Battlefield 6", false, ["bf6.exe"]),
    ];

    private readonly MainViewModel _main;
    private readonly object _returnTo;
    private DetectionIni _ini;
    private StudioGameOption? _game;
    private StudioRuleViewModel? _selectedRule;
    private ClipItemViewModel? _source;
    private string? _imageSource;
    private double _frameSeconds;
    private Bitmap? _frame;
    private bool _isBusy;
    private string? _status;
    private bool _statusIsError;
    private string? _readText;
    private bool? _matched;
    private bool _hasChanges;
    private int _regionsVersion;
    private bool _isAddingGame;
    private string _newGameName = "";
    private string _newGameExe = "";
    private RunningProgram? _newGameProgram;
    private Avalonia.Threading.DispatcherTimer? _frameTimer;
    private bool _reloadFrame;

    internal StudioViewModel(MainViewModel main, object returnTo, string? gameName = null, ClipItemViewModel? clip = null,
                             double seconds = 0)
    {
        _main = main;
        _returnTo = returnTo;
        _ini = main.IsLive ? DetectionIni.Load() : DetectionIni.Parse(SampleIni);

        BackCommand = new RelayCommand(() => _main.ShowPage(_returnTo));
        AddRuleCommand = new RelayCommand(AddRule, () => _game is not null);
        DeleteRuleCommand = new RelayCommand(DeleteRule, () => _selectedRule is not null);
        TestCommand = new RelayCommand(() => _ = TestAsync(), () => _selectedRule is not null && HasFrameSource && !IsBusy);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => HasChanges && !IsBusy);
        LoadFrameCommand = new RelayCommand(() => _ = LoadFrameAsync(), () => HasFrameSource && !IsBusy);
        OpenImageCommand = new RelayCommand(() => _ = OpenImageAsync());
        StartAddGameCommand = new RelayCommand(StartAddGame);
        CancelAddGameCommand = new RelayCommand(() => IsAddingGame = false);
        ConfirmAddGameCommand = new RelayCommand(ConfirmAddGame, () => CanAddGame);

        LoadGames();
        // Start on the clip (and moment) the Studio was opened from; picking the game keeps it.
        _source = clip;
        _frameSeconds = Math.Round(Math.Max(0, seconds), 1);
        SelectedGame = Games.FirstOrDefault(g => string.Equals(g.Name, gameName ?? clip?.Game, StringComparison.OrdinalIgnoreCase))
                       ?? Games.FirstOrDefault();
    }

    public MainViewModel Main => _main;
    public ObservableCollection<StudioGameOption> Games { get; } = [];
    public ObservableCollection<StudioRuleViewModel> Rules { get; } = [];
    public ObservableCollection<ClipItemViewModel> Sources { get; } = [];
    public ObservableCollection<RunningProgram> RunningPrograms { get; } = [];
    public IReadOnlyList<EventOption> Events { get; } =
        EngineSettings.AllEvents.Select(e => new EventOption(e, EventKinds.Label(e), EventKinds.Brush(e))).ToList();

    public RelayCommand BackCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand DeleteRuleCommand { get; }
    public RelayCommand TestCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand LoadFrameCommand { get; }
    public RelayCommand OpenImageCommand { get; }
    public RelayCommand StartAddGameCommand { get; }
    public RelayCommand CancelAddGameCommand { get; }
    public RelayCommand ConfirmAddGameCommand { get; }

    public string BackLabel => _returnTo is PlayerViewModel ? "Clip" : _returnTo is GamesPage ? "Games" : "Back";

    public StudioGameOption? SelectedGame
    {
        get => _game;
        set
        {
            if (value is null || !Set(ref _game, value)) return;
            LoadRules();
            LoadSources();
            AddRuleCommand.NotifyCanExecuteChanged();
            Raise(nameof(GameCaption));
        }
    }

    public string GameCaption => _game is null ? ""
        : $"Recorded when {string.Join(" or ", _game.Executables)} is running · {(Rules.Count == 1 ? "1 rule" : $"{Rules.Count} rules")}";

    public StudioRuleViewModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (!Set(ref _selectedRule, value)) return;
            foreach (var rule in Rules) rule.IsSelected = ReferenceEquals(rule, value);
            Raise(nameof(HasSelectedRule));
            ReadText = null;
            Matched = null;
            DeleteRuleCommand.NotifyCanExecuteChanged();
            TestCommand.NotifyCanExecuteChanged();
            RegionsChanged();
        }
    }

    public bool HasSelectedRule => _selectedRule is not null;
    public bool HasRules => Rules.Count > 0;

    // Where the frame comes from: one of the game's clips at a moment, or a screenshot.
    public ClipItemViewModel? SelectedSource
    {
        get => _source;
        set
        {
            var samePath = value?.Path == _source?.Path;
            if (!Set(ref _source, value) && _imageSource is null && _frame is not null) return;
            _imageSource = null;
            if (!samePath) _frameSeconds = 0;
            Raise(nameof(FrameSeconds));
            Raise(nameof(FrameTimeText));
            Raise(nameof(SourceDuration));
            Raise(nameof(SourceMoments));
            Raise(nameof(HasFrameSource));
            Raise(nameof(IsClipSource));
            LoadFrameCommand.NotifyCanExecuteChanged();
            TestCommand.NotifyCanExecuteChanged();
            if (value is not null && _main.IsLive) _ = LoadFrameAsync();
        }
    }

    public bool HasFrameSource => _source is not null || _imageSource is not null;
    public bool IsClipSource => _imageSource is null && _source is not null;
    public double SourceDuration => _source?.File.Duration?.TotalSeconds ?? _source?.File.Metadata?.Seconds ?? 30;

    public double FrameSeconds
    {
        get => _frameSeconds;
        set
        {
            if (!Set(ref _frameSeconds, Math.Round(Math.Max(0, value), 1))) return;
            Raise(nameof(FrameTimeText));
            // Show the new frame once the slider settles, rather than on every step of a drag.
            if (!_main.IsLive || !IsClipSource) return;
            _frameTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(350),
                Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
                {
                    _frameTimer!.Stop();
                    _ = LoadFrameAsync();
                });
            _frameTimer.Stop();
            _frameTimer.Start();
        }
    }

    public string FrameTimeText => Format.Duration(TimeSpan.FromSeconds(_frameSeconds)) + $".{(int)(_frameSeconds * 10) % 10}";

    // The clip's moments, to jump the frame to one: the kill feed is easiest to box while it shows a kill.
    public IReadOnlyList<MarkerViewModel> SourceMoments => _source?.Events
        .OrderBy(e => e.Time)
        .Select(e =>
        {
            MarkerViewModel? marker = null;
            marker = new MarkerViewModel(e.Type, e.Time, e.Text, new RelayCommand(() => FrameSeconds = marker!.Seconds + 0.3));
            return marker;
        }).ToList() ?? [];

    public Bitmap? Frame
    {
        get => _frame;
        private set { if (Set(ref _frame, value)) Raise(nameof(HasFrame)); }
    }

    public bool HasFrame => _frame is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            TestCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
            LoadFrameCommand.NotifyCanExecuteChanged();
        }
    }

    public string? Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status is not null;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    // What the last test read in the box, and whether the rule's words matched it.
    public string? ReadText { get => _readText; private set { if (Set(ref _readText, value)) Raise(nameof(HasReadText)); } }
    public bool HasReadText => _readText is not null;

    public bool? Matched
    {
        get => _matched;
        private set
        {
            if (!Set(ref _matched, value)) return;
            Raise(nameof(IsMatch));
            Raise(nameof(IsNoMatch));
        }
    }

    public bool IsMatch => _matched == true;
    public bool IsNoMatch => _matched == false;

    public bool HasChanges
    {
        get => _hasChanges;
        private set { if (Set(ref _hasChanges, value)) SaveCommand.NotifyCanExecuteChanged(); }
    }

    // Bumped whenever a box moves, so the canvas redraws.
    public int RegionsVersion { get => _regionsVersion; private set => Set(ref _regionsVersion, value); }

    // Adding a game: its name and the program that runs it.
    public bool IsAddingGame { get => _isAddingGame; private set { if (Set(ref _isAddingGame, value)) Raise(nameof(IsNotAddingGame)); } }
    public bool IsNotAddingGame => !_isAddingGame;

    public string NewGameName
    {
        get => _newGameName;
        set { if (Set(ref _newGameName, value ?? "")) ConfirmAddGameCommand.NotifyCanExecuteChanged(); }
    }

    public string NewGameExe
    {
        get => _newGameExe;
        set { if (Set(ref _newGameExe, value ?? "")) ConfirmAddGameCommand.NotifyCanExecuteChanged(); }
    }

    public RunningProgram? NewGameProgram
    {
        get => _newGameProgram;
        set
        {
            if (!Set(ref _newGameProgram, value) || value is null) return;
            NewGameExe = value.Exe;
            if (NewGameName.Trim().Length == 0) NewGameName = value.Title;
        }
    }

    private bool CanAddGame => GameId(NewGameName).Length > 0 && NewGameExe.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    internal void RuleChanged()
    {
        HasChanges = true;
        Matched = null;
        RegionsChanged();
    }

    private void RegionsChanged() => RegionsVersion++;

    private void LoadGames()
    {
        Games.Clear();
        foreach (var game in BuiltIn) Games.Add(game);
        foreach (var game in _ini.Games.Where(g => BuiltIn.All(b => b.Id != g.Id)))
            Games.Add(new StudioGameOption(game.Id, game.Name, true, game.Executables));
    }

    private void LoadRules()
    {
        Rules.Clear();
        if (_game is null) return;
        foreach (var rule in _ini.Rules.Where(r => r.Game == _game.Id)) Rules.Add(new StudioRuleViewModel(rule, this));
        SelectedRule = Rules.FirstOrDefault();
        HasChanges = false;
        Raise(nameof(HasRules));
        RegionsChanged();
    }

    private void LoadSources()
    {
        Sources.Clear();
        if (_game is null) return;
        foreach (var clip in _main.Clips.Where(c => string.Equals(c.Game, _game.Name, StringComparison.OrdinalIgnoreCase))) Sources.Add(clip);
        // Clips with moments first: their frames show what the rules look for.
        var ordered = Sources.OrderByDescending(c => c.Events.Count > 0).ToList();
        Sources.Clear();
        foreach (var clip in ordered) Sources.Add(clip);
        if (_imageSource is null) SelectedSource = Sources.FirstOrDefault(c => c.Path == _source?.Path) ?? Sources.FirstOrDefault();
    }

    private void AddRule()
    {
        if (_game is null) return;
        var rule = new StudioRuleViewModel(new StudioRule { Game = _game.Id, Event = "kill" }, this);
        Rules.Add(rule);
        SelectedRule = rule;
        Raise(nameof(HasRules));
        Raise(nameof(GameCaption));
        RuleChanged();
    }

    private void DeleteRule()
    {
        if (_selectedRule is null) return;
        var index = Rules.IndexOf(_selectedRule);
        Rules.Remove(_selectedRule);
        SelectedRule = Rules.Count > 0 ? Rules[Math.Min(index, Rules.Count - 1)] : null;
        Raise(nameof(HasRules));
        Raise(nameof(GameCaption));
        RuleChanged();
    }

    private string? FrameSourcePath => _imageSource ?? _source?.Path;
    private double FrameSourceSeconds => _imageSource is null ? _frameSeconds : 0;

    private async Task LoadFrameAsync()
    {
        if (FrameSourcePath is not { } path) return;
        if (IsBusy)
        {
            _reloadFrame = true;
            return;
        }
        IsBusy = true;
        try
        {
            var png = await StudioTools.GrabFrameAsync(path, FrameSourceSeconds);
            Bitmap frame;
            await using (var stream = File.OpenRead(png)) frame = new Bitmap(stream);
            var old = Frame;
            Frame = frame;
            old?.Dispose();
            ShowStatus(null);
        }
        catch (Exception e) when (e is StudioFailedException or IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            ShowStatus($"Couldn't get the frame: {e.Message}", error: true);
        }
        finally
        {
            IsBusy = false;
            if (_reloadFrame)
            {
                _reloadFrame = false;
                _ = LoadFrameAsync();
            }
        }
    }

    private async Task OpenImageAsync()
    {
        if (_main.Dialogs is null || await _main.Dialogs.PickImageAsync() is not { } path) return;
        _imageSource = path;
        _source = null;
        Raise(nameof(SelectedSource));
        Raise(nameof(HasFrameSource));
        Raise(nameof(IsClipSource));
        TestCommand.NotifyCanExecuteChanged();
        LoadFrameCommand.NotifyCanExecuteChanged();
        await LoadFrameAsync();
    }

    private async Task TestAsync()
    {
        if (_selectedRule is null || FrameSourcePath is not { } path) return;
        IsBusy = true;
        try
        {
            var (text, matches) = await StudioTools.ReadAsync(path, FrameSourceSeconds, _selectedRule.Rule);
            ReadText = text.Length > 0 ? text : "(nothing readable)";
            Matched = matches > 0;
            ShowStatus(null);
        }
        catch (Exception e) when (e is StudioFailedException or IOException or System.ComponentModel.Win32Exception)
        {
            ShowStatus($"Couldn't test the rule: {e.Message}", error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_game is null) return;
        var problem = Rules.FirstOrDefault(r => r.Words.Trim().Length == 0);
        if (problem is not null)
        {
            SelectedRule = problem;
            ShowStatus("Each rule needs at least one word to look for.", error: true);
            return;
        }

        // Several rules for one event get their own section names: [game.kill], [game.kill.2], ...
        foreach (var group in Rules.GroupBy(r => r.Event))
        {
            var n = 0;
            foreach (var rule in group) rule.Rule.Suffix = n++ == 0 ? "" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var custom = _game.IsCustom ? new StudioGame(_game.Id, _game.Name, _game.Executables) : null;
        _ini.SetGame(_game.Id, custom, Rules.Select(r => r.Rule));
        if (!_main.IsLive)
        {
            HasChanges = false;
            return;
        }
        try
        {
            _ini.SaveToUserFile();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn't save: {e.Message}", error: true);
            return;
        }
        HasChanges = false;
        ShowStatus($"Saved to {DetectionIni.UserFile}. The engine is restarting to use the new rules.");
        await _main.RestartEngineAsync();
    }

    private void StartAddGame()
    {
        NewGameName = "";
        NewGameExe = "";
        _newGameProgram = null;
        Raise(nameof(NewGameProgram));
        RunningPrograms.Clear();
        foreach (var program in FindRunningPrograms()) RunningPrograms.Add(program);
        IsAddingGame = true;
    }

    private void ConfirmAddGame()
    {
        if (!CanAddGame) return;
        var exe = Path.GetFileName(NewGameExe.Trim());
        var game = new StudioGameOption(GameId(NewGameName), NewGameName.Trim(), true, [exe]);
        var existing = Games.FirstOrDefault(g => g.Id == game.Id);
        if (existing is not null) Games.Remove(existing);
        Games.Add(game);
        IsAddingGame = false;
        SelectedGame = game;
        HasChanges = true;
        ShowStatus($"Added {game.Name}. Draw boxes for its moments, then save; it's recorded whenever {exe} runs.");
    }

    // "Marvel Rivals" -> "marvel_rivals", what detection.ini sections use.
    internal static string GameId(string name)
    {
        var id = new string(name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        while (id.Contains("__")) id = id.Replace("__", "_");
        return id.Trim('_');
    }

    private static IEnumerable<RunningProgram> FindRunningPrograms()
    {
        var self = Environment.ProcessId;
        var programs = new List<RunningProgram>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == self || process.MainWindowHandle == 0 || process.MainWindowTitle.Length == 0) continue;
                programs.Add(new RunningProgram(process.MainWindowTitle, process.ProcessName + ".exe"));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return programs.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase);
    }

    private void ShowStatus(string? text, bool error = false)
    {
        StatusIsError = error;
        Status = text;
    }

    // For the preview tool, which has no engine: show a frame and a test result.
    internal void ShowSample(Bitmap frame, string read, bool matched)
    {
        Frame = frame;
        ReadText = read;
        Matched = matched;
    }

    private const string SampleIni = """
        [the_finals.kill]
        region = 0.65, 0.125, 0.33, 0.045
        words = ELIMINATED | KNOCKED OUT
        exclude = BY

        [the_finals.revive]
        region = 0.65, 0.172, 0.33, 0.045
        words = REVIVED

        [the_finals.match_end]
        region = 0.25, 0.32, 0.5, 0.2
        words = VICTORY | DEFEAT
        cooldown_ms = 60000
        """;
}

public sealed record EventOption(string Name, string Label, IBrush Brush)
{
    public override string ToString() => Label;
}

// One rule being edited, with its box on the frame.
public sealed class StudioRuleViewModel : ObservableObject
{
    private readonly StudioViewModel _owner;
    private bool _isSelected;

    internal StudioRuleViewModel(StudioRule rule, StudioViewModel owner)
    {
        Rule = rule;
        _owner = owner;
    }

    internal StudioRule Rule { get; }

    public IReadOnlyList<EventOption> Events => _owner.Events;

    public EventOption EventOption
    {
        get => _owner.Events.First(e => e.Name == Rule.Event);
        set
        {
            if (value is null || value.Name == Rule.Event) return;
            Rule.Event = value.Name;
            Changed(nameof(EventOption), nameof(Event), nameof(Label), nameof(Brush));
        }
    }

    public string Event => Rule.Event;
    public string Label => EventKinds.Label(Rule.Event);
    public IBrush Brush => EventKinds.Brush(Rule.Event);

    public string Words { get => Rule.Words; set { Rule.Words = value ?? ""; Changed(nameof(Words), nameof(Summary)); } }
    public string Exclude { get => Rule.Exclude; set { Rule.Exclude = value ?? ""; Changed(nameof(Exclude)); } }
    public string Summary => Rule.Words.Trim().Length > 0 ? Rule.Words : "No words yet";

    // 0 reads the box as it is; higher keeps only bright pixels, for text on busy backgrounds.
    public double Threshold
    {
        get => Rule.Threshold;
        set
        {
            var t = (int)Math.Clamp(Math.Round(value / 5) * 5, 0, 255);
            if (t == Rule.Threshold) return;
            Rule.Threshold = t;
            Changed(nameof(Threshold), nameof(ThresholdText));
        }
    }

    public string ThresholdText => Rule.Threshold == 0 ? "Off" : Rule.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public double CooldownSeconds
    {
        get => Rule.CooldownMs / 1000.0;
        set
        {
            var ms = (int)Math.Clamp(Math.Round(value * 2) / 2 * 1000, 0, 120_000);
            if (ms == Rule.CooldownMs) return;
            Rule.CooldownMs = ms;
            Changed(nameof(CooldownSeconds), nameof(CooldownText));
        }
    }

    public string CooldownText => $"{Rule.CooldownMs / 1000.0:0.#} s";

    // The box, as fractions of the screen.
    public (double Left, double Top, double Width, double Height) Region => (Rule.Left, Rule.Top, Rule.Width, Rule.Height);

    internal void SetRegion(double left, double top, double width, double height)
    {
        width = Math.Clamp(width, 0.01, 1);
        height = Math.Clamp(height, 0.01, 1);
        Rule.Left = Math.Clamp(left, 0, 1 - width);
        Rule.Top = Math.Clamp(top, 0, 1 - height);
        Rule.Width = width;
        Rule.Height = height;
        Changed(nameof(Region), nameof(RegionText));
    }

    public string RegionText => $"{Rule.Left:P0} from left, {Rule.Top:P0} from top, {Rule.Width:P0} × {Rule.Height:P0}";

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private void Changed(params string[] names)
    {
        foreach (var name in names) Raise(name);
        _owner.RuleChanged();
    }
}
