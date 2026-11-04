using System.Windows;
using System.Windows.Media.Animation;

namespace Cavdeck.App.Controls;

internal sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(GridLength), typeof(GridLengthAnimation),
        new PropertyMetadata(new GridLength(0)));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(GridLength), typeof(GridLengthAnimation),
        new PropertyMetadata(new GridLength(0)));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(
        object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var progress = animationClock.CurrentProgress ?? 1;
        var eased = 1 - Math.Pow(1 - progress, 3);
        return new GridLength(From.Value + (To.Value - From.Value) * eased);
    }
}
