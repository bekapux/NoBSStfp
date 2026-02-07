using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NoBSSftp.Models;

namespace NoBSSftp.Services;

public interface ISecureCredentialStore
{
    Task<CredentialSecrets?> LoadAsync(string profileId);
    Task SaveAsync(string profileId, CredentialSecrets secrets);
    Task DeleteAsync(string profileId);
    Task WarmUpAccessAsync();
}

public sealed class SecureCredentialStore : ISecureCredentialStore
{
    private const string MacServiceName = "com.nobs.sftp.credentials";
    private const string MacVaultAccountName = "__app_vault__";
    private const int ErrSecSuccess = 0;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecItemNotFound = -25300;
    private static readonly object VaultCacheGate = new();
    private static Dictionary<string, CredentialSecrets>? _cachedVault;
    private static bool _cachedVaultLoaded;
    private bool _warnedUnsupportedPlatform;

    public async Task<CredentialSecrets?> LoadAsync(string profileId)
    {
        var id = NormalizeProfileId(profileId);
        if (id.Length == 0)
            return null;

        if (OperatingSystem.IsMacOS())
        {
            var vault = await LoadVaultAsync();
            if (vault.TryGetValue(id, out var cached))
                return CloneSecrets(cached);

            // Legacy migration: old builds stored one keychain entry per profile.
            var legacy = await LoadLegacyProfileSecretsAsync(id);
            if (legacy is null)
                return null;

            vault[id] = CloneSecrets(legacy);
            await SaveVaultAsync(vault);
            await DeleteLegacyProfileAsync(id);
            return CloneSecrets(legacy);
        }

        WarnUnsupportedPlatformOnce();
        return null;
    }

    public async Task SaveAsync(string profileId,
        CredentialSecrets secrets)
    {
        var id = NormalizeProfileId(profileId);
        if (id.Length == 0)
            return;

        if (string.IsNullOrEmpty(secrets.Password) && string.IsNullOrEmpty(secrets.PrivateKeyPassphrase))
        {
            await DeleteAsync(id);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var vault = await LoadVaultAsync();
            vault[id] = CloneSecrets(secrets);
            await SaveVaultAsync(vault);
            await DeleteLegacyProfileAsync(id);
            return;
        }

        WarnUnsupportedPlatformOnce();
    }

    public async Task DeleteAsync(string profileId)
    {
        var id = NormalizeProfileId(profileId);
        if (id.Length == 0)
            return;

        if (OperatingSystem.IsMacOS())
        {
            var vault = await LoadVaultAsync();
            if (vault.Remove(id))
                await SaveVaultAsync(vault);
            await DeleteLegacyProfileAsync(id);
            return;
        }

        WarnUnsupportedPlatformOnce();
    }

