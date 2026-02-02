using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoBSSftp.Models;
using NoBSSftp.Services;
using System.Threading;
using System.IO;

namespace NoBSSftp.ViewModels;

public partial class SessionViewModel : ViewModelBase
{
    private readonly ISftpService _sftpService;
    private readonly IDialogService _dialogService;
    private CancellationTokenSource? _transferCts;

    [ObservableProperty]
    private string _header = "New Server";

    [ObservableProperty]
    private ServerProfile _profile;

    [ObservableProperty]
    private string _currentPath = "/";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private FileEntry? _selectedFile;

    [ObservableProperty]
    private System.Collections.IList? _selectedFiles;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private double _transferProgress;

    [ObservableProperty]
    private string _transferTitle = "";

    [ObservableProperty]
    private bool _showHiddenFiles;

    [ObservableProperty]
    private bool _isTerminalVisible = true;

    [ObservableProperty]
    private TerminalViewModel _terminal;

    private class ClipboardItem
    {
        public string Path { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public DateTime LastWriteTime { get; init; }
    }

    private List<ClipboardItem> _clipboardItems = new();
    private bool _clipboardIsCut;

    partial void OnShowHiddenFilesChanged(bool value)
    {
        ApplyFileFilter();
    }

    public event Action<SessionViewModel>? CloseRequested;

    public ObservableCollection<FileEntry> Files { get; } = [];
    private List<FileEntry> _allFiles = [];

    public SessionViewModel() : this(new ServerProfile())
    {
    }

    public SessionViewModel(ServerProfile profile)
    {
        _sftpService = new SftpService();
        _dialogService = new DialogService();
        _profile = profile;
        _terminal = new TerminalViewModel(_sftpService);
        Header = GetDisplayName();
    }

