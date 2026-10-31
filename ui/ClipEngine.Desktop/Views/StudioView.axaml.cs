using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

public partial class StudioView : UserControl
{
    public StudioView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }

    // Shortcuts. Typing in a box, or picking in a list or slider, keeps its own keys.
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not StudioViewModel vm) return;
        var typing = e.Source is TextBox;
        var control = e.KeyModifiers == KeyModifiers.Control;
        var command = e.Key switch
        {
            Key.S when control => vm.SaveCommand,
            Key.Enter when control => vm.TestCommand,
            Key.D when control && !typing => vm.DuplicateRuleCommand,
            _ when typing || e.Source is ComboBox or Slider or ListBoxItem or ListBox or RangeBase => null,
            Key.Delete when e.KeyModifiers == KeyModifiers.None => vm.DeleteRuleCommand,
            Key.Left when e.KeyModifiers == KeyModifiers.Shift => vm.SecondBackCommand,
            Key.Right when e.KeyModifiers == KeyModifiers.Shift => vm.SecondForwardCommand,
            Key.Left or Key.OemComma when e.KeyModifiers == KeyModifiers.None => vm.FrameBackCommand,
            Key.Right or Key.OemPeriod when e.KeyModifiers == KeyModifiers.None => vm.FrameForwardCommand,
            Key.Up when e.KeyModifiers == KeyModifiers.None => vm.PreviousMomentCommand,
            Key.Down when e.KeyModifiers == KeyModifiers.None => vm.NextMomentCommand,
            Key.Escape when e.KeyModifiers == KeyModifiers.None => vm.BackCommand,
            _ => null,
        };
        if (command is null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }
}
