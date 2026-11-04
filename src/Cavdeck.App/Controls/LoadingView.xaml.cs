using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cavdeck.App.Controls;

public enum LoadingMode
{
    Skeleton,
    Inline,
    Overlay,
}

public partial class LoadingView : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(LoadingView),
        new PropertyMetadata("", OnMessageChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode),
        typeof(LoadingMode),
        typeof(LoadingView),
        new PropertyMetadata(LoadingMode.Skeleton, OnMotionChanged));

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows),
        typeof(int),
        typeof(LoadingView),
        new PropertyMetadata(5, OnRowsChanged));

    public LoadingView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BuildSkeleton();
            Play();
        };
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Play();
            else
                Stop();
        };
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public LoadingMode Mode
    {
        get => (LoadingMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        AutomationProperties.SetName((LoadingView)sender, args.NewValue as string ?? "");
    }

    private static void OnRowsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (LoadingView)sender;
        if (!view.IsLoaded)
            return;
        view.BuildSkeleton();
    }

    private static void OnMotionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (LoadingView)sender;
        if (view.IsLoaded)
            view.Play();
    }

    private void BuildSkeleton()
    {
        SkeletonBars.Children.Clear();
        var count = Math.Clamp(Rows, 1, 12);
        double[] factors = [0.92, 0.68, 0.84, 0.54, 0.76];
        for (var i = 0; i < count; i++)
        {
            var factor = factors[i % factors.Length];
            var row = new Grid { Height = 36, Margin = new Thickness(0, 0, 0, i == count - 1 ? 0 : 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(factor, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.08, 1 - factor), GridUnitType.Star) });

            var mark = Placeholder();
            mark.Width = 16;
            mark.Height = 16;
            mark.CornerRadius = new CornerRadius(4);
            Grid.SetColumn(mark, 0);

            var line = Placeholder();
            line.Height = 10;
            line.CornerRadius = new CornerRadius(4);
            Grid.SetColumn(line, 2);

            row.Children.Add(mark);
            row.Children.Add(line);
            SkeletonBars.Children.Add(row);
        }
    }

    private static Border Placeholder()
    {
        var mark = new Border { VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
        mark.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        return mark;
    }

    private void Play()
    {
        Stop();
        if (!IsVisible || !Motion.AnimationsEnabled)
            return;

        if (Mode == LoadingMode.Skeleton)
        {
            var pulse = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            SkeletonBars.BeginAnimation(OpacityProperty, pulse);
            return;
        }

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        var target = Mode == LoadingMode.Overlay ? OverlaySpin : InlineSpin;
        target.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    private void Stop()
    {
        if (SkeletonBars is null)
            return;
        SkeletonBars.BeginAnimation(OpacityProperty, null);
        SkeletonBars.Opacity = 1;
        InlineSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        OverlaySpin.BeginAnimation(RotateTransform.AngleProperty, null);
        InlineSpin.Angle = 0;
        OverlaySpin.Angle = 0;
    }
}
