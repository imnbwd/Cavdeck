using Cavdeck.App.Localization;

namespace Cavdeck.App.Features.Settings;

public sealed record ShortcutReference(string Action, string Keys);
public sealed record ShortcutGroup(string Title, IReadOnlyList<ShortcutReference> Items);

/// <summary>
/// Read-only reference for shortcuts currently handled in ShellWindow and ExplorerView.
/// Add entries only alongside a corresponding implemented keyboard action.
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutGroup> Create(LocalizationService texts)
    {
        ShortcutReference Entry(string key, string keys) => new(texts[key], keys);
        return
        [
            new(texts["Shortcuts_GroupTabs"],
            [
                Entry("Shortcuts_NewTab", "Ctrl + T"),
                Entry("Shortcuts_ReopenTab", "Ctrl + Shift + T"),
                Entry("Shortcuts_CloseTab", "Ctrl + W / Ctrl + F4"),
                Entry("Shortcuts_NextTab", "Ctrl + Tab"),
                Entry("Shortcuts_PreviousTab", "Ctrl + Shift + Tab"),
                Entry("Shortcuts_SelectTab", "Ctrl + 1–8"),
                Entry("Shortcuts_LastTab", "Ctrl + 9"),
                Entry("Shortcuts_Sidebar", "Ctrl + B")
            ]),
            new(texts["Shortcuts_GroupExplorer"],
            [
                Entry("Shortcuts_Search", "Ctrl + F"),
                Entry("Shortcuts_Refresh", "F5"),
                Entry("Shortcuts_ParentFolder", "Backspace"),
                Entry("Shortcuts_Open", "Enter"),
                Entry("Shortcuts_Rename", "F2"),
                Entry("Shortcuts_Delete", "Delete"),
                Entry("Shortcuts_Copy", "Ctrl + C"),
                Entry("Shortcuts_Cut", "Ctrl + X"),
                Entry("Shortcuts_Paste", "Ctrl + V")
            ]),
            new(texts["Shortcuts_GroupEditing"],
            [
                Entry("Shortcuts_SavePreview", "Ctrl + S"),
                Entry("Shortcuts_Back", "Esc")
            ])
        ];
    }
}
