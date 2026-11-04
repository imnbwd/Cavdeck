using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Cavdeck.Application.Settings;

namespace Cavdeck.App.Localization;

// Indexer binding: {Binding Texts[Some_Key]}; refreshes on language switch.
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly ResourceManager Strings = new(
        "Cavdeck.App.Resources.Strings", typeof(LocalizationService).Assembly);
    private CultureInfo _culture = CultureInfo.GetCultureInfo(
        AppLanguages.Resolve(AppLanguages.System, CultureInfo.InstalledUICulture));

    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Strings.GetString(key, _culture) ?? $"[{key}]";
    public string CurrentLanguage => _culture.Name;

    public void ChangeLanguage(string language)
    {
        var systemCulture = CultureInfo.InstalledUICulture;
        var selection = AppLanguages.NormalizeSelection(language);
        _culture = CultureInfo.GetCultureInfo(AppLanguages.Resolve(selection, systemCulture));

        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        // System selection still preserves the OS culture for dates/numbers.
        CultureInfo.DefaultThreadCurrentCulture = selection == AppLanguages.System
            ? systemCulture : _culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
    }
}
