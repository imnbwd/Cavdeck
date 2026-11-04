using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Cavdeck.App.Features.Preview;

public partial class PreviewPane : UserControl
{
    private PreviewPaneViewModel? _model;
    private Task? _pdfInitialization;
    private Uri? _allowedPdfUri;
    private int _navigationVersion;

    public PreviewPane()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
    }

    private void Hook()
    {
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as PreviewPaneViewModel;
        if (_model is not null)
            _model.PropertyChanged += OnModelChanged;
        _ = NavigatePdfAsync();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewPaneViewModel.IsEditing) && _model?.IsEditing == true)
            Dispatcher.BeginInvoke(Editor.Focus);
        if (e.PropertyName is nameof(PreviewPaneViewModel.PdfPath) or nameof(PreviewPaneViewModel.ShowPdf))
            _ = NavigatePdfAsync();
    }

    private async Task NavigatePdfAsync()
    {
        var request = ++_navigationVersion;
        var model = _model;
        var path = model?.ShowPdf == true ? model.PdfPath : null;
        if (path is null)
        {
            _allowedPdfUri = null;
            if (PdfBrowser.CoreWebView2 is not null)
                PdfBrowser.CoreWebView2.Navigate("about:blank");
            return;
        }

        try
        {
            _pdfInitialization ??= InitializePdfAsync();
            await _pdfInitialization;
            if (request != _navigationVersion || model != _model || model?.PdfPath != path || !model.ShowPdf)
                return;
            _allowedPdfUri = new Uri(Path.GetFullPath(path));
            PdfBrowser.Source = _allowedPdfUri;
        }
        catch (Exception)
        {
            // This also handles machines without the Evergreen WebView2 runtime.
            _pdfInitialization = null;
            if (request == _navigationVersion && model == _model)
                model?.PdfViewerFailed();
        }
    }

    private async Task InitializePdfAsync()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cavdeck", "webview2");
        var environment = await CoreWebView2Environment.CreateAsync(null, dataDirectory);
        await PdfBrowser.EnsureCoreWebView2Async(environment);
        var core = PdfBrowser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri == "about:blank" ||
                _allowedPdfUri is not null &&
                Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) &&
                Uri.Compare(destination, _allowedPdfUri, UriComponents.AbsoluteUri,
                    UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                return;
            args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += (_, _) => _model?.PdfViewerFailed();
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess && _allowedPdfUri is not null &&
                string.Equals(core.Source, _allowedPdfUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                _model?.PdfViewerFailed();
        };
    }
}
