using System;

namespace NoBSSftp.Models;

public class RemotePropertiesDialogRequest
{
    public string Name { get; init; } = string.Empty;
    public string RemotePath { get; init; } = "/";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteTime { get; init; }
    public string SymbolicPermissions { get; init; } = string.Empty;
    public string OctalPermissions { get; init; } = "0000";
    public int UserId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public int GroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;
}

public class RemotePropertiesDialogResult
{
    public bool ApplyPermissions { get; init; }
    public short PermissionMode { get; init; }
    public bool ApplyOwnerId { get; init; }
    public int OwnerId { get; init; }
    public bool ApplyGroupId { get; init; }
    public int GroupId { get; init; }
    public bool ApplyRecursively { get; init; }
}
