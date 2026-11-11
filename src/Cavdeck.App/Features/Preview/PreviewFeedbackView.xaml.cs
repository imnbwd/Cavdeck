using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cavdeck.App.Features.Preview;

/// <summary>
/// Shared, keyboard-accessible feedback surface for the preview pane, image viewer,
/// and media viewer. Uses theme resources and device-independent WPF dimensions.
/// Consumers own their cancellation, retry and resource lifetime.
/// </summary>
public partial class PreviewFeedbackView : UserControl
{
    public PreviewFeedbackView() => InitializeComponent();

    private static DependencyProperty Register<T>(string name, T initial) =>
        DependencyProperty.Register(name, typeof(T), typeof(PreviewFeedbackView),
            new PropertyMetadata(initial));

    public static readonly DependencyProperty HeadlineProperty =
        Register(nameof(Headline), "");
    public static readonly DependencyProperty DescriptionProperty =
        Register(nameof(Description), "");
    public static readonly DependencyProperty ProgressDescriptionProperty =
        Register(nameof(ProgressDescription), "");
    public static readonly DependencyProperty PrimaryButtonTextProperty =
        Register(nameof(PrimaryButtonText), "");
    public static readonly DependencyProperty SecondaryButtonTextProperty =
        Register(nameof(SecondaryButtonText), "");
    public static readonly DependencyProperty PrimaryCommandProperty =
        Register<ICommand?>(nameof(PrimaryCommand), null);
    public static readonly DependencyProperty SecondaryCommandProperty =
        Register<ICommand?>(nameof(SecondaryCommand), null);
    public static readonly DependencyProperty ShowPrimaryButtonProperty =
        Register(nameof(ShowPrimaryButton), false);
    public static readonly DependencyProperty ShowSecondaryButtonProperty =
        Register(nameof(ShowSecondaryButton), false);
    public static readonly DependencyProperty ShowProgressProperty =
        Register(nameof(ShowProgress), false);
    public static readonly DependencyProperty IsIndeterminateProperty =
        Register(nameof(IsIndeterminate), false);
    public static readonly DependencyProperty ProgressValueProperty =
        Register(nameof(ProgressValue), 0d);

    public string Headline
    {
        get => (string)GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string ProgressDescription
    {
        get => (string)GetValue(ProgressDescriptionProperty);
        set => SetValue(ProgressDescriptionProperty, value);
    }

    public string PrimaryButtonText
    {
        get => (string)GetValue(PrimaryButtonTextProperty);
        set => SetValue(PrimaryButtonTextProperty, value);
    }

    public string SecondaryButtonText
    {
        get => (string)GetValue(SecondaryButtonTextProperty);
        set => SetValue(SecondaryButtonTextProperty, value);
    }

    public ICommand? PrimaryCommand
    {
        get => (ICommand?)GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
    }

    public ICommand? SecondaryCommand
    {
        get => (ICommand?)GetValue(SecondaryCommandProperty);
        set => SetValue(SecondaryCommandProperty, value);
    }

    public bool ShowPrimaryButton
    {
        get => (bool)GetValue(ShowPrimaryButtonProperty);
        set => SetValue(ShowPrimaryButtonProperty, value);
    }

    public bool ShowSecondaryButton
    {
        get => (bool)GetValue(ShowSecondaryButtonProperty);
        set => SetValue(ShowSecondaryButtonProperty, value);
    }

    public bool ShowProgress
    {
        get => (bool)GetValue(ShowProgressProperty);
        set => SetValue(ShowProgressProperty, value);
    }

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public double ProgressValue
    {
        get => (double)GetValue(ProgressValueProperty);
        set => SetValue(ProgressValueProperty, value);
    }
}
