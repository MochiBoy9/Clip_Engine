using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

public partial class SquadPlayerView : UserControl
{
    public SquadPlayerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SquadPlayerViewModel vm || e.KeyModifiers != KeyModifiers.None) return;
        var command = e.Key switch
        {
            Key.Space or Key.K => vm.PlayPauseCommand,
            Key.Left or Key.J => vm.SkipBackCommand,
            Key.Right or Key.L => vm.SkipForwardCommand,
            Key.R => vm.ReplayCommand,
            >= Key.D1 and <= Key.D4 when e.Key - Key.D1 < vm.Tiles.Count => vm.Tiles[e.Key - Key.D1].ListenCommand,
            Key.Escape or Key.Back => vm.BackCommand,
            _ => null,
        };
        if (command is null) return;
        command.Execute(null);
        e.Handled = true;
    }
}
