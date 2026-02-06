using System;

namespace NoBSSftp.Models;

public class TrustedHostKeyEntry
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string KeyAlgorithm { get; set; } = string.Empty;
    public string FingerprintSha256 { get; set; } = string.Empty;
    public DateTime TrustedAtUtc { get; set; } = DateTime.UtcNow;
}
