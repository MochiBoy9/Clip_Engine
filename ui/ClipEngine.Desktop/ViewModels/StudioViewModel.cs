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
// frame from any of your clips or long recordings (or another video or a screenshot), draw a box
// where the game shows the moment, type the words it shows, test it on the frame, and save.
//
// A game Clip Engine knows starts from its built-in rules, which can be changed like any others.
// Saving makes them the user's own for that game (in DetectionIni.UserFile), used instead of the
// built-in ones; every other game keeps the built-in rules, so updates to them still arrive.
public sealed class StudioViewModel : ObservableObject
{
    // The games built into the engine that are read off the screen (src/game/GameDetector.cpp).
    private static readonly StudioGameOption[] BuiltIn =
    [
        new("the_finals", "The Finals", false, ["Discovery.exe"]),
        new("overwatch", "Overwatch", false, ["Overwatch.exe"]),
        new("battlefield_6", "Battlefield 6", false, ["bf6.exe"]),
        new("valorant", "Valorant", false, ["VALORANT-Win64-Shipping.exe"]),
        new("fortnite", "Fortnite", false, ["FortniteClient-Win64-Shipping.exe"]),
        new("apex_legends", "Apex Legends", false, ["r5apex.exe", "r5apex_dx12.exe"]),
        new("pubg", "PUBG", false, ["TslGame.exe"]),
        new("marvel_rivals", "Marvel Rivals", false, ["Marvel-Win64-Shipping.exe"]),
        new("rainbow_six_siege", "Rainbow Six Siege", false, ["RainbowSix.exe", "RainbowSix_Vulkan.exe", "RainbowSix_DX11.exe"]),
        new("call_of_duty", "Call of Duty", false, ["cod.exe", "cod24-cod.exe", "cod25-cod.exe"]),
        new("arc_raiders", "ARC Raiders", false, ["PioneerGame.exe"]),
        new("delta_force", "Delta Force", false, ["DeltaForceClient-Win64-Shipping.exe"]),
        new("deadlock", "Deadlock", false, ["deadlock.exe", "project8.exe"]),
        new("halo_infinite", "Halo Infinite", false, ["HaloInfinite.exe"]),
        new("destiny_2", "Destiny 2", false, ["destiny2.exe"]),
        new("team_fortress_2", "Team Fortress 2", false, ["tf_win64.exe", "tf.exe"]),
        new("escape_from_tarkov", "Escape from Tarkov", false, ["EscapeFromTarkov.exe"]),
        new("roblox", "Roblox", false, ["RobloxPlayerBeta.exe"]),
        new("minecraft", "Minecraft", false, ["Minecraft.Windows.exe"]),
    ];

    public static IReadOnlyList<QualityOption> Qualities { get; } =
    [
        new("low", "Low"), new("medium", "Medium"), new("high", "High"), new("ultra", "Ultra"),
    ];

    private readonly MainViewModel _main;
    private readonly object _returnTo;
    private readonly DetectionIni _shipped;  // the rules that ship with the app
    private readonly DetectionIni _user;     // the user's changes, laid over them
    private bool _ownRules;                  // the rules shown are the user's, not the built-in ones
    private QualityOption _testQuality;
    private StudioGameOption? _game;
    private StudioRuleViewModel? _selectedRule;
    private ClipItemViewModel? _source;
    private string? _fileSource;  // a video or screenshot opened from disk
    private double? _fileLength;  // that video's length, once known
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
    private CancellationTokenSource? _workCancel;  // the read track or the test on marked clips, while it runs
    private bool _isWorking;
    private int _workProgress;
    private string? _trackSummary;
    private string? _markedSummary;
    private int _hudScale = 100;       // the game's HUD size, in percent, as set here
    private int _savedHudScale = 100;  // and as clip_engine.ini has it

