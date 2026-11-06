using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

internal static class ExplorerPrompt
{
    public static bool AskForPath(DialogService dialogs, LocalizationService texts, GoToPathPromptData data,
        Func<ExplorerPathInput, CancellationToken, Task<string?>> validate, CancellationToken cancellationToken)
    {
        var box = CreateInput(data.Initial, texts["ExplorerPath_Label"]);
        InputAssist.SetPlaceholder(box, texts["ExplorerPath_Placeholder"]);
        var field = new FormField { Label = texts["ExplorerPath_Label"], Content = box };
        var status = new TextBlock
        {
            Text = texts["ExplorerPath_Loading"], TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var bucketMode = new CheckBox
        {
            Content = texts["ExplorerPath_BucketMode"],
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = texts["ExplorerPath_BucketModeHint"]
        };
        var modeHint = new TextBlock
        {
            Text = texts["ExplorerPath_BucketModeHint"], TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed
        };
        modeHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(bucketMode, texts["ExplorerPath_BucketMode"]);
        AutomationProperties.SetHelpText(bucketMode, texts["ExplorerPath_BucketModeHint"]);
        var fileOnly = false;
        box.TextChanged += (_, _) =>
        {
            field.Error = "";
            fileOnly = false;
        };
        void UpdateMode()
        {
            field.Error = "";
            fileOnly = false;
            modeHint.Visibility = bucketMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        bucketMode.Checked += (_, _) => UpdateMode();
        bucketMode.Unchecked += (_, _) => UpdateMode();

        void Fill(ExplorerPathInput input)
        {
            field.Error = "";
            bucketMode.IsChecked = input.BucketPath;
            box.Text = input.Text;
            fileOnly = input.FileOnly;
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }

        var suggestions = new StackPanel();
        if (data.Recent.Count > 0)
            suggestions.Children.Add(PathSuggestions(texts["ExplorerPath_Recent"], data.Recent, Fill));
        if (data.History.Count > 0 || data.ClearHistory is not null)
        {
            var history = PathSuggestions(texts["ExplorerPath_History"], data.History, Fill);
            suggestions.Children.Add(history);
            if (data.ClearHistory is { } clearHistory)
            {
                var clear = new Button
                {
                    Content = texts["ExplorerPath_ClearHistory"],
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                clear.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
                AutomationProperties.SetName(clear, texts["ExplorerPath_ClearHistory"]);
                clear.Click += async (_, _) =>
                {
                    if (dialogs.Show(Window.GetWindow(clear), new DialogRequest
                        {
                            Title = texts["ExplorerPath_ClearHistory"],
                            Message = texts["ExplorerPath_ClearHistoryConfirm"],
                            PrimaryText = texts["ExplorerPath_ClearHistory"],
                            SecondaryText = texts["Dialog_Cancel"],
                            Tone = DialogTone.Danger
                        }) != true)
                        return;
                    clear.IsEnabled = false;
                    if (await clearHistory())
                    {
                        suggestions.Children.Remove(history);
                        if (box.IsVisible && !box.IsReadOnly)
                            box.Focus();
                    }
                    else
                    {
                        clear.IsEnabled = true;
                        field.Error = texts["Places_SaveFailed"];
                    }
                };
                history.Children.Add(clear);
            }
        }

        return dialogs.ShowWithAsyncValidation(new DialogRequest
        {
            Title = texts["ExplorerPath_Title"],
            Message = texts["ExplorerPath_Description"],
            Content = new StackPanel { Children = { field, status, bucketMode, modeHint, suggestions } },
            PrimaryText = texts["ExplorerPath_Go"],
            SecondaryText = texts["Dialog_Cancel"]
        }, async token =>
        {
            field.Error = "";
            box.IsReadOnly = true;
            bucketMode.IsEnabled = false;
            suggestions.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            try
            {
                var issue = await validate(new ExplorerPathInput(box.Text, bucketMode.IsChecked == true, fileOnly), token);
                token.ThrowIfCancellationRequested();
                field.Error = issue ?? "";
                if (issue is not null)
                    box.Focus();
                return issue is null;
            }
            finally
            {
                status.Visibility = Visibility.Collapsed;
                box.IsReadOnly = false;
                bucketMode.IsEnabled = true;
                suggestions.IsEnabled = true;
            }
        }, cancellationToken) == DialogChoice.Primary;
    }

    private static StackPanel PathSuggestions(string title, IReadOnlyList<GoToPathSuggestion> entries,
        Action<ExplorerPathInput> fill)
    {
        var heading = new TextBlock { Text = title, Margin = new Thickness(0, 12, 0, 4) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        var group = new StackPanel { Children = { heading } };
        if (entries.Count == 0)
            return group;

        var list = new ListBox { MaxHeight = 126 };
        list.SetResourceReference(FrameworkElement.StyleProperty, "DialogFolderList");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, title);
        foreach (var entry in entries)
        {
            var item = new ListBoxItem
            {
                Content = new TextBlock { Text = entry.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = new TextBlock { Text = entry.Target, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
                Tag = entry.Input
            };
            AutomationProperties.SetName(item, entry.Label);
            list.Items.Add(item);
        }

        void FillItem(ListBoxItem item)
        {
            list.SelectedIndex = -1;
            fill((ExplorerPathInput)item.Tag);
        }

        list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            if ((ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject)
                ?? list.SelectedItem ?? list.Items[0]) is ListBoxItem item)
                FillItem(item);
        };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem item)
            {
                e.Handled = true;
                FillItem(item);
            }
        };
        group.Children.Add(list);
        return group;
    }

    public static string? Ask(
        DialogService dialogs,
        string title,
        string? message,
        string label,
        string initial,
        string primary,
        string cancel,
        Func<string, string?> validate)
    {
        var box = CreateInput(initial, label);
        var field = new FormField { Label = label, Content = box };

        string? value = null;
        var choice = dialogs.ShowChoice(null, new DialogRequest
        {
            Title = title,
            Message = message,
            Content = field,
            PrimaryText = primary,
            SecondaryText = cancel
        }, () =>
        {
            var issue = validate(box.Text);
            field.Error = issue ?? "";
            if (issue is null)
            {
                value = box.Text.Trim();
                return true;
            }

            box.Focus();
            box.SelectAll();
            return false;
        });
        return choice == DialogChoice.Primary ? value : null;
    }

    private static TextBox CreateInput(string initial, string label)
    {
        var box = new TextBox { Text = initial };
        AutomationProperties.SetName(box, label);
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return box;
    }
}
