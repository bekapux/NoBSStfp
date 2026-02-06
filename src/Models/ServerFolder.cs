using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NoBSSftp.Models;

public partial class ServerFolder : ObservableObject
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString();
    private string _name = "New Folder";
    private ObservableCollection<ServerProfile> _servers = [];

    public ServerFolder()
    {
        AttachServers(_servers);
    }

    [JsonInclude]
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    [JsonInclude]
    public ObservableCollection<ServerProfile> Servers
    {
        get => _servers;
        set
        {
            var next = value ?? [];
            if (ReferenceEquals(_servers, next))
                return;

            DetachServers(_servers);
            _servers = next;
            AttachServers(_servers);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasServers));
        }
    }

    public bool HasServers => _servers.Count > 0;

    private void OnServersCollectionChanged(object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasServers));
    }

    private void AttachServers(ObservableCollection<ServerProfile> servers)
    {
        servers.CollectionChanged += OnServersCollectionChanged;
    }

    private void DetachServers(ObservableCollection<ServerProfile> servers)
    {
        servers.CollectionChanged -= OnServersCollectionChanged;
    }
}
