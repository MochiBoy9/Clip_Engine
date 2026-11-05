using System.Collections.ObjectModel;
using ClipEngine.Desktop.Services.Stats;

namespace ClipEngine.Desktop.ViewModels;

// The readings on the stats overlay. They're updated in place each second, so only the numbers redraw.
public sealed class StatsOverlayViewModel : ObservableObject
{
    public ObservableCollection<StatItemViewModel> Items { get; } = [];

    internal void Show(GameStats stats, StatsChoice choice)
    {
        var readouts = StatsReadouts.For(stats, choice);
        if (!readouts.Select(r => r.Label).SequenceEqual(Items.Select(i => i.Label)))
        {
            Items.Clear();
            foreach (var r in readouts) Items.Add(new StatItemViewModel(r.Label));
        }
        for (var i = 0; i < readouts.Count; i++) Items[i].Update(readouts[i]);
    }
}

public sealed class StatItemViewModel(string label) : ObservableObject
{
    private string _value = StatsReadouts.None;
    private bool _isWarn;
    private bool _isBad;

    public string Label { get; } = label;
    public string Value { get => _value; private set => Set(ref _value, value); }
    public bool IsWarn { get => _isWarn; private set => Set(ref _isWarn, value); }
    public bool IsBad { get => _isBad; private set => Set(ref _isBad, value); }

    internal void Update(StatReadout readout)
    {
        Value = readout.Value;
        IsWarn = readout.Level == StatLevel.Warn;
        IsBad = readout.Level == StatLevel.Bad;
    }
}
