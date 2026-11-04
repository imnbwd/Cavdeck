using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.Application.Settings;

namespace Cavdeck.App.Views;

public partial class DesignCatalogWindow : AppWindow
{
    private readonly ThemeService _theme;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;

    public DesignCatalogWindow(ThemeService theme, DialogService dialogs, ToastService toasts, LocalizationService texts)
    {
        InitializeComponent();
        _theme = theme;
        _dialogs = dialogs;
        _toasts = toasts;
        DataContext = new CatalogModel(texts);
        Toasts.DataContext = toasts;
        UseTexts(texts);
        ObjectGrid.SelectedIndex = 1;
        Loaded += OnLoaded;
    }

    public bool EnableSnapshot { get; set; }
    public string SnapshotDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "cavdeck-design");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!EnableSnapshot)
            return;

        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            FocusSample.Focus();
            _toasts.Show(new ToastRequest
            {
                Title = "Upload finished",
                Message = "banner.webp is now in assets.",
                Tone = ToastTone.Success,
                Duration = TimeSpan.MaxValue
            });
            await Task.Delay(240);
            SaveElement(this, Path.Combine(SnapshotDirectory, "light.png"));
            SaveDialog(Path.Combine(SnapshotDirectory, "dialog-light.png"));

            _theme.Apply(AppTheme.Dark);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(80);
            SaveElement(this, Path.Combine(SnapshotDirectory, "dark.png"));
            SaveDialog(Path.Combine(SnapshotDirectory, "dialog-dark.png"));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            // Avoid persisting exception details that could contain local paths or endpoints.
            System.Diagnostics.Debug.WriteLine($"Design snapshot failed: {ex.GetType().Name}");
            System.Windows.Application.Current.Shutdown(1);
        }
    }

    private void SaveDialog(string path)
    {
        var dialog = new AppDialog(new DialogRequest
        {
            Title = "Delete objects",
            Message = "3 objects will be removed. This copy-and-delete rename cannot be undone.",
            PrimaryText = "Delete",
            SecondaryText = "Cancel",
            CloseText = "Close",
            Tone = DialogTone.Danger
        })
        {
            Owner = this,
            ShowActivated = false
        };
        dialog.Show();
        dialog.UpdateLayout();
        SaveElement(dialog, path);
        dialog.Close();
    }

    internal static void SaveElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (width < 1 || height < 1)
            throw new InvalidOperationException($"Element has no size: {width}x{height}");

        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void LightTheme_Click(object sender, RoutedEventArgs e) => _theme.Apply(AppTheme.Light);
    private void DarkTheme_Click(object sender, RoutedEventArgs e) => _theme.Apply(AppTheme.Dark);
    private void SystemTheme_Click(object sender, RoutedEventArgs e) => _theme.Apply(AppTheme.System);

    private void Dialog_Click(object sender, RoutedEventArgs e)
    {
        var texts = ((CatalogModel)DataContext).Texts;
        _dialogs.Show(this, new DialogRequest
        {
            Title = "Test connection",
            Message = "Credentials stay on this device. A successful test opens the bucket list.",
            PrimaryText = texts["Dialog_Ok"],
            SecondaryText = texts["Dialog_Cancel"]
        });
    }

    private void DangerDialog_Click(object sender, RoutedEventArgs e)
    {
        var texts = ((CatalogModel)DataContext).Texts;
        _dialogs.Show(this, new DialogRequest
        {
            Title = "Delete objects",
            Message = "The selected prefix and its objects will be removed.",
            PrimaryText = "Delete",
            SecondaryText = texts["Dialog_Cancel"],
            Tone = DialogTone.Danger
        });
    }

    private void InfoToast_Click(object sender, RoutedEventArgs e) =>
        _toasts.Show(new ToastRequest { Title = "Link copied", Message = "Object key is on the clipboard.", Tone = ToastTone.Information });

    private void SuccessToast_Click(object sender, RoutedEventArgs e) =>
        _toasts.Show(new ToastRequest { Title = "Upload finished", Message = "banner.webp is now in assets.", Tone = ToastTone.Success });

    private void WarningToast_Click(object sender, RoutedEventArgs e) =>
        _toasts.Show(new ToastRequest { Title = "Name already exists", Message = "Choose overwrite, skip, or keep both.", Tone = ToastTone.Warning });

    private void ErrorToast_Click(object sender, RoutedEventArgs e) =>
        _toasts.Show(new ToastRequest { Title = "Upload failed", Message = "The endpoint refused the connection.", Tone = ToastTone.Error });

    private sealed class CatalogModel
    {
        public CatalogModel(LocalizationService texts)
        {
            Texts = texts;
            Files =
            [
                new SampleFile("banner.webp", "248 KB", "2026-10-12"),
                new SampleFile("cover.jpg", "1.2 MB", "2026-10-11"),
                new SampleFile("icons/", "—", "2026-09-30"),
                new SampleFile("notes.txt", "4 KB", "2026-10-02")
            ];
        }

        public LocalizationService Texts { get; }
        public IReadOnlyList<SampleFile> Files { get; }
    }

    private sealed record SampleFile(string Name, string Size, string Modified);
}
