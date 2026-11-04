using Cavdeck.Application.Settings;

namespace Cavdeck.Application.Abstractions;

public interface ISettingsStore
{
    Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default);
}
