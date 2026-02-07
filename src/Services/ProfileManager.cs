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

    public Task<CredentialSecrets?> LoadCredentialsAsync(string profileId)
    {
        return _secureCredentialStore.LoadAsync(profileId);
    }

    public async Task SaveCredentialsAsync(string profileId,
        string password,
        string privateKeyPassphrase)
    {
        await _secureCredentialStore.SaveAsync(
            profileId,
            new CredentialSecrets
            {
                Password = password ?? string.Empty,
                PrivateKeyPassphrase = privateKeyPassphrase ?? string.Empty
            });
    }

    public Task DeleteCredentialsAsync(string profileId)
    {
        return _secureCredentialStore.DeleteAsync(profileId);
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
            UsePrivateKey = profile.UsePrivateKey,
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = string.Empty,
            Password = string.Empty
        };
    }
}
