using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

internal static class MetadataFields
{
    public static KeyValuePair<string, string?>[] Headers(ObjectHttpMetadata h) =>
    [
        new("Content-Type", h.ContentType), new("Cache-Control", h.CacheControl),
        new("Content-Disposition", h.ContentDisposition), new("Content-Encoding", h.ContentEncoding),
        new("Content-Language", h.ContentLanguage), new("Expires", h.Expires)
    ];

    public static string? SuggestedType(string key) => System.IO.Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
        ".svg" => "image/svg+xml", ".gif" => "image/gif", ".mp4" => "video/mp4",
        ".mp3" => "audio/mpeg", ".pdf" => "application/pdf", ".json" => "application/json",
        ".html" or ".htm" => "text/html", ".css" => "text/css", ".js" => "text/javascript",
        ".txt" => "text/plain", ".wasm" => "application/wasm", _ => null
    };
}

internal sealed class ObjectMetadataEditor : StackPanel, IDialogSubmitState
{
    private readonly LocalizationService _texts;
    private readonly ObjectMetadataSnapshot _original;
    private readonly TextBox[] _headers;
    private readonly StackPanel _custom = new();
    private readonly TextBlock _validation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<(TextBox Key, TextBox Value, UIElement Row)> _rows = [];
    private bool _initializing = true, _blocked;
    public bool IsSaving { get; private set; }
    public bool IsDirty => !TryBuild(out var value) || !_original.Values.SameAs(value!);
    public bool CanSubmit => !IsSaving && !_blocked && TryBuild(out var value) && !_original.Values.SameAs(value!);
    public event EventHandler? CanSubmitChanged;

    public ObjectMetadataEditor(LocalizationService texts, ObjectMetadataSnapshot original)
    {
        _texts = texts; _original = original;
        Children.Add(Heading(texts["Metadata_Http"]));
        var fields = MetadataFields.Headers(original.Values.Http);
        _headers = fields.Select(pair => Input(pair.Key, pair.Value)).ToArray();
        for (var i = 0; i < fields.Length; i++)
        {
            Children.Add(Label(fields[i].Key));
            Children.Add(_headers[i]);
            if (i == 0 && MetadataFields.SuggestedType(original.Head.Key) is { } suggestion)
            {
                var use = Button(string.Format(texts["Metadata_UseType"], suggestion), "GhostButton");
                use.Click += (_, _) => _headers[0].Text = suggestion;
                Children.Add(use);
            }
            if (i == 1)
            {
                var presets = new ComboBox { Margin = new Thickness(0, 4, 0, 4), MinHeight = 30,
                    ItemsSource = new[] { texts["Metadata_Preset"], "no-store", "no-cache",
                        "private, max-age=3600", "public, max-age=3600", "public, max-age=86400",
                        "public, max-age=31536000, immutable" }, SelectedIndex = 0 };
                AutomationProperties.SetName(presets, texts["Metadata_Preset"]);
                presets.SelectionChanged += (_, _) =>
                {
                    if (presets.SelectedIndex <= 0) return;
                    _headers[1].Text = (string)presets.SelectedItem;
                    presets.SelectedIndex = 0;
                };
                Children.Add(presets);
            }
            if (i == 5) Children.Add(Label(texts["Metadata_ExpiresHint"]));
        }
        Children.Add(Heading(texts["Metadata_Custom"]));
        Children.Add(_custom);
        foreach (var pair in original.Values.Custom.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            AddRow(pair.Key, pair.Value);
        var add = Button(texts["Metadata_Add"], "SecondaryButton");
        add.Click += (_, _) => AddRow("", "");
        Children.Add(add);
        _validation.SetResourceReference(TextBlock.ForegroundProperty, "DangerTextBrush");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerTextBrush");
        _error.Margin = _validation.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(_validation); Children.Add(_error);
        _initializing = false;
        Changed();
    }

    private TextBox Input(string name, string? value)
    {
        var box = new TextBox { Text = value ?? "", MinHeight = 30, Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(box, name);
        box.TextChanged += (_, _) => Changed();
        return box;
    }

    private static TextBlock Heading(string text) => new()
        { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return label;
    }
    private static Button Button(string text, string style)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 4) };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        return button;
    }

    private void AddRow(string key, string value)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var k = Input(_texts["Metadata_Key"], key);
        var v = Input(_texts["Metadata_Value"], value);
        var remove = Button(_texts["Metadata_Remove"], "GhostButton");
        row.Children.Add(Label(_texts["Metadata_Key"])); row.Children.Add(k);
        row.Children.Add(Label(_texts["Metadata_Value"])); row.Children.Add(v); row.Children.Add(remove);
        _rows.Add((k, v, row)); _custom.Children.Add(row);
        remove.Click += (_, _) =>
        {
            _rows.RemoveAll(x => ReferenceEquals(x.Row, row)); _custom.Children.Remove(row); Changed();
        };
        Changed();
    }

    private MetadataValidation Build(out ObjectMetadataValues? result)
    {
        result = null;
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
            if (!custom.TryAdd(row.Key.Text, row.Value.Text)) return MetadataValidation.DuplicateKey;
        string? Field(int i) => _headers[i].Text.Length == 0 ? null : _headers[i].Text;
        result = new(new ObjectHttpMetadata(Field(0), Field(1), Field(2), Field(3), Field(4), Field(5)), custom);
        return ObjectMetadataRules.Validate(result);
    }

    public bool TryBuild(out ObjectMetadataValues? result) => Build(out result) == MetadataValidation.None;
    private void Changed()
    {
        if (_initializing) return;
        var issue = Build(out _);
        _validation.Text = issue == MetadataValidation.None ? "" : _texts["Metadata_Validation_" + issue];
        CanSubmitChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSaving(bool saving)
    {
        IsSaving = saving;
        // Disable editor fields but leave status visible; the dialog keeps Save disabled.
        foreach (UIElement child in Children)
            if (child != _error && child != _validation) child.IsEnabled = !saving;
        if (saving) _error.Text = _texts["Metadata_Saving"];
        CanSubmitChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowError(string error, bool block)
    {
        _error.Text = error; _blocked = block;
        CanSubmitChanged?.Invoke(this, EventArgs.Empty);
    }
}
