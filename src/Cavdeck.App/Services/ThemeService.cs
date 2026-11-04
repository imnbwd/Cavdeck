using System.Windows;
using Cavdeck.Application.Settings;
using Microsoft.Win32;

namespace Cavdeck.App.Services;

public sealed class ThemeService : IDisposable
{
    private AppTheme _preference = AppTheme.System;
    private bool _disposed;
    public bool IsDark { get; private set; }
    public event EventHandler? Changed;

    public ThemeService() => SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

    public void Apply(AppTheme preference)
    {
        _preference = preference;
        var dark = preference switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark()
        };
        IsDark = dark;

        IsDark = dark;
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        dictionaries[0] = CreateColorsDictionary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Detached popups can use their own palette dictionary, synchronized with the app.
    internal ResourceDictionary CreateColorsDictionary()
    {
        var source = IsDark ? "Colors.Dark.xaml" : "Colors.Light.xaml";
        return new ResourceDictionary
        {
            Source = AppResourceUri.Relative($"Themes/{source}")
        };
    }

    // Also verify the OS setting when the tray menu opens: WM_SETTINGCHANGE is not
    // guaranteed to arrive while the window is hidden on every Windows version.
    public void RefreshSystemTheme()
    {
        if (!_disposed && _preference == AppTheme.System && IsSystemDark() != IsDark)
            Apply(AppTheme.System);
    }

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || _preference != AppTheme.System)
            return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;
        dispatcher.BeginInvoke(new Action(RefreshSystemTheme));
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
    }
}
