using System;

namespace NoBSSftp.Models;

public class RemotePosixAttributes
{
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteTime { get; init; }
    public string SymbolicPermissions { get; init; } = string.Empty;
    public string OctalPermissions { get; init; } = "0000";
    public short PermissionMode { get; init; }
    public int UserId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public int GroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;
}
