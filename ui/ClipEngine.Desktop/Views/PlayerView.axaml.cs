using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

public partial class PlayerView : UserControl
{
    private PlayerViewModel? _vm;
    private Control? _surface;

    public PlayerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ReferenceEquals(_vm, DataContext)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as PlayerViewModel;
        if (_surface is not null)
        {
            VideoHost.Children.Remove(_surface);
            _surface = null;
        }
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelChanged;

        if (_vm.IsLive && OperatingSystem.IsWindows())
        {
            var vm = _vm;
            var surface = new VideoSurface();
            surface.PlayerChanged += player => vm.AttachPlayer(player);
            surface.Clicked += () => vm.PlayPauseCommand.Execute(null);
            _surface = surface;
            VideoHost.Children.Insert(0, surface);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }

    // The video window is drawn over everything, so it hides while an error shows in its place.
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.HasError) && _surface is not null && _vm is not null)
            _surface.IsVisible = !_vm.HasError;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox) return;
        var command = e.Key switch
        {
            Key.Space or Key.K => _vm.PlayPauseCommand,
            Key.Left or Key.J => _vm.SkipBackCommand,
            Key.Right or Key.L => _vm.SkipForwardCommand,
            Key.OemComma or Key.Up => _vm.PreviousMarkerCommand,
            Key.OemPeriod or Key.Down => _vm.NextMarkerCommand,
            Key.I => _vm.SetTrimStartCommand,
            Key.O => _vm.SetTrimEndCommand,
            Key.M when _vm.IsChecking => _vm.Accuracy.MarkCommand,
            >= Key.D1 and <= Key.D7 when _vm.IsChecking => PickMarkType(e.Key - Key.D1),
            Key.Escape when _vm.IsTrimming => _vm.CancelTrimCommand,
            Key.Escape when _vm.IsChecking => _vm.CheckCommand,
            Key.Escape or Key.Back => _vm.BackCommand,
            _ => null,
        };
        if (command is null) return;
        command.Execute(null);
        e.Handled = true;
    }

    // 1 to 7 pick what the next mark is, in the order of the list.
    private RelayCommand PickMarkType(int index)
    {
        var accuracy = _vm!.Accuracy;
        return new RelayCommand(() => accuracy.MarkType = accuracy.Events[Math.Clamp(index, 0, accuracy.Events.Count - 1)]);
    }
}
