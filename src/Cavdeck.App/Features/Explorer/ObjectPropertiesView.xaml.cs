using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Diagnostics;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

public partial class ObjectPropertiesView : UserControl, IDisposable
{
    private readonly DialogService _dialogs;
    private readonly Func<CancellationToken, Task<ObjectMetadataSnapshot>> _read;
    private readonly Func<ObjectMetadataSnapshot, ObjectMetadataValues, CancellationToken, Task<ObjectMetadataSnapshot>> _update;
    private readonly Action<string> _copy;
    private readonly CancellationTokenSource _lifetime = new();
    private ObjectMetadataSnapshot? _snapshot;
    private bool _started, _busy, _disposed, _metadata;

    public LocalizationService Texts { get; }
    public string ObjectName { get; }
    public string ConnectionName { get; }
    public bool WriteAttempted { get; private set; }

    public ObjectPropertiesView(LocalizationService texts, DialogService dialogs, string name, string connection,
        Func<CancellationToken, Task<ObjectMetadataSnapshot>> read,
        Func<ObjectMetadataSnapshot, ObjectMetadataValues, CancellationToken, Task<ObjectMetadataSnapshot>> update,
        Action<string> copy)
    {
        Texts = texts; _dialogs = dialogs; ObjectName = name; ConnectionName = connection;
        _read = read; _update = update; _copy = copy;
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => { if (!_started) { _started = true; await ReloadAsync(); } };
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void General_Click(object sender, RoutedEventArgs e) { _metadata = false; Render(); }
    private void Metadata_Click(object sender, RoutedEventArgs e) { _metadata = true; Render(); }

    private async Task ReloadAsync()
    {
        if (_busy || _disposed) return;
        _busy = true; ReloadButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible; Actions.Visibility = Visibility.Collapsed;
        StatusText.Text = Texts["Explorer_Loading"]; Facts.Children.Clear(); _snapshot = null;
        try
        {
            var result = await _read(_lifetime.Token);
            if (!_disposed) { _snapshot = result; Render(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Queue("object-metadata", ex);
            if (!_disposed) StatusText.Text = Error(ex);
        }
        finally
        {
            _busy = false;
            if (!_disposed) { LoadingBar.Visibility = Visibility.Collapsed; ReloadButton.IsEnabled = true; }
        }
    }

    private void Render()
    {
        GeneralButton.Style = (Style)FindResource(_metadata ? "GhostButton" : "SecondaryButton");
        MetadataButton.Style = (Style)FindResource(_metadata ? "SecondaryButton" : "GhostButton");
        if (_snapshot is not { } value) return;
        Facts.Children.Clear();
        if (!_metadata)
        {
            foreach (var pair in GeneralFacts(value)) AddFact(pair.Key, pair.Value);
        }
        else
        {
            AddHeading(Texts["Metadata_Http"]);
            foreach (var pair in MetadataFields.Headers(value.Values.Http)) AddFact(pair.Key, pair.Value);
            AddHeading(Texts["Metadata_Custom"]);
            if (value.Values.Custom.Count == 0) Facts.Children.Add(Muted(Texts["Metadata_Empty"]));
            foreach (var pair in value.Values.Custom.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                AddFact(pair.Key, pair.Value);
        }
        StatusText.Text = _metadata && value.EditBlock != MetadataEditBlock.None
            ? Texts["Metadata_Block_" + value.EditBlock] : "";
        Actions.Visibility = Visibility.Visible;
        EditButton.Visibility = _metadata ? Visibility.Visible : Visibility.Collapsed;
        EditButton.IsEnabled = value.EditBlock == MetadataEditBlock.None;
    }

    private IEnumerable<KeyValuePair<string, string?>> GeneralFacts(ObjectMetadataSnapshot value)
    {
        yield return new(Texts["Explorer_Name"], ObjectName);
        yield return new(Texts["Explorer_DetailsBucket"], value.Head.Bucket);
        yield return new(Texts["Explorer_ObjectKey"], value.Head.Key);
        yield return new(Texts["Explorer_Size"], ExplorerText.FormatBytes(value.Head.SizeBytes) +
            " (" + string.Format(Texts["FolderProperties_Bytes"], value.Head.SizeBytes) + ")");
        yield return new(Texts["Explorer_Modified"], value.LastModified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        yield return new("ETag", value.Head.ETag);
        if (value.StorageClass is not null) yield return new(Texts["Metadata_StorageClass"], value.StorageClass);
        if (value.Encryption is not null) yield return new(Texts["Metadata_Encryption"], value.Encryption);
        if (value.VersionId is not null) yield return new(Texts["Metadata_Version"], value.VersionId);
        if (value.WebsiteRedirect is not null) yield return new("Website Redirect", value.WebsiteRedirect);
        yield return new("S3 URI", ObjectLayout.ObjectUri(value.Head.Bucket, value.Head.Key));
    }

    private static TextBlock Muted(string value)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return text;
    }

    private void AddHeading(string title) => Facts.Children.Add(new TextBlock
        { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });

    private void AddFact(string key, string? value)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        group.Children.Add(Muted(key));
        if (value is null) group.Children.Add(Muted(Texts["Metadata_NotSet"]));
        else
        {
            var row = new DockPanel { LastChildFill = true };
            var copy = new Button { Content = Texts["Explorer_Copy"], Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("GhostButton") };
            AutomationProperties.SetName(copy, Texts["Explorer_Copy"] + " " + key);
            copy.Click += (_, _) => _copy(value);
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center });
            group.Children.Add(row);
        }
        Facts.Children.Add(group);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { } value) return;
        IEnumerable<KeyValuePair<string, string?>> pairs = _metadata
            ? MetadataFields.Headers(value.Values.Http).Concat(value.Values.Custom.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            : GeneralFacts(value);
        _copy(string.Join(Environment.NewLine, pairs.Where(p => p.Value is not null).Select(p => p.Key + ": " + p.Value)));
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { EditBlock: MetadataEditBlock.None } original) return;
        var editor = new ObjectMetadataEditor(Texts, original);
        ObjectMetadataSnapshot? updated = null;
        _dialogs.ShowWithAsyncValidation(new DialogRequest
        {
            Title = Texts["Metadata_Edit"], Message = Texts["Metadata_SaveWarning"],
            Content = editor, PrimaryText = Texts["Metadata_Save"], SecondaryText = Texts["Dialog_Cancel"],
            ConfirmDismiss = () => !editor.IsSaving && (!editor.IsDirty || _dialogs.Show(null, new DialogRequest
            {
                Title = Texts["Metadata_Discard"], Message = Texts["Metadata_DiscardMessage"],
                PrimaryText = Texts["Metadata_Discard"], SecondaryText = Texts["Dialog_Cancel"]
            }) == true)
        }, async token =>
        {
            if (!editor.TryBuild(out var desired) || !editor.CanSubmit) return false;
            editor.SetSaving(true);
            try
            {
                WriteAttempted = true;
                updated = await _update(original, desired!, token);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Current.Queue("object-metadata", ex);
                editor.ShowError(Error(ex), ex is StorageException storage &&
                    storage.Failure.Kind is StorageFailureKind.PreconditionFailed or StorageFailureKind.Unknown or StorageFailureKind.Network);
                return false;
            }
            finally { editor.SetSaving(false); }
        }, _lifetime.Token);
        if (updated is not null)
        {
            _snapshot = updated; Render(); StatusText.Text = Texts["Metadata_Saved"];
        }
        else if (WriteAttempted)
        {
            // A timeout can leave the result unknown. Re-read instead of permitting a blind retry.
            _ = ReloadAsync();
        }
    }

    private string Error(Exception ex) => ex is StorageException storage
        ? storage.Failure.Kind == StorageFailureKind.PreconditionFailed ? Texts["Metadata_Conflict"]
            : storage.Failure.ErrorCode == "MetadataVerificationFailed" ? Texts["Metadata_VerifyFailed"]
            : ExplorerText.FailureTitle(Texts, storage.Failure)
        : Texts["Metadata_Failed"];

    public void Dispose()
    {
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
