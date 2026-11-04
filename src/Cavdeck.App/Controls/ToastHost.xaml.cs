using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Cavdeck.App.Controls;

public partial class ToastHost : UserControl
{
    public ToastHost()
    {
        InitializeComponent();
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement card)
            return;

        if (!Motion.AnimationsEnabled)
        {
            card.Opacity = 1;
            return;
        }

        card.Opacity = 0;
        var slide = new TranslateTransform(0, 8);
        card.RenderTransform = slide;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(150);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem item })
            item.Pause();
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem item } card &&
            !card.IsKeyboardFocusWithin)
            item.Resume();
    }

    private void Card_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem item })
            item.Pause();
    }

    private void Card_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem item } card &&
            !card.IsKeyboardFocusWithin && !card.IsMouseOver)
            item.Resume();
    }

    private void Announcement_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock { DataContext: ToastItem item } title)
            return;

        RaiseLiveRegionChanged(title, () => item.TryMarkAnnounced());
    }

    private void CopyFeedback_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is not TextBlock { DataContext: ToastItem item } feedback ||
            string.IsNullOrWhiteSpace(item.CopyFeedback))
            return;

        RaiseLiveRegionChanged(feedback, () => !item.IsClosing && !item.IsDisposed);
    }

    private static void RaiseLiveRegionChanged(TextBlock text, Func<bool> shouldAnnounce)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            return;

        // Let the template, data bindings and visual tree settle first.
        // Never activate or focus the Toast for a screen reader announcement.
        _ = text.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!text.IsLoaded || !shouldAnnounce())
                return;

            var peer = UIElementAutomationPeer.FromElement(text)
                ?? UIElementAutomationPeer.CreatePeerForElement(text);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }));
    }
}
