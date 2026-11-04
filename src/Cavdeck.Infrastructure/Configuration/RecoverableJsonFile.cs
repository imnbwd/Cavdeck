using System.Text.Json;

namespace Cavdeck.Infrastructure.Configuration;

public enum ConfigurationRecoveryKind
{
    BackupRestored,
    DefaultsUsed
}

public sealed record ConfigurationRecovery(ConfigurationRecoveryKind Kind, string PreservedPath);

/// <summary>
/// A newer configuration must not be downgraded by loading or writing an older schema.
/// </summary>
public sealed class UnsupportedConfigurationVersionException : IOException
{
    public UnsupportedConfigurationVersionException(string message) : base(message) { }
}

/// <summary>
/// Atomic JSON writes with one validated .bak, and lossless recovery of damaged files.
/// Callers serialize access to each file; the validator owns document/schema rules.
/// </summary>
internal static class RecoverableJsonFile
{
    public static async Task<(T? Value, ConfigurationRecovery? Recovery)> LoadAsync<T>(
        string path, Func<string, CancellationToken, Task<T>> readValidated,
        bool allowDefaults, CancellationToken cancellationToken) where T : class
    {
        var backupPath = path + ".bak";
        var damagedPrimary = false;
        if (File.Exists(path))
        {
            try
            {
                return (await readValidated(path, cancellationToken), null);
            }
            catch (Exception exception) when (IsDamaged(exception))
            {
                damagedPrimary = true;
            }
        }

        if (File.Exists(backupPath))
        {
            try
            {
                var recovered = await readValidated(backupPath, cancellationToken);
                var preservedPath = damagedPrimary ? Archive(path) : backupPath;
                var temp = path + ".recover.tmp";
                try
                {
                    File.Copy(backupPath, temp, overwrite: true);
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }

                return (recovered, new ConfigurationRecovery(
                    ConfigurationRecoveryKind.BackupRestored, preservedPath));
            }
            catch (Exception exception) when (IsDamaged(exception))
            {
                // Never promote a malformed backup.
            }
        }

        if (damagedPrimary && allowDefaults)
        {
            // Preserve every byte of the damaged settings document before future saves.
            return (null, new ConfigurationRecovery(
                ConfigurationRecoveryKind.DefaultsUsed, Archive(path)));
        }

        if (!damagedPrimary && !File.Exists(path) && !File.Exists(backupPath))
            return (null, null); // First launch: no configuration has been written.

        if (allowDefaults)
        {
            // With no primary, even a damaged backup is preserved before the
            // application can save a new default document.
            return (null, new ConfigurationRecovery(
                ConfigurationRecoveryKind.DefaultsUsed,
                File.Exists(backupPath) ? Archive(backupPath) : path));
        }

        throw new InvalidDataException("The connection catalog has no usable document or backup.");
    }

    public static async Task WriteAsync<T>(
        string path, T value, JsonSerializerOptions options,
        Func<string, CancellationToken, Task<T>> readValidated,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        var backupTemp = backupPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(path))
            {
                // The current document is the only candidate for the next .bak.
                // Refuse to overwrite any damaged or newer-schema connection catalog.
                await readValidated(path, cancellationToken);
                if (File.Exists(backupPath))
                    await ValidateOrArchiveBackupAsync(backupPath, readValidated, cancellationToken);
                File.Copy(path, backupTemp, overwrite: true);
                File.Move(backupTemp, backupPath, overwrite: true);
            }
            else
            {
                // New installations also get a usable backup from their first save.
                // When a valid backup already exists, keep it until a normal rotation.
                if (File.Exists(backupPath))
                    await ValidateOrArchiveBackupAsync(backupPath, readValidated, cancellationToken);
                if (!File.Exists(backupPath))
                {
                    File.Copy(tempPath, backupTemp, overwrite: true);
                    File.Move(backupTemp, backupPath, overwrite: true);
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (File.Exists(backupTemp))
                File.Delete(backupTemp);
        }
    }

    private static async Task ValidateOrArchiveBackupAsync<T>(
        string backupPath, Func<string, CancellationToken, Task<T>> readValidated,
        CancellationToken cancellationToken)
    {
        try
        {
            await readValidated(backupPath, cancellationToken);
        }
        catch (Exception exception) when (IsDamaged(exception))
        {
            Archive(backupPath);
        }
    }

    // UnsupportedConfigurationVersionException derives from IOException,
    // so newer configuration schemas must never trigger corruption recovery.
    private static bool IsDamaged(Exception exception) =>
        exception is JsonException or InvalidDataException;

    private static string Archive(string path)
    {
        var preserved = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") +
            "-" + Guid.NewGuid().ToString("N")[..8];
        File.Move(path, preserved);
        return preserved;
    }
}
