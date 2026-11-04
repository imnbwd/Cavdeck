# Cavdeck engineering guidelines

## WPF bindings that default to TwoWay

`ProgressBar.Value` inherits `RangeBase.Value`, and that dependency property sets `BindsTwoWayByDefault`. A binding written as `{Binding Progress}` is therefore TwoWay even when `Mode` is omitted.

Binding a TwoWay target to a get-only source can throw `XamlParseException` or `InvalidOperationException` when the template is applied, even if the solution builds successfully.

Set `Mode=OneWay` whenever the source is computed or has no setter. The same default applies to other `RangeBase` values (`Slider`, `ScrollBar`) and to `TextBox.Text`, `ComboBox.SelectedItem` / `SelectedValue`, and `CheckBox.IsChecked`. Those targets need a writable source, or an explicit `Mode=OneWay`.

A clean `dotnet build` does not catch this. Confirm the binding mode in XAML before treating a transfers view as fixed.

## Focus rings stay inside the control

Keep a visible focus cue. Do not remove it to hide a clipped outline.

Draw the `ButtonChrome` focus ring inside the control (`Margin="2"`, `CornerRadius` of `RadiusFocus`). Tabs, sidebars, and breadcrumbs may clip anything outside their layout bounds.

Show that ring only for keyboard navigation. `FocusCue` sets `FocusCue.Visible` after Tab, arrows, Enter, Space, Escape, paging keys, or Alt. A pointer click already has hover and selection, so it clears the cue. Do not bring back an `IsKeyboardFocused` trigger on buttons; a click focuses the button and would show the ring again.

Text fields are different. Their own border is the cue and should still change while the field is focused, including after a click.

## Localization and user-visible strings

Cavdeck supports multiple UI languages. Do not hardcode user-visible text in XAML or C# (including button labels, menu items, tooltips, dialog messages, errors, empty states, and accessibility text). Use the existing localization infrastructure instead.

Add or update matching keys in `src/Cavdeck.App/Resources/Strings.resx` and all shipped `Strings.<culture>.resx` files. The tests in `tests/Cavdeck.App.Tests/LocalizationResourceTests.cs` verify that registered languages include the complete resource-key set. Register a language in `AppLanguages.Supported` only after its resource file is complete; Settings choices, validation, preference normalization and system-locale matching all use this catalog. Keep language option labels in their native language. Do not add literal locale checks or `ComboBoxItem` language entries to Settings. In XAML, follow the existing `LocalizationService` indexer binding pattern (`{Binding Texts[Some_Key]}`) so strings refresh when the language changes. In C#, resolve UI copy through the same resource-based localization mechanism rather than embedding a fixed English or Chinese string. Do not use resource keys or untranslated fallbacks as intended UI text.

When changing UI copy, keep all four shipped languages consistent (English, Simplified Chinese, Japanese, German), including runtime language switching and layouts with longer German strings. Preserve .NET composite-format placeholders such as {0} exactly across translations; the localization tests enforce this. Avoid translating S3 protocol identifiers, file extensions, shortcuts, request IDs, or provider names. Error messages in normal UI should come from localized error kinds/hints; provider-supplied prose and exception messages belong only in explicit technical diagnostics, not standard toasts, titles or alerts. Preserve structured S3 error codes and request IDs for troubleshooting. Reuse the same culture-aware byte formatter across Explorer, Preview and Transfers. Internal identifiers, logs, and protocol values that are not presented to users are not UI strings.

## Light and dark mode

Every surface, label, icon stroke, and state (hover, selected, disabled, focus) must read in both themes. `ThemeService` swaps `src/Cavdeck.App/Themes/Colors.Light.xaml` and `src/Cavdeck.App/Themes/Colors.Dark.xaml`. Bind colors with `DynamicResource` to those brushes. A hardcoded color, `Transparent` on a content surface, or a system brush such as `SystemColors.ControlTextBrush` does not follow the swap.

Sibling views of the same content use the same role. The object list is `SurfaceBrush` with `PrimaryTextBrush`. The icon view must use those too. `Transparent` shows `WindowBackgroundBrush`, a different layer. System control text stays black, so dark-mode names disappear on `SurfaceBrush`.

Check both themes before treating a view as done. Contrast that works in light mode does not carry over.

## Native list and progress themes

Use `DialogFolderList` for small navigable dialog lists so the surface, text, hover, selection, disabled and focus states follow light and dark palette brushes. Keep it opt-in: Explorer tiles and Transfers provide their own ListBox item templates.

Use `ThemedProgressBar` in transfer and preview progress surfaces. It preserves native range calculation and indeterminate animation while switching its brushes with the palette. Progress bindings to get-only properties must remain `Mode=OneWay`.

## Action and navigation icons

Use `src/Cavdeck.App/Themes/Icons.xaml` for shared reusable 16-DIP action/navigation geometry. Use `IconGlyph` with the standard 1.5-DIP rounded stroke, transparent fill, and safe bounds in the 16-DIP canvas. Do not add inline Path.Data for ordinary actions or disclosure arrows. Menus use the established theme-aware icon styles.

Choose distinctive semantics: refresh has one circular arrow (not sync); upload/download show direction; grid density differs from the view-mode icon. Stateful filled glyphs are reserved for cases like favorites. Keep existing hit targets and localized ToolTip / AutomationProperties.Name for icon-only buttons.

Empty-state icons use `PageEmptyGlyph` (28 DIP, 2.25-DIP stroke). Local icon styles should inherit from `IconGlyph`. Check both themes and 100%, 125%, 150%, 200% DPI, including hover, focus, selected and disabled states. These rules do not change file-type icon geometry.

## Explorer file type icons

Keep every geometry in the Explorer `FileGlyph` style (`src/Cavdeck.App/Features/Explorer/FileGlyph.xaml`) on the same 16-DIP design canvas. Its visible outer bounds must be `Rect(3, 3, 10, 10)`, including the default file and Explorer folder glyphs. Distinguish file types with details inside that common frame; do not make one format or language icon visibly wider or taller than its peers. The Explorer folder uses `IconFolderFileGlyph` so changing its size does not alter `IconFolder` elsewhere in the app.

Use the shared `FileGlyph` style in both list and grid views. List icons are 16×16. Grid icons bind both `Width` and `Height` to `TileIconSize` (30 compact, 38 comfortable) and bind `StrokeThickness` to `TileIconStrokeThickness` so the line weight grows with the icon. Keep the icon centered and preserve its aspect ratio.

Use theme brushes through `DynamicResource` for file icon strokes. When adding or changing a file type icon, update the extension mapping and style trigger as needed, run the `FileIconGeometryTests` in `tests/Cavdeck.App.Tests/FileIconGeometryTests.cs`, and inspect the result at list and grid sizes in both light and dark themes. A successful build alone does not verify the visible size or contrast.

## Repository-wide conventions

- Never commit credentials, private keys, tokens, or other sensitive information. Use environment variables, Windows Credential Manager, or another approved secret store for real secrets; keep test credentials clearly fictional.
- Use a consistent conventional format for Git commit messages (for example, `docs: ...`, `fix: ...`, or `chore: ...`).
