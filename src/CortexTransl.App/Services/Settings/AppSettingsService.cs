using CortexTransl.App.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CortexTransl.App.Services.Settings;

public sealed class AppSettingsService
{
    private readonly string _settingsFilePath;

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
            using var stream = new FileStream(_settingsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: cancellationToken);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new FileStream(_settingsFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        }
        catch
        {
            // Fail silently or log
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