    internal StudioViewModel(MainViewModel main, object returnTo, string? gameName = null, ClipItemViewModel? clip = null,
                             double seconds = 0)
    {
        _main = main;
        _returnTo = returnTo;
        _shipped = main.IsLive ? DetectionIni.LoadShipped() : DetectionIni.Parse(SampleIni);
        _user = main.IsLive ? DetectionIni.LoadUser() : DetectionIni.NewChanges();
        // Tests read the box the way the engine does at the quality picked in Settings.
        var quality = main.IsLive ? EngineSettings.Load(AppPaths.EngineSettingsFile).ReadingQuality : "medium";
        _testQuality = Qualities.FirstOrDefault(q => q.Name == quality) ?? Qualities[1];

        BackCommand = new RelayCommand(() => _main.ShowPage(_returnTo));
        AddRuleCommand = new RelayCommand(AddRule, () => _game is not null);
        DeleteRuleCommand = new RelayCommand(DeleteRule, () => _selectedRule is not null);
        TestCommand = new RelayCommand(() => _ = TestAsync(), () => _selectedRule is { IsSound: false } && HasFrameSource && !IsBusy);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => HasChanges && !IsBusy);
        LoadFrameCommand = new RelayCommand(() => _ = LoadFrameAsync(), () => HasFrameSource && !IsBusy);
        OpenImageCommand = new RelayCommand(() => _ = OpenFileAsync());
        UseBuiltInCommand = new RelayCommand(UseBuiltIn, () => CanUseBuiltIn);
        StartAddGameCommand = new RelayCommand(StartAddGame);
        CancelAddGameCommand = new RelayCommand(() => IsAddingGame = false);
        ConfirmAddGameCommand = new RelayCommand(ConfirmAddGame, () => CanAddGame);
        TrackCommand = new RelayCommand(() => _ = TrackAsync(),
                                        () => _selectedRule is { IsSound: false } && HasTimeline && !IsBusy && !IsWorking);
        TestMarkedCommand = new RelayCommand(() => _ = TestMarkedAsync(), () => _game is not null && !IsBusy && !IsWorking);
        CutSoundCommand = new RelayCommand(() => _ = CutSoundAsync(), () => _game is not null && HasTimeline && !IsBusy && !IsWorking);
        CancelWorkCommand = new RelayCommand(() => _workCancel?.Cancel());

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
    public RelayCommand UseBuiltInCommand { get; }
    public RelayCommand StartAddGameCommand { get; }
    public RelayCommand CancelAddGameCommand { get; }
    public RelayCommand ConfirmAddGameCommand { get; }
    public RelayCommand TrackCommand { get; }
    public RelayCommand TestMarkedCommand { get; }
    public RelayCommand CutSoundCommand { get; }
    public RelayCommand CancelWorkCommand { get; }

