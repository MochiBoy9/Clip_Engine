using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClipEngine.Desktop.Views;

// Page changes: the old page fades out quickly and the new one fades in while rising a few pixels into place.
// Only opacity and a translate move, so the compositor redraws the page without laying it out again.
internal sealed class RiseTransition : IPageTransition
{
    public TimeSpan OutDuration { get; set; } = TimeSpan.FromMilliseconds(70);
    public TimeSpan InDuration { get; set; } = TimeSpan.FromMilliseconds(200);
    public double Distance { get; set; } = 12;

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        if (from is not null)
        {
            await Fade(1, 0, OutDuration, new LinearEasing()).RunAsync(from, cancellationToken);
            from.IsVisible = false;
        }

        if (to is null) return;
        to.IsVisible = true;
        var rise = new TranslateTransform(0, Distance);
        to.RenderTransform = rise;
        var easing = new CubicEaseOut();
        await Task.WhenAll(
            Fade(0, 1, InDuration, easing).RunAsync(to, cancellationToken),
            Move(Distance, 0, InDuration, easing).RunAsync(rise, cancellationToken));
        to.Opacity = 1;
        to.RenderTransform = null;
    }

    private static Animation Fade(double from, double to, TimeSpan duration, Easing easing) =>
        Frames(Visual.OpacityProperty, from, to, duration, easing);

    private static Animation Move(double from, double to, TimeSpan duration, Easing easing) =>
        Frames(TranslateTransform.YProperty, from, to, duration, easing);

    private static Animation Frames(AvaloniaProperty property, double from, double to, TimeSpan duration, Easing easing) => new()
    {
        Duration = duration,
        Easing = easing,
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(property, from) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(property, to) } },
        },
    };
}
