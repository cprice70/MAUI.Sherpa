using System.Text;
using System.Text.Json;
using MauiSherpa.Core.Interfaces;

namespace MauiSherpa.Core.Services;

public class PublishProfileService : IPublishProfileService
{
    const string CloudKeyPrefix = "sherpa-publish-profiles/";

    readonly ICloudSecretsService _cloudService;
    readonly ICertificateSyncService _certSync;
    readonly IKeystoreService _keystoreService;
    readonly ISecureStorageService _secureStorage;
    readonly IManagedSecretsService _managedSecrets;
    readonly IAppleConnectService _appleConnect;
    readonly IAppleIdentityService _appleIdentity;
    readonly ILoggingService _logger;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    List<PublishProfile>? _cache;

    public event Action? OnProfilesChanged;

    public PublishProfileService(
        ICloudSecretsService cloudService,
        ICertificateSyncService certSync,
        IKeystoreService keystoreService,
        ISecureStorageService secureStorage,
        IManagedSecretsService managedSecrets,
        IAppleConnectService appleConnect,
        IAppleIdentityService appleIdentity,
        ILoggingService logger)
    {
        _cloudService = cloudService;
        _certSync = certSync;
        _keystoreService = keystoreService;
        _secureStorage = secureStorage;
        _managedSecrets = managedSecrets;
        _appleConnect = appleConnect;
        _appleIdentity = appleIdentity;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PublishProfile>> GetProfilesAsync()
    {
        if (_cache is not null)
            return _cache;

        if (_cloudService.ActiveProvider is null)
            return Array.Empty<PublishProfile>();

        var keys = await _cloudService.ListSecretsAsync(CloudKeyPrefix);
        var profiles = new List<PublishProfile>();

        foreach (var key in keys)
        {
            try
            {
                var bytes = await _cloudService.GetSecretAsync(key);
                if (bytes is null) continue;

                var json = Encoding.UTF8.GetString(bytes);
                var profile = JsonSerializer.Deserialize<PublishProfile>(json, JsonOptions);
                if (profile is not null)
                    profiles.Add(profile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to load publish profile '{key}': {ex.Message}");
            }
        }

        _cache = profiles.OrderBy(p => p.Name).ToList();
        return _cache;
    }

    public async Task<PublishProfile?> GetProfileAsync(string id)
    {
        var profiles = await GetProfilesAsync();
        return profiles.FirstOrDefault(p => p.Id == id);
    }

    public async Task SaveProfileAsync(PublishProfile profile)
    {
        if (_cloudService.ActiveProvider is null)
            throw new InvalidOperationException("No cloud provider configured");

        var key = CloudKeyPrefix + profile.Id;
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _cloudService.StoreSecretAsync(key, bytes);

        // Update cache directly instead of invalidating — cloud list may lag
        if (_cache is null)
            _cache = new List<PublishProfile>();

        var existing = _cache.FindIndex(p => p.Id == profile.Id);
        if (existing >= 0)
            _cache[existing] = profile;
        else
            _cache.Add(profile);
        _cache = _cache.OrderBy(p => p.Name).ToList();

        OnProfilesChanged?.Invoke();
    }

    public async Task DeleteProfileAsync(string id)
    {
        if (_cloudService.ActiveProvider is null)
            throw new InvalidOperationException("No cloud provider configured");

        var key = CloudKeyPrefix + id;
        await _cloudService.DeleteSecretAsync(key);

        // Update cache directly instead of invalidating
        _cache?.RemoveAll(p => p.Id == id);
        OnProfilesChanged?.Invoke();
    }

    public async Task<Dictionary<string, string>> ResolveSecretsAsync(
        PublishProfile profile,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var secrets = new Dictionary<string, string>();

        // Resolve Apple configs
        foreach (var apple in profile.AppleConfigs)
        {
            ct.ThrowIfCancellationRequested();

            // Certificate P12
            if (!string.IsNullOrEmpty(apple.CertificateSerialNumber))
            {
                progress?.Report($"Fetching certificate for {apple.Label}...");
                try
                {
                    var p12Key = _certSync.GetCertificateSecretKey(apple.CertificateSerialNumber);
                    var p12Bytes = await _cloudService.GetSecretAsync(p12Key, ct);
                    if (p12Bytes is not null)
                    {
                        var defaultKey = $"APPLE_{SanitizeLabel(apple.Label)}_CERTIFICATE_P12";
                        AddMappedSecrets(secrets, apple.KeyMappings, defaultKey, Convert.ToBase64String(p12Bytes));
                    }

                    var pwdKey = _certSync.GetCertificatePasswordKey(apple.CertificateSerialNumber);
                    var pwdBytes = await _cloudService.GetSecretAsync(pwdKey, ct);
                    if (pwdBytes is not null)
                    {
                        var defaultKey = $"APPLE_{SanitizeLabel(apple.Label)}_CERTIFICATE_PASSWORD";
                        AddMappedSecrets(secrets, apple.KeyMappings, defaultKey, Encoding.UTF8.GetString(pwdBytes));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to resolve certificate for {apple.Label}: {ex.Message}");
                }
            }

            // Installer Certificate
            if (!string.IsNullOrEmpty(apple.InstallerCertSerialNumber))
            {
                progress?.Report($"Fetching installer certificate for {apple.Label}...");
                try
                {
                    var p12Key = _certSync.GetCertificateSecretKey(apple.InstallerCertSerialNumber);
                    var p12Bytes = await _cloudService.GetSecretAsync(p12Key, ct);
                    if (p12Bytes is not null)
                    {
                        var defaultKey = $"APPLE_{SanitizeLabel(apple.Label)}_INSTALLER_CERTIFICATE_P12";
                        AddMappedSecrets(secrets, apple.KeyMappings, defaultKey, Convert.ToBase64String(p12Bytes));
                    }

                    var pwdKey = _certSync.GetCertificatePasswordKey(apple.InstallerCertSerialNumber);
                    var pwdBytes = await _cloudService.GetSecretAsync(pwdKey, ct);
                    if (pwdBytes is not null)
                    {
                        var defaultKey = $"APPLE_{SanitizeLabel(apple.Label)}_INSTALLER_CERTIFICATE_PASSWORD";
                        AddMappedSecrets(secrets, apple.KeyMappings, defaultKey, Encoding.UTF8.GetString(pwdBytes));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to resolve installer cert for {apple.Label}: {ex.Message}");
                }
            }

            // Provisioning Profile
            if (!string.IsNullOrEmpty(apple.ProfileId))
            {
                progress?.Report($"Fetching provisioning profile for {apple.Label}...");
                try
                {
                    var profileBytes = await _appleConnect.DownloadProfileAsync(apple.ProfileId);
                    var defaultKey = $"APPLE_{SanitizeLabel(apple.Label)}_PROVISIONING_PROFILE";
                    AddMappedSecrets(secrets, apple.KeyMappings, defaultKey, Convert.ToBase64String(profileBytes));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to resolve profile for {apple.Label}: {ex.Message}");
                }
            }

            // Notarization credentials
            if (apple.IncludeNotarization)
            {
                progress?.Report("Fetching notarization credentials...");
                try
                {
                    var identities = await _appleIdentity.GetIdentitiesAsync();
                    var identity = identities.FirstOrDefault();
                    if (identity is not null)
                    {
                        AddMappedSecrets(secrets, apple.KeyMappings, "APPLE_NOTARIZATION_KEY_ID", identity.KeyId);
                        AddMappedSecrets(secrets, apple.KeyMappings, "APPLE_NOTARIZATION_ISSUER_ID", identity.IssuerId);
                        if (!string.IsNullOrEmpty(identity.P8KeyContent))
                            AddMappedSecrets(secrets, apple.KeyMappings, "APPLE_NOTARIZATION_KEY_P8", identity.P8KeyContent);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to resolve notarization creds: {ex.Message}");
                }
            }
        }

        // Resolve Android configs
        foreach (var android in profile.AndroidConfigs)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrEmpty(android.KeystoreId))
            {
                progress?.Report($"Fetching keystore for {android.Label}...");
                try
                {
                    var keystores = await _keystoreService.ListKeystoresAsync();
                    var keystore = keystores.FirstOrDefault(k => k.Id == android.KeystoreId);
                    if (keystore is not null)
                    {
                        // Read keystore file bytes
                        if (!string.IsNullOrEmpty(keystore.FilePath) && File.Exists(keystore.FilePath))
                        {
                            var keystoreBytes = await File.ReadAllBytesAsync(keystore.FilePath, ct);
                            var defaultKey = $"ANDROID_{SanitizeLabel(android.Label)}_KEYSTORE";
                            AddMappedSecrets(secrets, android.KeyMappings, defaultKey, Convert.ToBase64String(keystoreBytes));
                        }

                        // Key alias
                        var defaultAliasKey = $"ANDROID_{SanitizeLabel(android.Label)}_KEY_ALIAS";
                        AddMappedSecrets(secrets, android.KeyMappings, defaultAliasKey, keystore.Alias);

                        // Keystore password from secure storage
                        var pwd = await _secureStorage.GetAsync($"android_keystore_pwd_{keystore.Id}");
                        if (!string.IsNullOrEmpty(pwd))
                        {
                            var defaultPwdKey = $"ANDROID_{SanitizeLabel(android.Label)}_KEYSTORE_PASSWORD";
                            AddMappedSecrets(secrets, android.KeyMappings, defaultPwdKey, pwd);

                            // Key password (typically same as keystore password for PKCS12)
                            var defaultKeyPwdKey = $"ANDROID_{SanitizeLabel(android.Label)}_KEY_PASSWORD";
                            AddMappedSecrets(secrets, android.KeyMappings, defaultKeyPwdKey, pwd);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to resolve keystore for {android.Label}: {ex.Message}");
                }
            }
        }

        // Resolve managed secrets
        foreach (var mapping in profile.SecretMappings)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report($"Fetching secret {mapping.SourceKey}...");
            try
            {
                var valueBytes = await _managedSecrets.GetValueAsync(mapping.SourceKey, ct);
                if (valueBytes is not null)
                {
                    var value = Encoding.UTF8.GetString(valueBytes);
                    foreach (var destKey in mapping.DestinationKeys)
                    {
                        secrets[destKey] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to resolve secret '{mapping.SourceKey}': {ex.Message}");
            }
        }

        progress?.Report($"Resolved {secrets.Count} secrets");
        return secrets;
    }

    static void AddMappedSecrets(
        Dictionary<string, string> secrets,
        Dictionary<string, List<string>> keyMappings,
        string defaultKey,
        string value)
    {
        if (keyMappings.TryGetValue(defaultKey, out var destinations) && destinations.Count > 0)
        {
            foreach (var dest in destinations)
                secrets[dest] = value;
        }
        else
        {
            secrets[defaultKey] = value;
        }
    }

    static string SanitizeLabel(string label)
        => label.ToUpperInvariant().Replace(' ', '_').Replace('-', '_');
}
