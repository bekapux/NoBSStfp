using System.Collections.Generic;

namespace NoBSSftp.Models;

public class ConnectInfo
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public IReadOnlyList<AuthMethodPreference> AuthPreferenceOrder { get; init; } = [];
    public string PrivateKeyPath { get; init; } = "";
    public string PrivateKeyPassphrase { get; init; } = "";
}
