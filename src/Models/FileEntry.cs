using System;

namespace NoBSSftp.Models;

public class FileEntry
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; init; }
    public bool IsDirectory { get; set; }
    public string Permissions { get; set; } = string.Empty;
    public DateTime LastWriteTime { get; set; }

    public bool IsHidden => Name.StartsWith('.');

    public string SizeDisplay => IsDirectory ? "" : $"{Size / 1024.0:F2} KB";
    public string Icon => IsDirectory ? "qa-folder" : "qa-file";
}