using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.App.Views;
using Cavdeck.Application.Settings;
using Cavdeck.Domain.Storage;
using Xunit;
using WpfApplication = System.Windows.Application;

namespace Cavdeck.App.Tests;

// Share the rendering fixture's WPF application. WPF allows one Application per test process.
internal static class GoToPathDialogChecks
{
    public static void Verify(WpfApplication app, bool accept)
    {
        var chromeButton = new Button();
        var editor = new TextBox();
        var owner = new Window
        {
            Width = 520, Height = 380, ShowInTaskbar = false,
            Content = new StackPanel { Children = { chromeButton, editor } }
        };
        app.MainWindow = owner;
        owner.Show();
        try
        {
            owner.UpdateLayout();
            Assert.True(ShellWindow.IsWorkspaceShortcutShellSource(owner, owner));
            Assert.True(ShellWindow.IsWorkspaceShortcutShellSource(chromeButton, owner));
            Assert.False(ShellWindow.IsWorkspaceShortcutShellSource(editor, owner));
            Assert.False(ShellWindow.IsWorkspaceShortcutShellSource(new Button(), owner));
            VerifyLanguages(app, accept);
        }
        finally
        {
            owner.Close();
        }
    }

    private static void VerifyLanguages(WpfApplication app, bool accept)
    {
        foreach (var language in AppLanguages.Supported)
        {
            var texts = new LocalizationService();
            var culture = CultureInfo.DefaultThreadCurrentCulture;
            var uiCulture = CultureInfo.DefaultThreadCurrentUICulture;
            try
            {
                texts.ChangeLanguage(language.Code);
            }
            finally
            {
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
            }

            VerifyDialog(app, texts, accept);
        }
    }

