using System.Text.Json.Serialization;
using Cavdeck.Application.Shell;

namespace Cavdeck.Application.Settings;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme { System, Light, Dark }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectBrowserView { List, Icons }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectGridDensity { Comfortable, Compact }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadDestinationPreference { AskEveryTime, DefaultFolder }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransferConflictPreference { AskEveryTime, Skip, Rename, AskOnConflict }

[JsonConverter(typeof(WindowCloseBehaviorConverter))]
public enum WindowCloseBehavior { Exit, MinimizeToTray }

public sealed class WindowCloseBehaviorConverter : JsonConverter<WindowCloseBehavior>
{
    public override WindowCloseBehavior Read(ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.String &&
            Enum.TryParse<WindowCloseBehavior>(reader.GetString(), true, out var value) &&
            Enum.IsDefined(value))
            return value;
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number &&
            reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(WindowCloseBehavior), number))
            return (WindowCloseBehavior)number;
        return WindowCloseBehavior.Exit;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, WindowCloseBehavior value,
        System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : nameof(WindowCloseBehavior.Exit));
}

public sealed class UserPreferences
{
    public string Language { get; set; } = "system";
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool CheckForUpdates { get; set; } = true;
    public bool DownloadUpdatesInBackground { get; set; } = true;
    public string UpdateChannel { get; set; } = "stable";
    public int MaxConcurrentTransfers { get; set; } = 3;
    // In-app completed/skipped transfer toasts; errors always remain visible.
    public bool NotifyTransferCompletion { get; set; } = true;
    public DownloadDestinationPreference DownloadDestination { get; set; } = DownloadDestinationPreference.AskEveryTime;
    public string DefaultDownloadDirectory { get; set; } = "";
    // Overwrite must remain an explicit per-operation choice.
    public TransferConflictPreference TransferConflict { get; set; } = TransferConflictPreference.AskOnConflict;
    public WindowLayout Layout { get; set; } = new();
    // Preserve previous startup behavior for existing users.
    public bool RestorePreviousTabs { get; set; } = true;
    public ObjectBrowserView ObjectBrowser { get; set; } = ObjectBrowserView.List;
    public ObjectGridDensity GridDensity { get; set; } = ObjectGridDensity.Comfortable;
    public WindowCloseBehavior WindowCloseBehavior { get; set; } = WindowCloseBehavior.Exit;
    // An opt-in OS hotkey; no key is registered until the user enables it.
    public bool GlobalHotkeyEnabled { get; set; }
    public string GlobalHotkeyGesture { get; set; } = "Ctrl+Alt+C";
    public bool ShowDetailsPane { get; set; } = true;
    public double DetailsPaneWidth { get; set; } = 440;
    // Keyed by connection ID and bucket; values are user-confirmed public bucket roots.
    public Dictionary<string, string> PublicBucketUrls { get; set; } = new();

    // Missing or disconnected folders must not silently redirect downloads.
    [JsonIgnore]
    public string? AvailableDownloadDirectory => !string.IsNullOrWhiteSpace(DefaultDownloadDirectory) &&
        Directory.Exists(DefaultDownloadDirectory) ? DefaultDownloadDirectory : null;

    public void Normalize()
    {
        Language = AppLanguages.NormalizeSelection(Language);
        // Only the public stable feed is supported. Treat persisted legacy/custom
        // channels as untrusted input rather than switching executable sources.
        UpdateChannel = "stable";
        if (!Enum.IsDefined(ObjectBrowser)) ObjectBrowser = ObjectBrowserView.List;
        if (!Enum.IsDefined(GridDensity)) GridDensity = ObjectGridDensity.Comfortable;
        if (!Enum.IsDefined(DownloadDestination)) DownloadDestination = DownloadDestinationPreference.AskEveryTime;
        // Legacy AskEveryTime values from older settings now mean ask only on an actual conflict.
        if (!Enum.IsDefined(TransferConflict) || TransferConflict == TransferConflictPreference.AskEveryTime)
            TransferConflict = TransferConflictPreference.AskOnConflict;
        DefaultDownloadDirectory ??= "";
        // A malformed or legacy value cannot crash startup. The UI validates before saving.
        GlobalHotkeyGesture = string.IsNullOrWhiteSpace(GlobalHotkeyGesture) || GlobalHotkeyGesture.Length > 40
            ? "Ctrl+Alt+C" : GlobalHotkeyGesture;
        if (!Enum.IsDefined(WindowCloseBehavior))
            WindowCloseBehavior = WindowCloseBehavior.Exit;
        if (MaxConcurrentTransfers < 1)
            MaxConcurrentTransfers = 1;
        if (MaxConcurrentTransfers > 8)
            MaxConcurrentTransfers = 8;
        Layout ??= new WindowLayout();
        Layout.Clamp();
        if (double.IsNaN(DetailsPaneWidth) || double.IsInfinity(DetailsPaneWidth))
            DetailsPaneWidth = 440;
        DetailsPaneWidth = Math.Clamp(DetailsPaneWidth, 280, 720);
        PublicBucketUrls ??= new();
    }
}

public sealed class ApplicationState
{
    public UserPreferences Preferences { get; set; } = new();
    public event EventHandler? ExplorerDisplayChanged;

    public void NotifyExplorerDisplayChanged() => ExplorerDisplayChanged?.Invoke(this, EventArgs.Empty);
}
