using System.Collections.ObjectModel;

namespace NoBSSftp.Models;

public class RemoteTreeNode
{
    private RemoteTreeNode(FileEntry entry,
        bool isPlaceholder)
    {
        Entry = entry;
        IsPlaceholder = isPlaceholder;
    }

    public RemoteTreeNode(FileEntry entry) : this(entry, false)
    {
        if (entry.IsDirectory && !entry.IsSymbolicLink)
            Children.Add(CreatePlaceholder(entry.RemotePath));
    }

    public FileEntry Entry { get; }

    public string Name => Entry.Name;
    public string NameDisplay => Entry.NameDisplay;

    public string RemotePath => Entry.RemotePath;

    public bool IsDirectory => Entry.IsDirectory;
    public bool IsSymbolicLink => Entry.IsSymbolicLink;
    public bool ShowDirectoryIcon => Entry.ShowDirectoryIcon;
    public bool ShowFileIcon => Entry.ShowFileIcon;
    public bool ShowSymlinkIcon => Entry.ShowSymlinkIcon;

    public string SizeDisplay => IsPlaceholder ? string.Empty : Entry.SizeDisplay;

    public string DateDisplay => IsPlaceholder ? string.Empty : Entry.DateDisplay;

    public bool IsPlaceholder { get; }

    public bool IsLoaded { get; set; }

    public bool IsLoading { get; set; }

    public ObservableCollection<RemoteTreeNode> Children { get; } = [];

    private static RemoteTreeNode CreatePlaceholder(string parentPath)
    {
        return new RemoteTreeNode(
            new FileEntry
            {
                Name = "Loading...",
                RemotePath = parentPath,
                IsDirectory = false
            },
            true);
    }
}
