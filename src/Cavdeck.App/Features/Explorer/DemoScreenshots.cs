#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cavdeck.App.Features.Connections;
using Cavdeck.App.Localization;
using Cavdeck.App.Services;
using Cavdeck.App.ViewModels;
using Cavdeck.App.Views;
using Cavdeck.Application.Connections;
using Cavdeck.Application.Settings;
using Cavdeck.Domain.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.App.Features.Explorer;

// Real shell and Explorer controls, fictional in-memory data; no S3 calls or saved connections.
// Run: dotnet run -c Debug --project src/Cavdeck.App -- --demo-snapshot
internal static class DemoScreenshots
{
    internal static readonly StorageConnection[] Connections =
    [
        new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "Amazon S3",
            StorageProviderKind.AwsS3, "", "us-east-1", "", RestrictedBucket: "project-assets"),
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"), "Cloudflare R2",
            StorageProviderKind.CloudflareR2, "https://r2.example.com", "auto", "",
            IsReadOnly: true, RestrictedBucket: "project-assets"),
        new(Guid.Parse("33333333-3333-4333-8333-333333333333"), "MinIO",
            StorageProviderKind.Minio, "https://minio.example.com", "us-east-1", ""),
        new(Guid.Parse("44444444-4444-4444-8444-444444444444"), "S3 Compatible",
            StorageProviderKind.S3Compatible, "https://storage.example.com", "us-east-1", "")
    ];

    private static readonly DateTimeOffset Modified = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);
    private static readonly ObjectEntry[] Files =
    [
        new(ObjectEntryKind.Folder, "documents", "documents/", 0, null, null),
        new(ObjectEntryKind.Folder, "images", "images/", 0, null, null),
        new(ObjectEntryKind.Folder, "reports", "reports/", 0, null, null),
        new(ObjectEntryKind.Folder, "backups", "backups/", 0, null, null),
        new(ObjectEntryKind.File, "project-overview.pdf", "project-overview.pdf", 2_486_272, Modified, null),
        new(ObjectEntryKind.File, "dashboard.png", "dashboard.png", 876_544, Modified, null),
        new(ObjectEntryKind.File, "monthly-report.xlsx", "monthly-report.xlsx", 131_072, Modified, null),
        new(ObjectEntryKind.File, "README.md", "README.md", 4_352, Modified, null),
        new(ObjectEntryKind.File, "config.json", "config.json", 2_048, Modified, null)
    ];

    internal static async Task CaptureAsync(IServiceProvider services)
    {
        services.GetRequiredService<LocalizationService>().ChangeLanguage(AppLanguages.DefaultLanguage);
        var state = services.GetRequiredService<ApplicationState>();
        state.Preferences.Language = AppLanguages.DefaultLanguage;
        state.Preferences.Theme = AppTheme.Light;
        var theme = services.GetRequiredService<ThemeService>();
        theme.Apply(AppTheme.Light);

        var catalog = services.GetRequiredService<ConnectionsViewModel>();
        foreach (var connection in Connections)
            catalog.PublishSaved(new ConnectionChange(null, connection));

        var window = services.GetRequiredService<ShellWindow>();
        window.UseDesignLayout(); // No session-layout persistence.
        window.Width = 1440;
        window.Height = 900;
        System.Windows.Application.Current.MainWindow = window;
        window.Show();

        var shell = services.GetRequiredService<ShellViewModel>();
        shell.OpenPage("explorer");
        if (shell.ActiveTab?.Workspace is not ExplorerWorkspace workspace)
            throw new InvalidOperationException("Demo Explorer workspace was not created.");
        workspace.Explorer.LoadSnapshotData(Connections[0], "project-assets", Files);

        var output = Path.Combine(Path.GetTempPath(), "cavdeck-demo-screenshots");
        await SaveAsync("explorer-light.png");
        state.Preferences.Theme = AppTheme.Dark;
        theme.Apply(AppTheme.Dark);
        await SaveAsync("explorer-dark.png");

        state.Preferences.Theme = AppTheme.Light;
        theme.Apply(AppTheme.Light);
        shell.ShowConnectionsCommand.Execute(null);
        await SaveAsync("connections-light.png");
        shell.ShowSettingsCommand.Execute(null);
        await SaveAsync("settings-light.png");
        window.Close();

        async Task SaveAsync(string filename)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            DesignCatalogWindow.SaveElement(window, Path.Combine(output, filename));
        }
    }
}

// A screenshot-only state initializer. Production browsing continues through ExplorerService.
public sealed partial class ExplorerViewModel
{
    internal void LoadSnapshotData(StorageConnection connection, string bucket, IReadOnlyList<ObjectEntry> entries)
    {
        _connection = connection;
        _bucket = bucket;
        _prefix = ObjectLayout.RootPrefix(connection);
        _continuation = null;
        _selected.Clear();
        _loaded.Clear();
        _loaded.AddRange(entries);
        PublishRows(appendOnlyNewPage: false);
        RebuildCrumbs();
        RefreshContextDetail();
        NotifyChrome();
        RaiseCommands();
    }
}
#endif