    [RelayCommand]
    private void ToggleTerminal()
    {
        IsTerminalVisible = !IsTerminalVisible;
    }

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this);
    }

    [RelayCommand]
    public async Task Connect()
    {
        try
        {
            StatusMessage = "Connecting...";
            await _sftpService.ConnectAsync(Profile);
            IsConnected = true;
            Header = GetDisplayName(preferHostWhenDefaultName: true);
            StatusMessage = "Connected";
            await RefreshFileList();
            try
            {
                await Terminal.ConnectAsync(Profile);
            }
            catch (Exception ex)
            {
                LoggingService.Error("Terminal connect failed", ex);
                StatusMessage = $"Terminal Error: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Connect failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private string GetDisplayName(bool preferHostWhenDefaultName = false)
    {
        var name = Profile.Name;
        if (preferHostWhenDefaultName && string.Equals(name, "New Server", StringComparison.OrdinalIgnoreCase))
            name = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
            name = Profile.Host;

        return string.IsNullOrWhiteSpace(name) ? "New Server" : name;
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        await Terminal.DisconnectAsync();
        await _sftpService.DisconnectAsync();
        IsConnected = false;
        Files.Clear();
        StatusMessage = "Disconnected";
    }

    [RelayCommand]
    public async Task Navigate(string path)
    {
        if (!IsConnected) return;

        string newPath;
        if (path == "..")
        {
            var parent = System.IO.Path.GetDirectoryName(CurrentPath);
            newPath = string.IsNullOrEmpty(parent) ? "/" : parent;
            if (CurrentPath == "/" && path == "..") newPath = "/";
        }
        else if (path.StartsWith('/'))
        {
            newPath = path;
        }
        else
        {
            newPath = CurrentPath.EndsWith('/') ? CurrentPath + path : CurrentPath + "/" + path;
        }

        try
        {
            StatusMessage = "Listing directory...";
            var files = await _sftpService.ListDirectoryAsync(newPath);
            CurrentPath = newPath;
            _allFiles = files;
            ApplyFileFilter();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Navigate failed", ex);
            StatusMessage = $"Navigation Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshFileList()
    {
        try
        {
            StatusMessage = "Listing directory...";
            _allFiles = await _sftpService.ListDirectoryAsync(CurrentPath);
            ApplyFileFilter();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Refresh file list failed", ex);
            StatusMessage = $"List Error: {ex.Message}";
        }
    }

    private void ApplyFileFilter()
    {
        Files.Clear();

        if (CurrentPath != "/")
        {
            Files.Add(new FileEntry { Name = "..", IsDirectory = true });
        }

        foreach (var file in _allFiles.Where(file => ShowHiddenFiles || !file.IsHidden))
        {
            Files.Add(file);
        }

        StatusMessage = $"Showing {Files.Count} items";
    }

    public async Task OpenItem(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            await Navigate(entry.Name);
        }
        else
        {
            StatusMessage = $"Selected file: {entry.Name}";
        }
    }

    [RelayCommand]
    private async Task CreateFolder()
    {
        if (!IsConnected) return;
        var name = await _dialogService.PromptAsync("New Folder", "Enter folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var path = CurrentPath.EndsWith('/') ? CurrentPath + name : $"{CurrentPath}/{name}";
            if (await _sftpService.PathExistsAsync(path))
            {
                StatusMessage = $"Folder already exists: {name}";
                return;
            }

            await _sftpService.CreateDirectoryAsync(path);
            await RefreshFileList();
            StatusMessage = $"Created folder: {name}";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Create folder failed", ex);
            StatusMessage = $"Error creating folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateFile()
    {
        if (!IsConnected) return;
        var name = await _dialogService.PromptAsync("New File", "Enter file name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var path = CurrentPath.EndsWith('/') ? CurrentPath + name : $"{CurrentPath}/{name}";
            if (await _sftpService.PathExistsAsync(path))
            {
                StatusMessage = $"File already exists: {name}";
                return;
            }

            await _sftpService.CreateFileAsync(path);
            await RefreshFileList();
            StatusMessage = $"Created file: {name}";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Create file failed", ex);
            StatusMessage = $"Error creating file: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Rename(FileEntry? file)
    {
        var target = file ?? SelectedFile;
        if (!IsConnected || target is null || target.Name == "..") return;

        var newName = await _dialogService.PromptAsync("Rename", "Enter new name:", target.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == target.Name) return;

        try
        {
            var oldPath = CurrentPath.EndsWith('/') ? CurrentPath + target.Name : $"{CurrentPath}/{target.Name}";
            var newPath = CurrentPath.EndsWith('/') ? CurrentPath + newName : $"{CurrentPath}/{newName}";

            await _sftpService.RenameFileAsync(oldPath, newPath);
            await RefreshFileList();
            StatusMessage = $"Renamed to: {newName}";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Rename failed", ex);
            StatusMessage = $"Rename Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Delete(FileEntry? file)
    {
        if (!IsConnected) return;
        var targets = GetSelectedTargets(file);
        if (targets.Count is 0) return;

        var confirm =
            targets.Count is 1
                ? await _dialogService.ConfirmAsync("Delete", $"Are you sure you want to delete '{targets[0].Name}'?")
                : await _dialogService.ConfirmAsync("Delete",
                    $"Are you sure you want to delete {targets.Count} items?");
        if (!confirm) return;

        try
        {
            foreach (var target in targets)
            {
                var path = CurrentPath.EndsWith('/') ? CurrentPath + target.Name : $"{CurrentPath}/{target.Name}";
                if (target.IsDirectory)
                {
                    await _sftpService.DeleteDirectoryAsync(path);
                }
                else
                {
                    await _sftpService.DeleteFileAsync(path);
                }
            }

            await RefreshFileList();
            StatusMessage = targets.Count == 1 ? $"Deleted: {targets[0].Name}" : $"Deleted {targets.Count} items";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Delete failed", ex);
            StatusMessage = $"Delete Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Copy(FileEntry? file)
    {
        var targets = GetSelectedTargets(file);
        if (targets.Count is 0) return;

        _clipboardItems =
            targets.Select(t =>
                new ClipboardItem
                {
                    Name = t.Name,
                    Path = CurrentPath.EndsWith('/') ? CurrentPath + t.Name : $"{CurrentPath}/{t.Name}",
                    IsDirectory = t.IsDirectory,
                    Size = t.Size,
                    LastWriteTime = t.LastWriteTime
                }).ToList();
        _clipboardIsCut = false;
        LoggingService.Info($"Copy set: {_clipboardItems.Count} item(s)");
        StatusMessage = _clipboardItems.Count == 1 ? $"Copied: {targets[0].Name}" : $"Copied {targets.Count} items";
    }

    [RelayCommand]
    private void Cut(FileEntry? file)
    {
        var targets = GetSelectedTargets(file);
        if (targets.Count == 0) return;

        _clipboardItems =
            targets.Select(t =>
                new ClipboardItem
                {
                    Name = t.Name,
                    Path = CurrentPath.EndsWith('/') ? CurrentPath + t.Name : $"{CurrentPath}/{t.Name}",
                    IsDirectory = t.IsDirectory,
                    Size = t.Size,
                    LastWriteTime = t.LastWriteTime
                }).ToList();
        _clipboardIsCut = true;
        LoggingService.Info($"Cut set: {_clipboardItems.Count} item(s)");
        StatusMessage = _clipboardItems.Count == 1 ? $"Cut: {targets[0].Name}" : $"Cut {targets.Count} items";
    }

    [RelayCommand]
    private async Task Paste()
    {
        await PasteInto(null);
    }

    [RelayCommand]
    private async Task PasteInto(FileEntry? targetFolder)
    {
        if (_clipboardItems.Count == 0 || !IsConnected) return;

        var destinationFolder = CurrentPath;
        if (targetFolder is { IsDirectory: true } && targetFolder.Name != "..")
        {
            destinationFolder =
                CurrentPath.EndsWith('/')
                    ? CurrentPath + targetFolder.Name
                    : $"{CurrentPath}/{targetFolder.Name}";
        }

        try
        {
            foreach (var item in _clipboardItems)
            {
                var fileName = item.Name;
                var destPath =
                    destinationFolder.EndsWith('/') ? destinationFolder + fileName : $"{destinationFolder}/{fileName}";

                if (item.Path == destPath) continue;

                LoggingService.Info(
                    $"Paste start: source={item.Path} dest={destPath} cut={_clipboardIsCut} isDir={item.IsDirectory}");

                if (await _sftpService.PathExistsAsync(destPath))
                {
                    var choice = await PromptConflictAsync(item, destPath);

                    if (choice == ConflictChoice.Cancel)
                    {
                        StatusMessage = "Paste cancelled";
                        return;
                    }

                    if (choice == ConflictChoice.Duplicate)
                    {
                        destPath = await GetUniqueDestinationPathAsync(destinationFolder, fileName);
                        fileName = Path.GetFileName(destPath);
                    }
                    else if (choice == ConflictChoice.Overwrite)
                    {
                        var destIsDir = await _sftpService.IsDirectoryAsync(destPath);
                        if (destIsDir)
                            await _sftpService.DeleteDirectoryAsync(destPath);
                        else
                            await _sftpService.DeleteFileAsync(destPath);
                    }
                }

                if (_clipboardIsCut)
                {
                    await _sftpService.RenameFileAsync(item.Path, destPath);
                    StatusMessage = $"Moved to: {fileName}";
                }
                else
                {
                    StatusMessage = $"Copying {fileName}...";
                    if (item.IsDirectory)
                    {
                        await _sftpService.CopyDirectoryAsync(item.Path, destPath);
                    }
                    else
                    {
                        await _sftpService.CopyFileAsync(item.Path, destPath);
                    }

                    StatusMessage = $"Copied to: {fileName}";
                }
            }

            if (_clipboardIsCut)
            {
                _clipboardItems.Clear();
            }

            await RefreshFileList();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Paste failed", ex);
            StatusMessage = $"Paste Error: {ex.Message}";
        }
    }

    private List<FileEntry> GetSelectedTargets(FileEntry? file)
    {
        var list = new List<FileEntry>();
        if (SelectedFiles is { Count: > 0 })
        {
            foreach (var item in SelectedFiles)
            {
                if (item is FileEntry entry && entry.Name != "..")
                    list.Add(entry);
            }
        }
        else if (file is not null && file.Name != "..")
        {
            list.Add(file);
        }
        else if (SelectedFile is not null && SelectedFile.Name != "..")
        {
            list.Add(SelectedFile);
        }

        return list;
    }

    private Task<ConflictChoice> PromptConflictAsync(ClipboardItem sourceItem,
        string destPath)
    {
        var sourceInfo = (sourceItem.IsDirectory, sourceItem.Size, sourceItem.LastWriteTime);
        return PromptConflictAsync(sourceItem.Name, sourceInfo, destPath);
    }

    private async Task<ConflictChoice> PromptConflictAsync(string name,
        (bool IsDirectory, long Size, DateTime LastWriteTime) sourceInfo,
        string destPath)
    {
        var destInfo = await _sftpService.GetEntryInfoAsync(destPath);
        var sourceDetails = FormatEntryDetails(sourceInfo.IsDirectory, sourceInfo.Size, sourceInfo.LastWriteTime);
        var destDetails = FormatEntryDetails(destInfo.IsDirectory, destInfo.Size, destInfo.LastWriteTime);

        return await _dialogService.ConfirmConflictAsync(
            "Name conflict",
            $"'{name}' already exists in the destination. What do you want to do?",
            sourceDetails,
            destDetails);
    }

    private async Task<string> GetUniqueDestinationPathAsync(string destinationFolder,
        string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var index = 1;

        while (true)
        {
            var candidateName =
                ext.Length > 0
                    ? $"{nameWithoutExt} ({index}){ext}"
                    : $"{nameWithoutExt} ({index})";
            var candidatePath =
                destinationFolder.EndsWith('/')
                    ? destinationFolder + candidateName
                    : $"{destinationFolder}/{candidateName}";

            if (!await _sftpService.PathExistsAsync(candidatePath))
                return candidatePath;

            index++;
        }
    }

    private static string FormatEntryDetails(bool isDirectory,
        long size,
        DateTime lastWriteTime)
    {
        if (isDirectory)
        {
            var dateText = lastWriteTime == DateTime.MinValue ? "Unknown" : lastWriteTime.ToString("yyyy-MM-dd HH:mm");
            return $"Directory, Modified: {dateText}";
        }

        var sizeText = $"{size / 1024.0:F2} KB";
        var modText = lastWriteTime == DateTime.MinValue ? "Unknown" : lastWriteTime.ToString("yyyy-MM-dd HH:mm");
        return $"File, Size: {sizeText}, Modified: {modText}";
    }

    // For Drag and Drop internal move
    public async Task MoveItem(string sourcePath,
        string destFolderPath)
    {
        if (!IsConnected) return;

        var fileName = System.IO.Path.GetFileName(sourcePath);
        var destPath = destFolderPath.EndsWith('/') ? destFolderPath + fileName : $"{destFolderPath}/{fileName}";

        if (sourcePath == destPath) return;

        try
        {
            LoggingService.Info($"Move start: source={sourcePath} destFolder={destFolderPath}");
            if (await _sftpService.PathExistsAsync(destPath))
            {
                var sourceInfo = await _sftpService.GetEntryInfoAsync(sourcePath);
                var choice = await PromptConflictAsync(fileName, sourceInfo, destPath);

                if (choice == ConflictChoice.Cancel)
                {
                    StatusMessage = "Move cancelled";
                    return;
                }

                if (choice == ConflictChoice.Duplicate)
                {
                    destPath = await GetUniqueDestinationPathAsync(destFolderPath, fileName);
                    fileName = Path.GetFileName(destPath);
                }
                else if (choice == ConflictChoice.Overwrite)
                {
                    var destIsDir = await _sftpService.IsDirectoryAsync(destPath);
                    if (destIsDir)
                        await _sftpService.DeleteDirectoryAsync(destPath);
                    else
                        await _sftpService.DeleteFileAsync(destPath);
                }
            }

            await _sftpService.RenameFileAsync(sourcePath, destPath);
            await RefreshFileList();
            StatusMessage = $"Moved {fileName} to {System.IO.Path.GetFileName(destFolderPath)}";
        }
        catch (Exception ex)
        {
            LoggingService.Error($"Move failed: source={sourcePath} destFolder={destFolderPath}", ex);
            StatusMessage = $"Move Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        _transferCts?.Cancel();
    }

    [RelayCommand]
    private async Task DownloadTo(FileEntry? file)
    {
        var target = file ?? SelectedFile;
        if (!IsConnected || target is null || target.IsDirectory || target.Name == "..") return;

        var localFolder = await _dialogService.PickFolderAsync();
        if (string.IsNullOrEmpty(localFolder)) return;

        _transferCts = new CancellationTokenSource();

        try
        {
            var remotePath = CurrentPath.EndsWith('/') ? CurrentPath + target.Name : $"{CurrentPath}/{target.Name}";
            var localPath = System.IO.Path.Combine(localFolder, target.Name);

            IsTransferring = true;
            TransferTitle = $"Downloading {target.Name}";
            TransferProgress = 0;

            var progress =
                new Progress<double>(p =>
                {
                    TransferProgress = p;
                    StatusMessage = $"Downloading {target.Name}: {p:F0}%";
                });

            await _sftpService.DownloadFileAsync(remotePath, localPath, progress, _transferCts.Token);
            StatusMessage = $"Downloaded: {target.Name}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Download cancelled";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Download failed", ex);
            StatusMessage = $"Download Error: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
            _transferCts.Dispose();
            _transferCts = null;
        }
    }

    public async Task UploadFiles(IEnumerable<string> localPaths)
    {
        if (!IsConnected) return;

        _transferCts = new CancellationTokenSource();

        try
        {
            IsTransferring = true;
            var pathList = new List<string>(localPaths);
            var total = pathList.Count;
            var current = 0;

            foreach (var localPath in pathList.TakeWhile(_ => !_transferCts.Token.IsCancellationRequested))
            {
                current++;
                var fileName = System.IO.Path.GetFileName(localPath);
                var remotePath = CurrentPath.EndsWith('/') ? CurrentPath + fileName : $"{CurrentPath}/{fileName}";

                TransferTitle = $"Uploading {current}/{total}: {fileName}";
                TransferProgress = 0;

                var progress =
                    new Progress<double>(p =>
                    {
                        TransferProgress = p;
                        StatusMessage = $"Uploading {fileName}: {p:F0}%";
                    });

                await _sftpService.UploadFileAsync(localPath, remotePath, progress, _transferCts.Token);
            }

            StatusMessage = _transferCts.Token.IsCancellationRequested ? "Upload cancelled" : "Upload complete";

            await RefreshFileList();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Upload cancelled";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Upload failed", ex);
            StatusMessage = $"Upload Error: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
            _transferCts.Dispose();
            _transferCts = null;
        }
    }
}
