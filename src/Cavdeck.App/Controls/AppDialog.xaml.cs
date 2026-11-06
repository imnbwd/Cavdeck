using System.Windows;
using System.Windows.Automation;
using Cavdeck.App.Features.Diagnostics;

namespace Cavdeck.App.Controls;

public partial class AppDialog : Window
{
    public DialogChoice Choice { get; private set; } = DialogChoice.Dismiss;
    public Func<bool>? ConfirmPrimary { get; set; }
    internal Func<CancellationToken, Task<bool>>? ConfirmPrimaryAsync { get; init; }
    private CancellationTokenSource? _submission;

    public AppDialog(DialogRequest request)
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (Choice != DialogChoice.Primary)
                _submission?.Cancel();
        };
        Title = request.Title;
        Heading.Text = request.Title;

        if (string.IsNullOrWhiteSpace(request.Message))
            MessageText.Visibility = Visibility.Collapsed;
        else
            MessageText.Text = request.Message;

        if (request.Content is null)
            BodyHost.Visibility = Visibility.Collapsed;
        else
            BodyHost.Content = request.Content;
        if (request.Content is IDialogSubmitState submitState)
        {
            PrimaryAction.IsEnabled = submitState.CanSubmit;
            EventHandler update = (_, _) => PrimaryAction.IsEnabled = _submission is null && submitState.CanSubmit;
            submitState.CanSubmitChanged += update;
            Closed += (_, _) => submitState.CanSubmitChanged -= update;
        }

        PrimaryAction.Content = request.PrimaryText;
        // Enter should never silently confirm deletion or overwriting.
        PrimaryAction.IsDefault = DialogInteractionPolicy.IsPrimaryDefault(request.Tone);
        PrimaryAction.Style = (Style)FindResource(
            request.Tone == DialogTone.Danger ? "DangerButton" : "PrimaryButton");

        if (string.IsNullOrWhiteSpace(request.TertiaryText))
            TertiaryAction.Visibility = Visibility.Collapsed;
        else
        {
            TertiaryAction.Content = request.TertiaryText;
            TertiaryAction.IsCancel = true;
            AutomationProperties.SetName(TertiaryAction, request.TertiaryText);
        }

        if (string.IsNullOrWhiteSpace(request.SecondaryText))
        {
            SecondaryAction.Visibility = Visibility.Collapsed;
            CloseAction.IsCancel = true;
        }
        else
        {
            SecondaryAction.Content = request.SecondaryText;
            SecondaryAction.IsCancel = request.TertiaryText is null;
        }

        CloseAction.ToolTip = request.CloseText;
        AutomationProperties.SetName(CloseAction, request.CloseText);
        AutomationProperties.SetName(PrimaryAction, request.PrimaryText);
        if (!string.IsNullOrWhiteSpace(request.SecondaryText))
            AutomationProperties.SetName(SecondaryAction, request.SecondaryText);

        if (request.Tone == DialogTone.Danger)
            Loaded += (_, _) =>
            {
                // Focus a safe escape action when available; leave the destructive
                // action accessible to an explicit mouse or keyboard choice.
                if (SecondaryAction.Visibility == Visibility.Visible)
                    SecondaryAction.Focus();
                else if (TertiaryAction.Visibility == Visibility.Visible)
                    TertiaryAction.Focus();
                else
                    CloseAction.Focus();
            };
    }

    public void FitToWorkArea(double workWidthDips, double workHeightDips)
    {
        // Account for DPI-scaled working area, not full-screen physical pixels.
        if (!double.IsFinite(workWidthDips) || !double.IsFinite(workHeightDips)
            || workWidthDips <= 0 || workHeightDips <= 0)
            return;
        var maxWidth = Math.Max(1, workWidthDips - 24);
        MaxWidth = Math.Min(MaxWidth, maxWidth);
        MinWidth = Math.Min(MinWidth, MaxWidth);
        Width = Math.Min(Width, MaxWidth);
        MaxHeight = Math.Max(1, workHeightDips - 24);
        // Reserve the title, buttons and border padding before body scrolling.
        BodyScrollViewer.MaxHeight = Math.Min(360, Math.Max(1, MaxHeight - 176));
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmPrimaryAsync is { } confirm)
        {
            if (_submission is null)
                _ = SubmitAsync(confirm);
            return;
        }
        if (ConfirmPrimary is not null && !ConfirmPrimary())
            return;
        AcceptPrimary();
    }

    private async Task SubmitAsync(Func<CancellationToken, Task<bool>> confirm)
    {
        using var submission = new CancellationTokenSource();
        _submission = submission;
        PrimaryAction.IsEnabled = false;
        try
        {
            if (await confirm(submission.Token) && !submission.IsCancellationRequested)
                AcceptPrimary();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Current.Queue("ui", exception);
        }
        finally
        {
            _submission = null;
            if (IsVisible)
                PrimaryAction.IsEnabled = BodyHost.Content is not IDialogSubmitState state || state.CanSubmit;
        }
    }

    private void AcceptPrimary()
    {
        Choice = DialogChoice.Primary;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.Secondary;
        DialogResult = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.Dismiss;
        DialogResult = false;
    }

    private void Tertiary_Click(object sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.Dismiss;
        DialogResult = false;
    }
}