    // Reading the picked rule's box all through the clip, or testing the game's rules on every
    // clip with marks: what it found, and how far it has got.
    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (!Set(ref _isWorking, value)) return;
            NotifyWorkCommands();
        }
    }
    public int WorkProgress { get => _workProgress; private set => Set(ref _workProgress, value); }
    public ObservableCollection<TrackRowViewModel> Track { get; } = [];
    public bool HasTrack => Track.Count > 0 || _trackSummary is not null;
    public string? TrackSummary { get => _trackSummary; private set { if (Set(ref _trackSummary, value)) Raise(nameof(HasTrack)); } }
    public ObservableCollection<MarkedClipRow> MarkedResults { get; } = [];
    public string? MarkedSummary
    {
        get => _markedSummary;
        private set { if (Set(ref _markedSummary, value)) Raise(nameof(HasMarkedResults)); }
    }
    public bool HasMarkedResults => _markedSummary is not null;
    // Clips of this game with marks of what really happened, from Check detection in the player.
    public int MarkedClipCount => MarkedClips().Count;
    public string TestMarkedTip => MarkedClipCount switch
    {
        0 => "Mark what really happened in some clips first (Check detection in the player), then test the rules on all of them at once",
        1 => "Run the saved rules over the one clip you've marked and score them",
        var n => $"Run the saved rules over the {n} clips you've marked and score them together",
    };

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

    // Whose rules these are: the game's built-in ones, or the user's own in their place.
    public bool UsesOwnRules => _ownRules;
    public bool HasBuiltInRules => _game is not null && _shipped.RulesFor(_game.Id).Count > 0;
    private bool CanUseBuiltIn => HasBuiltInRules && (_ownRules || HasChanges);

    public string RulesOrigin => _game is null ? ""
        : _ownRules && HasBuiltInRules ? "Your own rules, used instead of the built-in ones."
        : _ownRules ? "Your own rules."
        : HasBuiltInRules ? (HasChanges ? "Built-in rules, changed. Save to use your version." : "Built-in rules. Change any of them and save to make them yours.")
        : "No rules yet.";

    // How big the game's HUD is set in the game, in percent. The boxes are drawn, tested and
    // used in game where a HUD that size puts them (ScreenPlacement), so one set of rules fits
    // every HUD size.
    public double HudScale
    {
        get => _hudScale;
        set
        {
            var percent = (int)Math.Clamp(Math.Round(value / 5) * 5, HudScales.Min, HudScales.Max);
            if (percent == _hudScale) return;
            _hudScale = percent;
            Raise(nameof(HudScale));
            Raise(nameof(HudScaleText));
            if (percent != _savedHudScale) HasChanges = true;
            Matched = null;
            ReadText = null;
            RegionsChanged();
        }
    }

    public string HudScaleText => $"{_hudScale}%";

    internal double HudFactor => _hudScale / 100.0;

    // The loaded frame's size in pixels, which the boxes are placed for: a frame from an
    // ultrawide or a 16:10 screen shows them where the engine looks on that screen.
    internal (double Width, double Height) FrameSize => _frame is { } f ? (f.PixelSize.Width, f.PixelSize.Height) : (0, 0);

    private static int LoadHudScale(string gameId)
    {
        try
        {
            return HudScales.For(AppPaths.EngineSettingsFile, gameId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 100;
        }
    }

    // The reading quality tests use.
    public QualityOption TestQuality
    {
        get => _testQuality;
        set
        {
            if (value is null || !Set(ref _testQuality, value)) return;
            Matched = null;
            ReadText = null;
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
            NotifyWorkCommands();
            Track.Clear();
            TrackSummary = null;
            RegionsChanged();
        }
    }

    public bool HasSelectedRule => _selectedRule is not null;
    public bool HasRules => Rules.Count > 0;

    // Where the frame comes from: any of the user's clips or long recordings at a moment, or a
    // video or screenshot opened from disk.
    public ClipItemViewModel? SelectedSource
    {
        get => _source;
        set
        {
            var samePath = value?.Path == _source?.Path;
            if (!Set(ref _source, value) && _fileSource is null && _frame is not null) return;
            if (value is null && _fileSource is not null) return;
            _fileSource = null;
            _fileLength = null;
            if (!samePath) _frameSeconds = 0;
            SourceChanged();
            if (value is not null && _main.IsLive) _ = LoadFrameAsync();
        }
    }

    public bool HasFrameSource => _source is not null || _fileSource is not null;
    public bool IsClipSource => _fileSource is null && _source is not null;
    // A clip, or a video opened from disk: something with a slider to find the frame.
    public bool HasTimeline => IsClipSource || _fileLength is > 0;
    public string? FileSourceName => _fileSource is null ? null : Path.GetFileName(_fileSource);
    public bool HasFileSource => _fileSource is not null;

    public double SourceDuration => _fileSource is not null ? _fileLength ?? 0
        : _source?.File.Duration?.TotalSeconds ?? _source?.File.Metadata?.Seconds ?? 30;

    private void SourceChanged()
    {
        Raise(nameof(SelectedSource));
        Raise(nameof(FrameSeconds));
        Raise(nameof(FrameTimeText));
        Raise(nameof(SourceDuration));
        Raise(nameof(SourceMoments));
        Raise(nameof(HasFrameSource));
        Raise(nameof(IsClipSource));
        Raise(nameof(HasTimeline));
        Raise(nameof(FileSourceName));
        Raise(nameof(HasFileSource));
        LoadFrameCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        NotifyWorkCommands();
        Track.Clear();
        TrackSummary = null;
    }

    public double FrameSeconds
    {
        get => _frameSeconds;
        set
        {
            if (!Set(ref _frameSeconds, Math.Round(Math.Max(0, value), 1))) return;
            Raise(nameof(FrameTimeText));
            // Show the new frame once the slider settles, rather than on every step of a drag.
            if (!_main.IsLive || !HasTimeline) return;
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
    public IReadOnlyList<MarkerViewModel> SourceMoments => _fileSource is not null ? [] : _source?.Events
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
        private set
        {
            if (!Set(ref _frame, value)) return;
            Raise(nameof(HasFrame));
            RegionsChanged();
        }
    }

    public bool HasFrame => _frame is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            NotifyWorkCommands();
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
        private set
        {
            if (!Set(ref _hasChanges, value)) return;
            SaveCommand.NotifyCanExecuteChanged();
            UseBuiltInCommand.NotifyCanExecuteChanged();
            Raise(nameof(RulesOrigin));
        }
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
        foreach (var game in _user.Games.Concat(_shipped.Games).Where(g => BuiltIn.All(b => b.Id != g.Id)).DistinctBy(g => g.Id))
            Games.Add(new StudioGameOption(game.Id, game.Name, true, game.Executables));
    }

    private void LoadRules()
    {
        if (_game is null) return;
        _ownRules = _user.EditedGames.Contains(_game.Id);
        _savedHudScale = _hudScale = _main.IsLive ? LoadHudScale(_game.Id) : 100;
        Raise(nameof(HudScale));
        Raise(nameof(HudScaleText));
        ShowRules((_ownRules ? _user : _shipped).RulesFor(_game.Id));
        HasChanges = false;
    }

    // Picks the rule with this detection.ini section, as when opened from a moment it found.
    internal void SelectRule(string section)
    {
        var rule = Rules.FirstOrDefault(r => string.Equals(r.Rule.Section, section, StringComparison.OrdinalIgnoreCase));
        if (rule is not null) SelectedRule = rule;
    }

    private void ShowRules(IEnumerable<StudioRule> rules)
    {
        Rules.Clear();
        foreach (var rule in rules) Rules.Add(new StudioRuleViewModel(rule, this));
        SelectedRule = Rules.FirstOrDefault();
        Raise(nameof(HasRules));
        Raise(nameof(GameCaption));
        RaiseOrigin();
        RegionsChanged();
    }

    private void RaiseOrigin()
    {
        Raise(nameof(UsesOwnRules));
        Raise(nameof(HasBuiltInRules));
        Raise(nameof(RulesOrigin));
        UseBuiltInCommand.NotifyCanExecuteChanged();
    }

    // Puts the built-in rules back in the editor; saving then drops the user's own for this game.
    private void UseBuiltIn()
    {
        if (_game is null) return;
        // Parsed afresh, so editing them doesn't change the copy kept for comparing.
        ShowRules(DetectionIni.Parse(_shipped.ToString()).RulesFor(_game.Id));
        HasChanges = _ownRules;
        ShowStatus(_ownRules ? $"These are {_game.Name}'s built-in rules. Save to use them instead of yours." : null);
    }

    private void LoadSources()
    {
        Sources.Clear();
        if (_game is null) return;
        // Every clip and long recording, the game's own first and those with moments first among
        // them: their frames show what the rules look for. Other games' are there too, for a game
        // recorded under another name or a HUD shared between games.
        var game = _game.Name;
        foreach (var clip in _main.Clips
                     .OrderByDescending(c => string.Equals(c.Game, game, StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(c => c.Events.Count > 0))
        {
            Sources.Add(clip);
        }
        Raise(nameof(MarkedClipCount));
        Raise(nameof(TestMarkedTip));
        if (_fileSource is not null) return;
        SelectedSource = Sources.FirstOrDefault(c => c.Path == _source?.Path)
                         ?? Sources.FirstOrDefault(c => string.Equals(c.Game, game, StringComparison.OrdinalIgnoreCase))
                         ?? Sources.FirstOrDefault();
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

    private string? FrameSourcePath => _fileSource ?? _source?.Path;
    private double FrameSourceSeconds => HasTimeline ? _frameSeconds : 0;

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
            var (png, length) = await StudioTools.GrabFrameAsync(path, FrameSourceSeconds);
            if (path == _fileSource && length is > 0 && _fileLength is null)
            {
                _fileLength = length;
                Raise(nameof(SourceDuration));
                Raise(nameof(HasTimeline));
            }
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

    // Any video (one recorded with something else, say) or a screenshot.
    private async Task OpenFileAsync()
    {
        if (_main.Dialogs is null || await _main.Dialogs.PickFrameSourceAsync() is not { } path) return;
        _fileSource = path;
        _fileLength = null;
        _frameSeconds = 0;
        _source = null;
        SourceChanged();
        await LoadFrameAsync();
    }

    private async Task TestAsync()
    {
        if (_selectedRule is null || FrameSourcePath is not { } path) return;
        IsBusy = true;
        try
        {
            var (text, matches) = await StudioTools.ReadAsync(path, FrameSourceSeconds, _selectedRule.Rule, _testQuality.Name, _hudScale);
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
        var problem = Rules.FirstOrDefault(r => r.Words.Trim().Length == 0 && r.Rule.Image is null && !r.IsSound);
        if (problem is not null)
        {
            SelectedRule = problem;
            ShowStatus("Each rule needs at least one word to look for.", error: true);
            return;
        }

        // Several rules for one event need their own section names: [game.kill], [game.kill.2], ...
        // A rule keeps its name (like the built-in [game.kill.score]) unless another has it.
        foreach (var group in Rules.GroupBy(r => r.Event))
        {
            var used = new HashSet<string>();
            foreach (var rule in group)
            {
                if (used.Add(rule.Rule.Suffix)) continue;
                var n = 2;
                while (!used.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture))) n++;
                rule.Rule.Suffix = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        if (_main.IsLive && _hudScale != _savedHudScale)
        {
            try
            {
                HudScales.Save(AppPaths.EngineSettingsFile, _game.Id, _hudScale);
                _savedHudScale = _hudScale;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ShowStatus($"Couldn't save the HUD size: {e.Message}", error: true);
                return;
            }
        }
        var custom = _game.IsCustom ? new StudioGame(_game.Id, _game.Name, _game.Executables) : null;
        var rules = Rules.Select(r => r.Rule).ToList();
        // The same as the built-in rules: drop the user's copy, so later fixes to them arrive.
        var builtIn = custom is null && DetectionIni.RulesText(rules) == _shipped.RulesText(_game.Id);
        if (builtIn) _user.RemoveGame(_game.Id);
        else _user.SetGame(_game.Id, custom, rules);
        _ownRules = !builtIn;
        RaiseOrigin();
        if (!_main.IsLive)
        {
            HasChanges = false;
            return;
        }
        try
        {
            _user.SaveToUserFile();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn't save: {e.Message}", error: true);
            return;
        }
        HasChanges = false;
        ShowStatus(builtIn
            ? $"{_game.Name} uses its built-in rules again. The engine is restarting to use them."
            : $"Saved your rules for {_game.Name} to {DetectionIni.UserFile}. The engine is restarting to use them.");
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

    private void NotifyWorkCommands()
    {
        TrackCommand.NotifyCanExecuteChanged();
        TestMarkedCommand.NotifyCanExecuteChanged();
        CutSoundCommand.NotifyCanExecuteChanged();
    }

    private List<ClipItemViewModel> MarkedClips() => _game is null ? [] : _main.Clips
        .Where(c => string.Equals(c.Game, _game.Name, StringComparison.OrdinalIgnoreCase) && c.File.Metadata?.Labels is { Count: > 0 })
        .ToList();

    // What the picked rule reads all through the clip, each time it changes, so it's clear
    // where it reads the moment, where it misreads it and where it reads something else.
    private async Task TrackAsync()
    {
        if (_selectedRule is not { IsSound: false } rule || FrameSourcePath is not { } path) return;
        IsWorking = true;
        WorkProgress = 0;
        Track.Clear();
        TrackSummary = null;
        _workCancel = new CancellationTokenSource();
        ShowStatus("Reading the box all through the clip…");
        try
        {
            var reads = await StudioTools.TrackAsync(path, rule.Rule, _testQuality.Name, _hudScale, 250,
                p => Avalonia.Threading.Dispatcher.UIThread.Post(() => WorkProgress = p), _workCancel.Token);
            foreach (var read in reads)
            {
                Track.Add(new TrackRowViewModel(read.Seconds, read.Text, read.Matches,
                                                new RelayCommand(() => FrameSeconds = read.Seconds)));
            }
            var matched = reads.Count(r => r.Matches > 0);
            TrackSummary = reads.Count == 0
                ? "It read nothing anywhere in the clip."
                : $"{reads.Count} changes in what it read; {matched} {(matched == 1 ? "matches" : "match")} the rule. Click one to see that frame.";
            Raise(nameof(HasTrack));
            ShowStatus(null);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(null);
        }
        catch (Exception e) when (e is StudioFailedException or IOException or System.ComponentModel.Win32Exception)
        {
            ShowStatus($"Couldn't read the clip: {e.Message}", error: true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    // Runs the game's saved rules over every clip marked in Check detection, the way the engine
    // reads the game, and scores them together: one bad clip can't hide among good ones.
    private async Task TestMarkedAsync()
    {
        if (_game is null) return;
        var clips = MarkedClips();
        MarkedResults.Clear();
        MarkedSummary = null;
        if (clips.Count == 0)
        {
            ShowStatus($"No {_game.Name} clips are marked yet. Open one, use Check detection to mark what really happened, then test here.");
            return;
        }
        IsWorking = true;
        WorkProgress = 0;
        _workCancel = new CancellationTokenSource();
        ShowStatus(HasChanges ? "Testing the saved rules; save to test your changes too." : null);
        var quality = _main.IsLive ? EngineSettings.Load(AppPaths.EngineSettingsFile).ReadingQuality : "medium";
        var marked = new List<ClipEvent>();
        var found = new List<ClipEvent>();
        try
        {
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                var index = i;
                var events = await StudioTools.ScanAsync(clip.Path, _game.Name, quality,
                    p => Avalonia.Threading.Dispatcher.UIThread.Post(() => WorkProgress = (index * 100 + p) / clips.Count),
                    _workCancel.Token);
                var labels = clip.File.Metadata?.Labels ?? [];
                var result = DetectionAccuracy.Compare(labels, events);
                MarkedResults.Add(new MarkedClipRow(clip.Title, result.Summary, result.Missed.Count + result.Extra.Count == 0,
                                                    new RelayCommand(() => SelectedSource = clip)));
                // Offset each clip's times so moments from different clips never pair up.
                var offset = i * 100_000.0;
                marked.AddRange(labels.Select(l => new ClipEvent { Type = l.Type, Time = l.Time + offset }));
                found.AddRange(events.Select(e => new ClipEvent { Type = e.Type, Time = e.Time + offset }));
            }
            var total = DetectionAccuracy.Compare(marked, found);
            MarkedSummary = $"{clips.Count} {(clips.Count == 1 ? "clip" : "clips")}: {total.Summary}";
        }
        catch (OperationCanceledException)
        {
            ShowStatus(null);
        }
        catch (Exception e) when (e is StudioFailedException or IOException or System.ComponentModel.Win32Exception)
        {
            ShowStatus($"Couldn't test the rules: {e.Message}", error: true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    // Cuts the sound at the frame (from just before it to a second after) out of the clip, and
    // adds a rule that listens for it. It backs up the game's other rules for the same moment
    // rather than counting alone, as game sounds can be close to other sounds.
    private async Task CutSoundAsync()
    {
        if (_game is null || FrameSourcePath is not { } path) return;
        var eventName = _selectedRule?.Event ?? "kill";
        IsBusy = true;
        try
        {
            var folder = Path.Combine(AppPaths.DataDir, "sounds");
            var n = 1;
            string file;
            do file = Path.Combine(folder, $"{_game.Id}_{eventName}_{n++}.wav"); while (File.Exists(file));
            var saved = await StudioTools.CutSoundAsync(path, FrameSourceSeconds, file);

            var rule = new StudioRule { Game = _game.Id, Event = eventName, Suffix = "sound" };
            rule.SetSetting("sound", saved);
            rule.SetSetting("confirm_only", "true");
            rule.SetSetting("merge_ms", "4000");
            // The rules it backs up have to group with it.
            foreach (var other in Rules.Where(r => r.Event == eventName && r.Rule.Setting("merge_ms") is null))
                other.Rule.SetSetting("merge_ms", "4000");
            var added = new StudioRuleViewModel(rule, this);
            Rules.Add(added);
            SelectedRule = added;
            Raise(nameof(HasRules));
            RuleChanged();
            ShowStatus($"Added a rule that listens for the sound at {FrameTimeText}. Start the frame just as the sound plays. Save to use it.");
        }
        catch (Exception e) when (e is StudioFailedException or IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            ShowStatus($"Couldn't cut the sound: {e.Message}", error: true);
        }
        finally
        {
            IsBusy = false;
        }
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

    // For the preview tool: what reading through the clip and testing on marked clips show.
    internal void ShowSampleTools()
    {
        foreach (var (at, text, matches) in new[]
                 {
                     (10.2, "", 0), (12.0, "ENTERRO ELIMINATED VEX", 1), (13.4, "ENTERRO ELIMlNATED VEX", 1),
                     (15.1, "ELIMINATED BY VEX", 0), (17.6, "", 0),
                 })
        {
            Track.Add(new TrackRowViewModel(at, text, matches, new RelayCommand(() => FrameSeconds = at)));
        }
        TrackSummary = "5 changes in what it read; 2 match the rule. Click one to see that frame.";
        MarkedResults.Add(new MarkedClipRow("3 kills", "Caught 3 of 3 (100%), no false alarms.", true, new RelayCommand(() => { })));
        MarkedResults.Add(new MarkedClipRow("2 kills, 1 death", "Caught 2 of 3 (66%), 1 false alarm.", false, new RelayCommand(() => { })));
        MarkedSummary = "2 clips: Caught 5 of 6 (83%), 1 false alarm.";
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

// What the picked rule read at one time in the clip, from Read through the clip.
public sealed class TrackRowViewModel(double seconds, string text, int matches, RelayCommand seek)
{
    public string TimeText { get; } = Format.Duration(TimeSpan.FromSeconds(seconds)) + $".{(int)(seconds * 10) % 10}";
    public string Text { get; } = text.Length > 0 ? text : "(nothing)";
    public bool IsMatch { get; } = matches > 0;
    public RelayCommand SeekCommand { get; } = seek;
}

// How the rules did on one marked clip.
public sealed record MarkedClipRow(string Title, string Summary, bool IsPerfect, RelayCommand OpenCommand);

public sealed record EventOption(string Name, string Label, IBrush Brush)
{
    public override string ToString() => Label;
}

public sealed record QualityOption(string Name, string Label)
{
    public override string ToString() => Label;
}

// Where on a row of text a rule's words must be (position in detection.ini).
public sealed record PositionOption(string Name, string Label)
{
    public static IReadOnlyList<PositionOption> All { get; } =
    [
        new("anywhere", "Anywhere in the box"),
        new("start", "Start of a row (the killer, in a killfeed)"),
        new("end", "End of a row (who died, in a killfeed)"),
    ];

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

    public IReadOnlyList<PositionOption> Positions => PositionOption.All;

    public PositionOption Position
    {
        get => PositionOption.All.FirstOrDefault(p => p.Name == Rule.Position) ?? PositionOption.All[0];
        set
        {
            if (value is null || value.Name == Rule.Position) return;
            Rule.SetSetting("position", value.Name == "anywhere" ? null : value.Name);
            Changed(nameof(Position));
        }
    }
    public string Exclude { get => Rule.Exclude; set { Rule.Exclude = value ?? ""; Changed(nameof(Exclude)); } }
    public string Summary => IsSound ? $"Listens for {SoundText}"
        : Rule.Words.Trim().Length > 0 ? Rule.Words : Rule.Image is not null ? $"Looks for {Path.GetFileName(Rule.Image)}" : "No words yet";

    // A rule that listens to the game's audio for a sound cut from a clip, instead of a box.
    public bool IsSound => Rule.Setting("sound") is not null;
    public bool IsNotSound => !IsSound;
    public string SoundText => string.Join(", ", (Rule.Setting("sound") ?? "")
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFileName));

    // Never counts a moment alone, only backs up the game's other rules for it.
    public bool ConfirmOnly
    {
        get => Rule.Setting("confirm_only") is "true" or "yes" or "1";
        set
        {
            if (value == ConfirmOnly) return;
            Rule.SetSetting("confirm_only", value ? "true" : null);
            Changed(nameof(ConfirmOnly));
        }
    }

    // How alike the audio must be to the sound, 0.3 to 0.9.
    public double SoundMatch
    {
        get => double.TryParse(Rule.Setting("sound_match"), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0.44;
        set
        {
            var m = Math.Clamp(Math.Round(value, 2), 0.3, 0.9);
            if (Math.Abs(m - SoundMatch) < 0.001) return;
            Rule.SetSetting("sound_match", m.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            Changed(nameof(SoundMatch), nameof(SoundMatchText));
        }
    }
    public string SoundMatchText => SoundMatch.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

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

    // The box as fractions of the frame: where the engine looks on a screen the frame's shape,
    // with the HUD at the size set in Studio.
    public (double Left, double Top, double Width, double Height) Region
    {
        get
        {
            var (width, height) = _owner.FrameSize;
            var box = ScreenPlacement.Place(new(Rule.Left, Rule.Top, Rule.Width, Rule.Height), Rule.Setting("anchor"),
                                            Rule.Setting("aspect"), width, height, _owner.HudFactor);
            return (box.Left, box.Top, box.Width, box.Height);
        }
    }

    // A box drawn on the frame, saved as made for a screen that shape with the HUD at 100%.
    internal void SetRegion(double left, double top, double width, double height)
    {
        width = Math.Clamp(width, 0.01, 1);
        height = Math.Clamp(height, 0.01, 1);
        var drawn = new ScreenPlacement.Box(Math.Clamp(left, 0, 1 - width), Math.Clamp(top, 0, 1 - height), width, height);
        var (frameWidth, frameHeight) = _owner.FrameSize;
        if (frameWidth > 0 || _owner.HudFactor != 1)
        {
            var (box, aspect, anchor) = ScreenPlacement.Unplace(drawn, Rule.Setting("anchor"), frameWidth, frameHeight, _owner.HudFactor);
            drawn = box;
            Rule.SetSetting("aspect", aspect);
            Rule.SetSetting("anchor", anchor);
        }
        Rule.Left = drawn.Left;
        Rule.Top = drawn.Top;
        Rule.Width = drawn.Width;
        Rule.Height = drawn.Height;
        Changed(nameof(Region), nameof(RegionText));
    }

    public string RegionText
    {
        get
        {
            var (left, top, width, height) = Region;
            return $"{left:P0} from left, {top:P0} from top, {width:P0} × {height:P0}";
        }
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private void Changed(params string[] names)
    {
        foreach (var name in names) Raise(name);
        _owner.RuleChanged();
    }
}