    public async Task WarmUpAccessAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            _ = await LoadVaultAsync();
            return;
        }

        WarnUnsupportedPlatformOnce();
    }

    private static string NormalizeProfileId(string profileId)
    {
        return profileId?.Trim() ?? string.Empty;
    }

    private static async Task<Dictionary<string, CredentialSecrets>> LoadVaultAsync()
    {
        Dictionary<string, CredentialSecrets>? snapshot;
        lock (VaultCacheGate)
        {
            if (_cachedVaultLoaded && _cachedVault is not null)
            {
                snapshot = CloneVault(_cachedVault);
                return snapshot;
            }
        }

        await Task.Yield();
        var payload = LoadPayloadFromMacKeychain(MacVaultAccountName);
        var parsed = ParseVaultPayload(payload);

        lock (VaultCacheGate)
        {
            _cachedVault = CloneVault(parsed);
            _cachedVaultLoaded = true;
            snapshot = CloneVault(_cachedVault);
        }

        return snapshot!;
    }

    private static async Task SaveVaultAsync(Dictionary<string, CredentialSecrets> vault)
    {
        var snapshot = CloneVault(vault);
        await Task.Yield();

        if (snapshot.Count == 0)
            DeleteFromMacKeychain(MacVaultAccountName);
        else
        {
            var payload = JsonSerializer.Serialize(
                snapshot,
                SerializationContext.Default.DictionaryStringCredentialSecrets);
            SavePayloadToMacKeychain(MacVaultAccountName, payload);
        }

        lock (VaultCacheGate)
        {
            _cachedVault = CloneVault(snapshot);
            _cachedVaultLoaded = true;
        }
    }

    private static async Task<CredentialSecrets?> LoadLegacyProfileSecretsAsync(string profileId)
    {
        await Task.Yield();
        var payload = LoadPayloadFromMacKeychain(profileId);
        if (payload is null)
            return null;

        return ParseSecretsPayload(payload, profileId);
    }

    private static async Task DeleteLegacyProfileAsync(string profileId)
    {
        await Task.Yield();
        DeleteFromMacKeychain(profileId);
    }

    private static string? LoadPayloadFromMacKeychain(string accountName)
    {
        try
        {
            var service = EncodeUtf8(MacServiceName);
            var account = EncodeUtf8(accountName);

            var status =
                SecKeychainFindGenericPassword(
                    IntPtr.Zero,
                    (uint)service.Length,
                    service,
                    (uint)account.Length,
                    account,
                    out var secretLength,
                    out var secretPtr,
                    out var itemRef);

            if (status == ErrSecItemNotFound)
                return null;

            if (status != ErrSecSuccess)
            {
                LoggingService.Warn($"Keychain read failed for account '{accountName}' (status {status}).");
                return null;
            }

            try
            {
                if (secretPtr == IntPtr.Zero || secretLength == 0)
                    return null;

                var bytes = new byte[secretLength];
                Marshal.Copy(secretPtr, bytes, 0, (int)secretLength);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                if (secretPtr != IntPtr.Zero)
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, secretPtr);
                if (itemRef != IntPtr.Zero)
                    CFRelease(itemRef);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Keychain read failed for account '{accountName}': {ex.Message}");
            return null;
        }
    }

    private static void SavePayloadToMacKeychain(string accountName,
        string payload)
    {
        try
        {
            var service = EncodeUtf8(MacServiceName);
            var account = EncodeUtf8(accountName);
            var secretBytes = Encoding.UTF8.GetBytes(payload);

            var addStatus =
                SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    (uint)service.Length,
                    service,
                    (uint)account.Length,
                    account,
                    (uint)secretBytes.Length,
                    secretBytes,
                    out var createdItemRef);

            if (addStatus == ErrSecSuccess)
            {
                if (createdItemRef != IntPtr.Zero)
                    CFRelease(createdItemRef);
                return;
            }

            if (addStatus != ErrSecDuplicateItem)
            {
                LoggingService.Warn($"Keychain write failed for account '{accountName}' (status {addStatus}).");
                return;
            }

            var findStatus =
                SecKeychainFindGenericPassword(
                    IntPtr.Zero,
                    (uint)service.Length,
                    service,
                    (uint)account.Length,
                    account,
                    out var existingLength,
                    out var existingPtr,
                    out var existingItemRef);

            if (findStatus != ErrSecSuccess)
            {
                LoggingService.Warn($"Keychain update failed for account '{accountName}' (status {findStatus}).");
                return;
            }

            try
            {
                var modifyStatus =
                    SecKeychainItemModifyAttributesAndData(
                        existingItemRef,
                        IntPtr.Zero,
                        (uint)secretBytes.Length,
                        secretBytes);

                if (modifyStatus != ErrSecSuccess)
                    LoggingService.Warn($"Keychain update failed for account '{accountName}' (status {modifyStatus}).");
            }
            finally
            {
                if (existingPtr != IntPtr.Zero)
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, existingPtr);
                if (existingItemRef != IntPtr.Zero)
                    CFRelease(existingItemRef);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Keychain write failed for account '{accountName}': {ex.Message}");
        }
    }

    private static void DeleteFromMacKeychain(string accountName)
    {
        try
        {
            var service = EncodeUtf8(MacServiceName);
            var account = EncodeUtf8(accountName);

            var findStatus =
                SecKeychainFindGenericPassword(
                    IntPtr.Zero,
                    (uint)service.Length,
                    service,
                    (uint)account.Length,
                    account,
                    out var existingLength,
                    out var existingPtr,
                    out var itemRef);

            if (findStatus == ErrSecItemNotFound)
                return;

            if (findStatus != ErrSecSuccess)
            {
                LoggingService.Warn($"Keychain delete failed for account '{accountName}' (status {findStatus}).");
                return;
            }

            try
            {
                var deleteStatus = SecKeychainItemDelete(itemRef);
                if (deleteStatus != ErrSecSuccess && deleteStatus != ErrSecItemNotFound)
                    LoggingService.Warn($"Keychain delete failed for account '{accountName}' (status {deleteStatus}).");
            }
            finally
            {
                if (existingPtr != IntPtr.Zero)
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, existingPtr);
                if (itemRef != IntPtr.Zero)
                    CFRelease(itemRef);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Keychain delete failed for account '{accountName}': {ex.Message}");
        }
    }

    private static Dictionary<string, CredentialSecrets> ParseVaultPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return [];

        try
        {
            var parsed = JsonSerializer.Deserialize(payload, SerializationContext.Default.DictionaryStringCredentialSecrets);
            if (parsed is not null)
                return CloneVault(parsed);
        }
        catch
        {
            // Legacy fallback below.
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload.Trim()));
            var parsed = JsonSerializer.Deserialize(json, SerializationContext.Default.DictionaryStringCredentialSecrets);
            if (parsed is not null)
            {
                SavePayloadToMacKeychain(MacVaultAccountName, json);
                return CloneVault(parsed);
            }
        }
        catch
        {
            // ignored
        }

        return [];
    }

    private static CredentialSecrets? ParseSecretsPayload(string payload,
        string profileId)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize(payload, SerializationContext.Default.CredentialSecrets);
            if (parsed is not null)
                return parsed;
        }
        catch
        {
            // Legacy fallback below.
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload.Trim()));
            var parsed = JsonSerializer.Deserialize(json, SerializationContext.Default.CredentialSecrets);
            if (parsed is not null)
            {
                // Opportunistic migration of old base64 payloads to native JSON payload.
                SavePayloadToMacKeychain(
                    profileId,
                    JsonSerializer.Serialize(parsed, SerializationContext.Default.CredentialSecrets));
                return parsed;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static CredentialSecrets CloneSecrets(CredentialSecrets source)
    {
        return new CredentialSecrets
        {
            Password = source.Password ?? string.Empty,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase ?? string.Empty
        };
    }

    private static Dictionary<string, CredentialSecrets> CloneVault(
        IDictionary<string, CredentialSecrets> source)
    {
        var clone = new Dictionary<string, CredentialSecrets>(StringComparer.Ordinal);
        foreach (var entry in source)
        {
            var id = NormalizeProfileId(entry.Key);
            if (id.Length == 0 || entry.Value is null)
                continue;
            clone[id] = CloneSecrets(entry.Value);
        }

        return clone;
    }

    private static byte[] EncodeUtf8(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    private void WarnUnsupportedPlatformOnce()
    {
        if (_warnedUnsupportedPlatform)
            return;

        _warnedUnsupportedPlatform = true;
        LoggingService.Warn(
            "Secure credential storage is currently implemented for macOS keychain. Credentials will not be persisted securely on this platform yet.");
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef,
        IntPtr attrList,
        uint length,
        byte[] data);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList,
        IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}
