using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

public partial class EditorView : UserControl
{
    // The bar beside a part in the list: lit for the picked part.
    public static readonly IValueConverter SelectedOpacity =
        new FuncValueConverter<bool, double>(selected => selected ? 1 : 0.18);

    private EditorViewModel? _vm;
    private Control? _surface;

    public EditorView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ReferenceEquals(_vm, DataContext)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as EditorViewModel;
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

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }

    // The video window is drawn over everything, so it hides while an error shows in its place.
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.HasError) && _surface is not null && _vm is not null)
            _surface.IsVisible = !_vm.HasError;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Source is TextBox) return;
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        var ctrlShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        var none = e.KeyModifiers == KeyModifiers.None;
        var command = e.Key switch
        {
            Key.Z when ctrl => _vm.UndoCommand,
            Key.Y when ctrl => _vm.RedoCommand,
            Key.Z when ctrlShift => _vm.RedoCommand,
            Key.D when ctrl => _vm.DuplicateCommand,
            Key.E when ctrl => _vm.ExportCommand,
            Key.B when ctrl => _vm.SplitCommand,
            _ when !none => null,
            Key.Space or Key.K => _vm.PlayPauseCommand,
            Key.Left or Key.J => _vm.SkipBackCommand,
            Key.Right or Key.L => _vm.SkipForwardCommand,
            Key.Up => _vm.PreviousCutCommand,
            Key.Down => _vm.NextCutCommand,
            Key.S => _vm.SplitCommand,
            Key.I => _vm.TrimStartHereCommand,
            Key.O => _vm.TrimEndHereCommand,
            Key.Delete or Key.Back => _vm.DeleteCommand,
            Key.OemPlus or Key.Add => _vm.ZoomInCommand,
            Key.OemMinus or Key.Subtract => _vm.ZoomOutCommand,
            Key.Escape => _vm.BackCommand,
            _ => null,
        };
        if (command is null) return;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }
}
