using Avalonia;
using Avalonia.Controls;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

// Holds one teammate's video in the watch together grid: a video window of its own for the
// tile it's showing. Clicking the video listens to that view.
internal sealed class SquadVideoHost : Panel
{
    private SquadTile? _tile;
    private Control? _surface;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ReferenceEquals(_tile, DataContext)) return;
        if (_surface is not null)
        {
            Children.Remove(_surface);
            _surface = null;
        }
        _tile = DataContext as SquadTile;
        if (_tile is not { IsLive: true } tile || !OperatingSystem.IsWindows()) return;
        var surface = new VideoSurface();
        surface.PlayerChanged += player => tile.Attach(player);
        surface.Clicked += () => tile.ListenCommand.Execute(null);
        _surface = surface;
        Children.Insert(0, surface);
    }
}
