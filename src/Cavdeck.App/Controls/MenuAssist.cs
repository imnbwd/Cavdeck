using System.Windows;

namespace Cavdeck.App.Controls;

public static class MenuAssist
{
    public static readonly DependencyProperty IsDangerProperty = DependencyProperty.RegisterAttached(
        "IsDanger",
        typeof(bool),
        typeof(MenuAssist),
        new FrameworkPropertyMetadata(false));

    public static bool GetIsDanger(DependencyObject element) => (bool)element.GetValue(IsDangerProperty);

    public static void SetIsDanger(DependencyObject element, bool value) => element.SetValue(IsDangerProperty, value);
}
