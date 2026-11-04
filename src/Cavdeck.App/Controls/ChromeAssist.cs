using System.Windows;
using System.Windows.Media;

namespace Cavdeck.App.Controls;

// Per-variant brushes for the shared button chrome, so one template serves every tone.
public static class ChromeAssist
{
    public static readonly DependencyProperty HoverBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "HoverBackground",
            typeof(Brush),
            typeof(ChromeAssist),
            new FrameworkPropertyMetadata(null));

    public static Brush? GetHoverBackground(DependencyObject element) =>
        (Brush?)element.GetValue(HoverBackgroundProperty);

    public static void SetHoverBackground(DependencyObject element, Brush? value) =>
        element.SetValue(HoverBackgroundProperty, value);

    public static readonly DependencyProperty FocusBrushProperty =
        DependencyProperty.RegisterAttached(
            "FocusBrush",
            typeof(Brush),
            typeof(ChromeAssist),
            new FrameworkPropertyMetadata(null));

    public static Brush? GetFocusBrush(DependencyObject element) =>
        (Brush?)element.GetValue(FocusBrushProperty);

    public static void SetFocusBrush(DependencyObject element, Brush? value) =>
        element.SetValue(FocusBrushProperty, value);

    public static readonly DependencyProperty HoverForegroundProperty =
        DependencyProperty.RegisterAttached(
            "HoverForeground",
            typeof(Brush),
            typeof(ChromeAssist),
            new FrameworkPropertyMetadata(null));

    public static Brush? GetHoverForeground(DependencyObject element) =>
        (Brush?)element.GetValue(HoverForegroundProperty);

    public static void SetHoverForeground(DependencyObject element, Brush? value) =>
        element.SetValue(HoverForegroundProperty, value);
}
