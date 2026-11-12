using System.Windows;
using System.Windows.Controls;

namespace Cavdeck.App.Controls;

public static class InputAssist
{
    public static readonly DependencyProperty ShowFocusIndicatorProperty =
        DependencyProperty.RegisterAttached(
            "ShowFocusIndicator",
            typeof(bool),
            typeof(InputAssist),
            new FrameworkPropertyMetadata(true));

    public static bool GetShowFocusIndicator(DependencyObject element) =>
        (bool)element.GetValue(ShowFocusIndicatorProperty);

    public static void SetShowFocusIndicator(DependencyObject element, bool value) =>
        element.SetValue(ShowFocusIndicatorProperty, value);

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached(
            "Placeholder",
            typeof(string),
            typeof(InputAssist),
            new FrameworkPropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element) =>
        (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) =>
        element.SetValue(PlaceholderProperty, value);

    public static readonly DependencyProperty IsInvalidProperty =
        DependencyProperty.RegisterAttached(
            "IsInvalid",
            typeof(bool),
            typeof(InputAssist),
            new FrameworkPropertyMetadata(false));

    public static bool GetIsInvalid(DependencyObject element) =>
        (bool)element.GetValue(IsInvalidProperty);

    public static void SetIsInvalid(DependencyObject element, bool value) =>
        element.SetValue(IsInvalidProperty, value);

    public static readonly DependencyProperty IsEmptyProperty =
        DependencyProperty.RegisterAttached(
            "IsEmpty",
            typeof(bool),
            typeof(InputAssist),
            new FrameworkPropertyMetadata(true));

    public static bool GetIsEmpty(DependencyObject element) =>
        (bool)element.GetValue(IsEmptyProperty);

    public static void SetIsEmpty(DependencyObject element, bool value) =>
        element.SetValue(IsEmptyProperty, value);

    // PasswordBox.Password is not a dependency property, so the style opts in to tracking.
    public static readonly DependencyProperty TrackPasswordProperty =
        DependencyProperty.RegisterAttached(
            "TrackPassword",
            typeof(bool),
            typeof(InputAssist),
            new PropertyMetadata(false, OnTrackPasswordChanged));

    public static bool GetTrackPassword(DependencyObject element) =>
        (bool)element.GetValue(TrackPasswordProperty);

    public static void SetTrackPassword(DependencyObject element, bool value) =>
        element.SetValue(TrackPasswordProperty, value);

    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.RegisterAttached(
            "Password",
            typeof(string),
            typeof(InputAssist),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public static string GetPassword(DependencyObject element) =>
        (string)element.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject element, string value) =>
        element.SetValue(PasswordProperty, value);

    private static readonly DependencyProperty UpdatingProperty =
        DependencyProperty.RegisterAttached(
            "Updating",
            typeof(bool),
            typeof(InputAssist),
            new PropertyMetadata(false));

    private static bool GetUpdating(DependencyObject element) => (bool)element.GetValue(UpdatingProperty);
    private static void SetUpdating(DependencyObject element, bool value) => element.SetValue(UpdatingProperty, value);

    private static void OnTrackPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box || e.NewValue is not true)
            return;

        box.PasswordChanged += PasswordChanged;
        SetIsEmpty(box, box.Password.Length == 0);
    }

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box || GetUpdating(box))
            return;

        var next = e.NewValue as string ?? "";
        SetIsEmpty(box, next.Length == 0);
        if (box.Password == next)
            return;

        SetUpdating(box, true);
        box.Password = next;
        SetUpdating(box, false);
    }

    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box || GetUpdating(box))
            return;

        SetUpdating(box, true);
        SetPassword(box, box.Password);
        SetIsEmpty(box, box.Password.Length == 0);
        SetUpdating(box, false);
    }
}
