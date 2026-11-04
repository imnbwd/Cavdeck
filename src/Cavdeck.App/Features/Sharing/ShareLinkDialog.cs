using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.Application.Sharing;

namespace Cavdeck.App.Features.Sharing;

internal sealed record ShareLinkChoice(bool IsPublic, string? PublicBase, TimeSpan Lifetime);

internal static class ShareLinkDialog
{
    private static readonly (string LabelKey, TimeSpan Lifetime)[] Durations =
    [
        ("Share_15Minutes", TimeSpan.FromMinutes(15)),
        ("Share_1Hour", TimeSpan.FromHours(1)),
        ("Share_24Hours", TimeSpan.FromDays(1)),
        ("Share_7Days", TimeSpan.FromDays(7))
    ];

    public static ShareLinkChoice? Choose(DialogService dialogs, LocalizationService texts, string? savedRoot)
    {
        var panel = new StackPanel();
        var kindLabel = Label(texts["Share_Kind"]);
        var kind = new ComboBox { MinHeight = 34, Margin = new Thickness(0, 4, 0, 12) };
        kind.Items.Add(texts["Share_Temporary"]);
        kind.Items.Add(texts["Share_Public"]);
        kind.SelectedIndex = 0;
        AutomationProperties.SetName(kind, texts["Share_Kind"]);
        panel.Children.Add(kindLabel);
        panel.Children.Add(kind);

        var durationLabel = Label(texts["Share_Expires"]);
        var duration = new ComboBox { MinHeight = 34, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var item in Durations)
            duration.Items.Add(texts[item.LabelKey]);
        duration.SelectedIndex = 1;
        AutomationProperties.SetName(duration, texts["Share_Expires"]);

        var rootLabel = Label(texts["Share_PublicBase"]);
        var root = new TextBox
        {
            Text = savedRoot ?? "",
            MinHeight = 34,
            Margin = new Thickness(0, 4, 0, 8)
        };
        AutomationProperties.SetName(root, texts["Share_PublicBase"]);
        var publicHint = Hint(texts["Share_PublicHint"]);
        var temporaryHint = Hint(texts["Share_TemporaryHint"]);
        var error = Hint(texts["Share_InvalidBase"]);
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerTextBrush");
        error.Visibility = Visibility.Collapsed;

        panel.Children.Add(durationLabel);
        panel.Children.Add(duration);
        panel.Children.Add(temporaryHint);
        panel.Children.Add(rootLabel);
        panel.Children.Add(root);
        panel.Children.Add(publicHint);
        panel.Children.Add(error);

        void Update()
        {
            var isPublic = kind.SelectedIndex == 1;
            durationLabel.Visibility = duration.Visibility = temporaryHint.Visibility =
                isPublic ? Visibility.Collapsed : Visibility.Visible;
            rootLabel.Visibility = root.Visibility = publicHint.Visibility =
                isPublic ? Visibility.Visible : Visibility.Collapsed;
            error.Visibility = Visibility.Collapsed;
        }
        kind.SelectionChanged += (_, _) => Update();
        Update();

        string? normalized = null;
        var result = dialogs.ShowChoice(null, new DialogRequest
        {
            Title = texts["Share_Title"],
            Message = texts["Share_ChooseHint"],
            Content = panel,
            PrimaryText = texts["Share_Create"],
            SecondaryText = texts["Dialog_Cancel"]
        }, () =>
        {
            if (kind.SelectedIndex != 1)
                return true;
            if (ShareLinks.TryPublicBase(root.Text, out var value))
            {
                normalized = value;
                return true;
            }
            error.Visibility = Visibility.Visible;
            root.Focus();
            return false;
        });
        return result != DialogChoice.Primary ? null : new ShareLinkChoice(
            kind.SelectedIndex == 1, normalized, Durations[duration.SelectedIndex].Lifetime);
    }

    public static void ShowResult(DialogService dialogs, LocalizationService texts,
        ToastService toasts, ShareLink link)
    {
        var content = new StackPanel();
        var status = new TextBlock
        {
            Text = link.IsPublic ? texts["Share_PublicStatus"] :
                string.Format(texts["Share_ExpiryStatus"], link.ExpiresAt!.Value.ToLocalTime().ToString("g")),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        content.Children.Add(status);
        var url = new TextBox
        {
            Text = link.Url,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(6),
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 54,
            MaxHeight = 120,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(url, texts["Share_Url"]);
        content.Children.Add(url);
        content.Children.Add(Hint(link.IsPublic
            ? texts["Share_PublicCaution"] : texts["Share_Sensitive"]));
        if (dialogs.ShowChoice(null, new DialogRequest
        {
            Title = texts["Share_Ready"],
            Content = content,
            PrimaryText = texts["Share_Copy"],
            SecondaryText = texts["Dialog_Close"]
        }) != DialogChoice.Primary)
            return;
        try
        {
            Clipboard.SetText(link.Url);
            toasts.Show(new ToastRequest { Title = texts["Share_Copied"], Tone = ToastTone.Success });
        }
        catch (Exception)
        {
            toasts.Show(new ToastRequest { Title = texts["Explorer_CopyFailed"], Tone = ToastTone.Error });
        }
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold
    };

    private static TextBlock Hint(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return block;
    }
}
