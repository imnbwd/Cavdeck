using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cavdeck.App.Controls;

namespace Cavdeck.App.Features.Explorer;

internal static class ExplorerPrompt
{
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
        var box = new TextBox { Text = initial };
        AutomationProperties.SetName(box, label);
        var field = new FormField { Label = label, Content = box };
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

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
}
