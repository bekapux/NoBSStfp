using System.Collections.Generic;

namespace NoBSSftp.Models;

public class ServerProfile
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Server";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public int KeepAliveIntervalSeconds { get; set; } = 30;
    public ReconnectStrategy ReconnectStrategy { get; set; } = ReconnectStrategy.FixedInterval;
    public int ReconnectAttempts { get; set; } = 2;
    public int ReconnectDelaySeconds { get; set; } = 2;

    public bool UsePrivateKey { get; set; }
    public List<AuthMethodPreference> AuthPreferenceOrder { get; set; } = [];
    public string PrivateKeyPath { get; set; } = "";
    public string PrivateKeyPassphrase { get; set; } = "";

    public string Password { get; set; } = "";
}
