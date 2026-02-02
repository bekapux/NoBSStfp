using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NoBSSftp.Models;

namespace NoBSSftp.Services;

public interface IProfileManager
{
    Task<ServerLibrary> LoadLibraryAsync();
    Task SaveLibraryAsync(ServerLibrary library);
}

public class ProfileManager : IProfileManager
{
    private readonly string _filePath;

    public ProfileManager()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "NoBSSftp");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "servers.json");
    }

    public async Task<ServerLibrary> LoadLibraryAsync()
    {
        if (!File.Exists(_filePath))
            return new ServerLibrary();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var library = JsonSerializer.Deserialize(json, SerializationContext.Default.ServerLibrary);
            if (library is not null)
                return library;

            var legacy = JsonSerializer.Deserialize(json, SerializationContext.Default.ListServerProfile) ?? [];
            return new ServerLibrary { RootServers = legacy };
        }
        catch
        {
            return new ServerLibrary();
        }
    }

    public async Task SaveLibraryAsync(ServerLibrary library)
    {
        var json = JsonSerializer.Serialize(library, SerializationContext.Default.ServerLibrary);
        await File.WriteAllTextAsync(_filePath, json);
    }
}
