using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Cavdeck.App.Controls;

public class FormField : ContentControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(FormField),
            new PropertyMetadata(string.Empty, OnChromeChanged));

    public static readonly DependencyProperty ErrorProperty =
        DependencyProperty.Register(
            nameof(Error),
            typeof(string),
            typeof(FormField),
            new PropertyMetadata(string.Empty, OnChromeChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Error
    {
        get => (string)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ApplyChrome();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (oldContent is DependencyObject previous)
        {
            InputAssist.SetIsInvalid(previous, false);
            AutomationProperties.SetHelpText(previous, string.Empty);
        }

        ApplyChrome();
    }

    private static void OnChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FormField field)
            field.ApplyChrome();
    }

    private void ApplyChrome()
    {
        if (Content is not DependencyObject child)
            return;

        var invalid = !string.IsNullOrEmpty(Error);
        InputAssist.SetIsInvalid(child, invalid);
        AutomationProperties.SetHelpText(child, Error ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(Label) && string.IsNullOrEmpty(AutomationProperties.GetName(child)))
            AutomationProperties.SetName(child, Label);
    }
}
