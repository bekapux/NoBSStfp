using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using NoBSSftp.Models;
using NoBSSftp.Services;

namespace NoBSSftp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IProfileManager _profileManager;
    private readonly IDialogService _dialogService;

    public ObservableCollection<object> TabItems { get; } = [];
    public ObservableCollection<ServerProfile> RootServers { get; } = [];
    public ObservableCollection<ServerFolder> Folders { get; } = [];

    [ObservableProperty]
    private object? _selectedTabItem;

    private readonly AddTabViewModel _addTabItem = new();

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    public MainWindowViewModel()
    {
        _profileManager = new ProfileManager();
        _dialogService = new DialogService();

        TabItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
        TabItems.Add(_addTabItem);
        AddTab();

        // Load servers
        Task.Run(async () =>
        {
            var library = await _profileManager.LoadLibraryAsync();
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                foreach (var server in library.RootServers) RootServers.Add(server);
                foreach (var folder in library.Folders)
                {
                    Folders.Add(new ServerFolder
                    {
                        Id = folder.Id,
                        Name = folder.Name,
                        Servers = new ObservableCollection<ServerProfile>(folder.Servers)
                    });
                }

                RefreshSessionSuggestions();
            });
        });
    }

    public bool HasTabs => TabItems.OfType<SessionViewModel>().Any();

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
    }

    [RelayCommand]
    private async Task AddServer()
    {
        var newProfile = await _dialogService.ShowServerFormAsync();
        if (newProfile is not null)
        {
            RootServers.Add(newProfile);
            await SaveLibraryAsync();
            RefreshSessionSuggestions();
        }
    }

    [RelayCommand]
    private async Task EditServer(ServerProfile? profile)
    {
        if (profile is null) return;

        var updatedProfile = await _dialogService.ShowServerFormAsync(profile);
        if (updatedProfile is not null)
        {
            var list = FindServerList(profile);
            if (list is null) return;
            var index = list.IndexOf(profile);
            if (index >= 0)
                list[index] = updatedProfile;
            await SaveLibraryAsync();
            RefreshSessionSuggestions();
        }
    }

    [RelayCommand]
    private async Task DeleteServer(ServerProfile profile)
    {
        var list = FindServerList(profile);
        if (list is null) return;
        var confirm = await _dialogService.ConfirmAsync("Delete Server", $"Delete '{profile.Name}'?");
        if (confirm)
        {
            list.Remove(profile);
            await SaveLibraryAsync();
            RefreshSessionSuggestions();
        }
    }

    [RelayCommand]
    private async Task ConnectToServer(ServerProfile? profile)
    {
        if (profile is null) return;

        var credentials = await _dialogService.ShowConnectDialogAsync("Connect", profile.Host, profile);
        if (credentials is null) return;

        var sessionProfile =
            new ServerProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                Host = profile.Host,
                Port = profile.Port,
                Username = credentials.Username,
                Password = credentials.Password,
                UsePrivateKey = credentials.UsePrivateKey,
                PrivateKeyPath = credentials.PrivateKeyPath,
                PrivateKeyPassphrase = credentials.PrivateKeyPassphrase
            };

        var newTab = CreateSessionTab(sessionProfile);

        InsertBeforeAddTab(newTab);

        SelectedTabItem = newTab;

        await newTab.Connect();
    }

    [RelayCommand]
    private void AddTab()
    {
        var newTab = CreateSessionTab();

        InsertBeforeAddTab(newTab);

        SelectedTabItem = newTab;
    }

    private void OnTabCloseRequested(SessionViewModel tab)
    {
        CloseTab(tab);
    }

    [RelayCommand]
    private void CloseTab(SessionViewModel tab)
    {
        if (!TabItems.Contains(tab)) return;
        var closingIndex = TabItems.IndexOf(tab);
        var wasSelected = ReferenceEquals(SelectedTabItem, tab);

        tab.CloseRequested -= OnTabCloseRequested;
        tab.DisconnectCommand.Execute(null);
        TabItems.Remove(tab);

        if (!TabItems.OfType<SessionViewModel>().Any())
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            return;
        }

        if (!wasSelected && SelectedTabItem is SessionViewModel)
            return;

        SessionViewModel? replacement = null;

        for (var i = closingIndex; i < TabItems.Count; i++)
        {
            if (TabItems[i] is SessionViewModel nextSession)
            {
                replacement = nextSession;
                break;
            }
        }

        if (replacement is null)
        {
            for (var i = closingIndex - 1; i >= 0; i--)
            {
                if (TabItems[i] is SessionViewModel prevSession)
                {
                    replacement = prevSession;
                    break;
                }
            }
        }

        SelectedTabItem = replacement ?? TabItems.OfType<SessionViewModel>().LastOrDefault();
    }

    public Task SaveServerOrderAsync()
    {
        return SaveLibraryAsync();
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        var name = await _dialogService.PromptAsync("New Folder", "Enter folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        Folders.Add(new ServerFolder { Name = name });
        await SaveLibraryAsync();
    }

    [RelayCommand]
    private async Task RenameFolder(ServerFolder? folder)
    {
        if (folder is null) return;
        var name = await _dialogService.PromptAsync("Rename Folder", "Enter new name:", folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        folder.Name = name;
        await SaveLibraryAsync();
    }

    [RelayCommand]
    private async Task DeleteFolder(ServerFolder? folder)
    {
        if (folder is null) return;
        var confirm = await _dialogService.ConfirmAsync("Delete Folder",
            $"Delete '{folder.Name}' and move servers to root?");
        if (!confirm) return;

        foreach (var server in folder.Servers)
            RootServers.Add(server);

        Folders.Remove(folder);
        await SaveLibraryAsync();
        RefreshSessionSuggestions();
    }

    public Task SaveLibraryAsync()
    {
        var library = new ServerLibrary
        {
            RootServers = RootServers.ToList(),
            Folders = Folders.Select(f => new ServerFolder
            {
                Id = f.Id,
                Name = f.Name,
                Servers = new ObservableCollection<ServerProfile>(f.Servers)
            }).ToList()
        };
        return _profileManager.SaveLibraryAsync(library);
    }

    private ObservableCollection<ServerProfile>? FindServerList(ServerProfile profile)
    {
        if (RootServers.Contains(profile))
            return RootServers;

        foreach (var folder in Folders)
        {
            if (folder.Servers.Contains(profile))
                return folder.Servers;
        }

        return null;
    }

    private SessionViewModel CreateSessionTab(ServerProfile? profile = null)
    {
        var tab = profile is null
            ? new SessionViewModel()
            : new SessionViewModel(profile);

        tab.CloseRequested += OnTabCloseRequested;
        tab.SetConnectionSuggestions(BuildConnectionSuggestions());
        return tab;
    }

    private IReadOnlyList<ServerProfile> BuildConnectionSuggestions()
    {
        var suggestions = new List<ServerProfile>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        static void AddUnique(List<ServerProfile> target,
            HashSet<string> seen,
            ServerProfile server)
        {
            if (string.IsNullOrWhiteSpace(server.Id))
            {
                target.Add(server);
                return;
            }

            if (seen.Add(server.Id))
                target.Add(server);
        }

        foreach (var server in RootServers)
            AddUnique(suggestions, seenIds, server);

        foreach (var folder in Folders)
        {
            foreach (var server in folder.Servers)
                AddUnique(suggestions, seenIds, server);
        }

        return suggestions;
    }

    private void RefreshSessionSuggestions()
    {
        var suggestions = BuildConnectionSuggestions();
        foreach (var tab in TabItems.OfType<SessionViewModel>())
            tab.SetConnectionSuggestions(suggestions);
    }

    partial void OnSelectedTabItemChanged(object? value)
    {
        if (value is AddTabViewModel)
        {
            AddTab();
        }
    }

    private void InsertBeforeAddTab(SessionViewModel tab)
    {
        var index = TabItems.IndexOf(_addTabItem);
        if (index >= 0)
            TabItems.Insert(index, tab);
        else
            TabItems.Add(tab);
    }
}
