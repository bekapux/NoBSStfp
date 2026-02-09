using System;

namespace NoBSSftp.Models;

public class FileEntry
{
    public string Name { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public long Size { get; init; }
    public bool IsDirectory { get; set; }
    public bool IsSymbolicLink { get; set; }
    public string SymbolicLinkTarget { get; set; } = string.Empty;
    public bool SymbolicLinkTargetIsDirectory { get; set; }
    public string Permissions { get; set; } = string.Empty;
    public DateTime LastWriteTime { get; set; }

    public bool IsHidden => Name.StartsWith('.');

    public string NameDisplay =>
        IsSymbolicLink
            ? $"{Name} (link)"
            : Name;

    public bool IsLinkToDirectory => IsSymbolicLink && SymbolicLinkTargetIsDirectory;
    public bool ShowDirectoryIcon => IsDirectory && !IsSymbolicLink;
    public bool ShowFileIcon => !IsDirectory && !IsSymbolicLink;
    public bool ShowSymlinkIcon => IsSymbolicLink;

    public string SizeDisplay => IsDirectory ? string.Empty : FormatFileSize(Size);

    public string DateDisplay =>
        Name == ".." || LastWriteTime == DateTime.MinValue
            ? string.Empty
            : LastWriteTime.ToString();

    public string Icon => IsDirectory ? "qa-folder" : "qa-file";

    private static string FormatFileSize(long size)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = Math.Max(0, size);
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:F0} {units[unitIndex]}"
            : $"{value:F2} {units[unitIndex]}";
    }
}
