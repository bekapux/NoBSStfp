namespace NoBSSftp.Models;

public class ServerProfile
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Server";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";

    public bool UsePrivateKey { get; set; }
    public string PrivateKeyPath { get; set; } = "";
    public string PrivateKeyPassphrase { get; set; } = "";
    
    // TODO
    public string Password { get; set; } = "";
}
