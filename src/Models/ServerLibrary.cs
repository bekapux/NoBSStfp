using System.Collections.Generic;

namespace NoBSSftp.Models;

public class ServerLibrary
{
    public List<ServerProfile> RootServers { get; set; } = [];
    public List<ServerFolder> Folders { get; set; } = [];
}