# Controls

Shared WPF controls for the Cavdeck design system. Styles live in [../Styles/](../Styles/), while theme colors, icons, and spacing tokens live in [../Themes/](../Themes/). `ThemeService` swaps `Colors.Light.xaml` and `Colors.Dark.xaml` in the application's merged resource dictionaries.

| Piece | How to use it |
| --- | --- |
| Window | Subclass `AppWindow` and set `Style="{StaticResource AppWindowStyle}"`. Put the tab strip in `TitleBarCenter` and extra caption commands in `TitleBarContent`. |
| Button | `PrimaryButton`, `SecondaryButton`, `GhostButton`, `DangerButton`, `IconButton`. Unstyled buttons use Secondary. |
| Input | `TextBox`, `PasswordBox`, `ComboBox`, `CheckBox`, and `FormField` for label plus error text. Placeholder: `InputAssist.Placeholder`. |
| TreeView | Implicit item style for connection and prefix trees. |
| DataGrid | Implicit file-list style. Use `GridCellText` on text columns. |
| Dialog | `DialogService.Show` with `DialogRequest`. |
| Toast | `ToastService.Show` with `ToastRequest`. `ShellWindow` already hosts `ToastHost`. |
| Loading | `LoadingView`. `Mode` is `Skeleton` (list placeholder), `Inline` (spinner plus message), or `Overlay` (scrim and card). Set `Message` and, for skeleton, `Rows`. Motion follows the system animation setting. |
| Context menu | Implicit `ContextMenu` and `MenuItem` styles. Mark a destructive item with `MenuAssist.IsDanger="True"`. |
| Converters | Shared WPF binding converters live in [`Converters/`](Converters/) under `Cavdeck.App.Controls.Converters`. Feature-specific converters stay with their owning feature (for example, Explorer's `GridFileNameConverter`). |

From the repository root, open the component catalog with `dotnet run --project src/Cavdeck.App/Cavdeck.App.csproj -c Debug -- --design-preview`. Design preview and snapshot commands are available only in Debug builds.
