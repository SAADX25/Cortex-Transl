using CortexTransl.App.Models;
using CortexTransl.App.Utils;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CortexTransl.App.Services.Settings;

public sealed class AppSettingsService
{
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AppSettingsService(string dataDirectory)
    {
        _settingsFilePath = Path.Combine(dataDirectory, "appsettings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = new FileStream(
                _settingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: cancellationToken);
            return settings ?? new AppSettings();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-load", ex);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken);
        var temporaryPath = _settingsFilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _settingsFilePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-save", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("settings-temp-cleanup", ex);
            }

            _saveLock.Release();
        }
    }

    public string DecryptApiKey(string encryptedKey)
    {
        if (string.IsNullOrWhiteSpace(encryptedKey))
        {
            return string.Empty;
        }

        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedKey);
            var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    public string EncryptApiKey(string plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey))
        {
            return string.Empty;
        }

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainTextKey);
            var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
