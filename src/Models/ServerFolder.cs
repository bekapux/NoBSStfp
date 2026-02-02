using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NoBSSftp.Models;

public partial class ServerFolder : ObservableObject
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString();
    private string _name = "New Folder";

    [JsonInclude]
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ObservableCollection<ServerProfile> Servers { get; set; } = [];
}
