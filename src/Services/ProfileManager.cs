using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NoBSSftp.Models;

namespace NoBSSftp.Services;

public interface IProfileManager
{
    Task<ServerLibrary> LoadLibraryAsync();
    Task SaveLibraryAsync(ServerLibrary library);
    Task<CredentialSecrets?> LoadCredentialsAsync(string profileId);
    Task SaveCredentialsAsync(string profileId, string password, string privateKeyPassphrase);
    Task DeleteCredentialsAsync(string profileId);
}

public class ProfileManager : IProfileManager
{
    private readonly string _filePath;
    private readonly ISecureCredentialStore _secureCredentialStore;

    public ProfileManager()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "NoBSSftp");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "servers.json");
        _secureCredentialStore = new SecureCredentialStore();
    }

    public async Task<ServerLibrary> LoadLibraryAsync()
    {
        ServerLibrary library;

        if (!File.Exists(_filePath))
        {
            library = new ServerLibrary();
        }
        else
        {
            try
            {
                var json = await File.ReadAllTextAsync(_filePath);
                var parsed = JsonSerializer.Deserialize(json, SerializationContext.Default.ServerLibrary);
                if (parsed is not null)
                {
                    library = parsed;
                }
                else
                {
                    var legacy = JsonSerializer.Deserialize(json, SerializationContext.Default.ListServerProfile) ?? [];
                    library = new ServerLibrary { RootServers = legacy };
                }
            }
            catch
            {
                library = new ServerLibrary();
            }
        }

        return library;
    }

    public async Task SaveLibraryAsync(ServerLibrary library)
    {
        var persisted = CreateSanitizedLibrary(library);
        var json = JsonSerializer.Serialize(persisted, SerializationContext.Default.ServerLibrary);
        await File.WriteAllTextAsync(_filePath, json);
    }

    public async Task<CredentialSecrets?> LoadCredentialsAsync(string profileId)
    {
        if (CredentialUnlockSession.TryGetSecrets(profileId, out var cached))
            return cached;

        var secrets = await _secureCredentialStore.LoadAsync(profileId);
        if (secrets is not null && CredentialUnlockSession.IsActive)
            CredentialUnlockSession.CacheSecrets(profileId, secrets);

        return secrets;
    }

    public async Task SaveCredentialsAsync(string profileId,
        string password,
        string privateKeyPassphrase)
    {
        var normalizedPassword = password ?? string.Empty;
        var normalizedKeyPassphrase = privateKeyPassphrase ?? string.Empty;

        await _secureCredentialStore.SaveAsync(
            profileId,
            new CredentialSecrets
            {
                Password = normalizedPassword,
                PrivateKeyPassphrase = normalizedKeyPassphrase
            });

        if (CredentialUnlockSession.IsActive)
        {
            if (normalizedPassword.Length == 0 && normalizedKeyPassphrase.Length == 0)
            {
                CredentialUnlockSession.RemoveSecrets(profileId);
            }
            else
            {
                CredentialUnlockSession.CacheSecrets(
                    profileId,
                    new CredentialSecrets
                    {
                        Password = normalizedPassword,
                        PrivateKeyPassphrase = normalizedKeyPassphrase
                    });
            }
        }
    }

    public async Task DeleteCredentialsAsync(string profileId)
    {
        await _secureCredentialStore.DeleteAsync(profileId);
        CredentialUnlockSession.RemoveSecrets(profileId);
    }

    private static ServerLibrary CreateSanitizedLibrary(ServerLibrary source)
    {
        return new ServerLibrary
        {
            RootServers = source.RootServers.Select(CloneSanitizedProfile).ToList(),
            Folders = source.Folders.Select(
                    f =>
                        new ServerFolder
                        {
                            Id = f.Id,
                            Name = f.Name,
                            Servers = new System.Collections.ObjectModel.ObservableCollection<ServerProfile>(
                                f.Servers.Select(CloneSanitizedProfile))
                        })
                .ToList()
        };
    }

    private static ServerProfile CloneSanitizedProfile(ServerProfile profile)
    {
        return new ServerProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            ConnectionTimeoutSeconds = profile.ConnectionTimeoutSeconds,
            KeepAliveIntervalSeconds = profile.KeepAliveIntervalSeconds,
            ReconnectStrategy = profile.ReconnectStrategy,
            ReconnectAttempts = profile.ReconnectAttempts,
            ReconnectDelaySeconds = profile.ReconnectDelaySeconds,
            UsePrivateKey = profile.UsePrivateKey,
            AuthPreferenceOrder = AuthPreferenceOrder.Normalize(profile.AuthPreferenceOrder, profile.UsePrivateKey),
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = string.Empty,
            Password = string.Empty
        };
    }
}