    private static void VerifyDialog(WpfApplication app, LocalizationService texts, bool accept)
    {
        var dialogs = new DialogService(texts);
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReply = new TaskCompletionSource<string?>();
        var submitted = new List<ExplorerPathInput>();
        CancellationToken submission = default;
        Exception? failure = null;

        var clearCount = 0;
        var saved = new ExplorerPathInput("assets/valid/", BucketPath: true);
        var data = new GoToPathPromptData("/current/",
            [new GoToPathSuggestion("/recent/", "s3://source/recent/", new("/recent/"))],
            [new GoToPathSuggestion(saved.Text, "s3://assets/valid/", saved)],
            () => { clearCount++; return Task.FromResult(true); });
        app.Dispatcher.InvokeAsync(() => ExerciseDialog(), DispatcherPriority.ApplicationIdle);
        var accepted = ExplorerPrompt.AskForPath(dialogs, texts, data, (input, token) =>
        {
            submitted.Add(input);
            submission = token;
            return submitted.Count == 1 ? reply.Task : accept ? Task.FromResult<string?>(null) : lateReply.Task;
        }, default);

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(accept == accepted,
            $"Unexpected dialog result for {texts.CurrentLanguage}, with {submitted.Count} submissions.");
        Assert.Equal(new[] { new ExplorerPathInput("missing/"), saved }, submitted);
        return;

        async void ExerciseDialog()
        {
            AppDialog? dialog = null;
            try
            {
                dialog = Assert.Single(app.Windows.OfType<AppDialog>(), window => window.IsVisible);
                var panel = Assert.IsType<StackPanel>(((ContentControl)dialog.FindName("BodyHost")).Content);
                var field = Assert.IsType<FormField>(panel.Children[0]);
                var status = Assert.IsType<TextBlock>(panel.Children[1]);
                var input = Assert.IsType<TextBox>(field.Content);
                var bucketMode = Assert.IsType<CheckBox>(panel.Children[2]);
                var modeHint = Assert.IsType<TextBlock>(panel.Children[3]);
                var suggestions = Assert.IsType<StackPanel>(panel.Children[4]);
                var history = Assert.IsType<StackPanel>(suggestions.Children[1]);
                var list = Assert.IsType<ListBox>(history.Children[1]);
                var clear = Assert.IsType<Button>(history.Children[2]);
                var primary = Assert.IsType<Button>(dialog.FindName("PrimaryAction"));
                var cancel = Assert.IsType<Button>(dialog.FindName("SecondaryAction"));
                var scope = PresentationSource.FromVisual(dialog);

                Assert.Equal(texts["ExplorerPath_Title"], dialog.Title);
                Assert.Equal(texts["ExplorerPath_Description"], ((TextBlock)dialog.FindName("MessageText")).Text);
                Assert.Equal(texts["ExplorerPath_Go"], primary.Content);
                Assert.Equal(texts["Dialog_Cancel"], cancel.Content);
                Assert.Equal("/current/", input.SelectedText);
                Assert.Equal(texts["ExplorerPath_Label"], AutomationProperties.GetName(input));
                Assert.Equal(texts["ExplorerPath_Placeholder"], InputAssist.GetPlaceholder(input));
                Assert.Equal(app.FindResource("SurfaceBrush"), dialog.Background);
                Assert.Equal(app.FindResource("MutedTextBrush"), status.Foreground);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                Assert.True(primary.IsDefault);
                Assert.True(cancel.IsCancel);
                Assert.False(bucketMode.IsChecked);
                Assert.Equal(Visibility.Collapsed, modeHint.Visibility);
                Assert.Equal(texts["ExplorerPath_History"], AutomationProperties.GetName(list));
                Assert.Equal(app.FindResource("InputBackgroundBrush"), list.Background);

                input.Text = "missing/";
                AccessKeyManager.ProcessKey(scope, "\r", false);
                Assert.Single(submitted);
                Assert.False(primary.IsEnabled);
                Assert.True(input.IsReadOnly);
                Assert.Equal(Visibility.Visible, status.Visibility);
                AccessKeyManager.ProcessKey(scope, "\r", false);
                Assert.Single(submitted);

                reply.SetResult(texts["ExplorerPath_NotFound"]);
                // Let the async submit continuation render inline validation before checking the view.
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(dialog.IsVisible);
                Assert.True(primary.IsEnabled);
                Assert.False(input.IsReadOnly);
                Assert.Equal("missing/", input.Text);
                Assert.Equal(texts["ExplorerPath_NotFound"], field.Error);
                Assert.Equal(field.Error, AutomationProperties.GetHelpText(input));
                list.SelectedIndex = 0;
                list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, scope, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                });
                Assert.Single(submitted);
                Assert.Equal(saved.Text, input.Text);
                Assert.True(bucketMode.IsChecked);
                Assert.Equal(Visibility.Visible, modeHint.Visibility);
                Assert.Empty(field.Error);
                var response = app.Dispatcher.InvokeAsync(() =>
                {
                    var confirmation = app.Windows.OfType<AppDialog>()
                        .FirstOrDefault(window => window != dialog && window.IsVisible);
                    try
                    {
                        Assert.NotNull(confirmation);
                        var confirm = Assert.IsType<Button>(confirmation.FindName("PrimaryAction"));
                        Assert.False(confirm.IsDefault);
                        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    finally
                    {
                        if (confirmation is { IsVisible: true })
                            confirmation.Close();
                    }
                }, DispatcherPriority.ApplicationIdle);
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await response.Task;
                Assert.Equal(1, clearCount);
                Assert.Single(suggestions.Children);
                AccessKeyManager.ProcessKey(scope, "\r", false);
                Assert.Equal(2, submitted.Count);
                if (!accept)
                {
                    Assert.True(dialog.IsVisible);
                    AccessKeyManager.ProcessKey(scope, "\u001b", false);
                    lateReply.SetResult(null);
                }
                Assert.False(dialog.IsVisible);
                Assert.Equal(!accept, submission.IsCancellationRequested);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (dialog is { IsVisible: true })
                    dialog.Close();
            }
        }
    }
}
