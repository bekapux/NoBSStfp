using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Avalonia.Threading;
using NoBSSftp.Models;
using Renci.SshNet.Common;

namespace NoBSSftp.Services;

public interface IHostKeyTrustService
{
    bool IsTrustedHostKey(ServerProfile profile, HostKeyEventArgs args);
    IReadOnlyList<TrustedHostKeyEntry> GetTrustedHostKeys();
    bool RemoveTrustedHostKey(TrustedHostKeyEntry entry);
    void ClearTrustedHostKeys();
}

public class HostKeyTrustService : IHostKeyTrustService
{
    private readonly IDialogService _dialogService;
    private readonly string _knownHostsPath;
    private readonly Lock _gate = new();

    public HostKeyTrustService() : this(new DialogService())
    {
    }

    public HostKeyTrustService(IDialogService dialogService)
    {
        _dialogService = dialogService;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "NoBSSftp");
        Directory.CreateDirectory(folder);
        _knownHostsPath = Path.Combine(folder, "known_hosts.json");
    }

    public bool IsTrustedHostKey(ServerProfile profile,
        HostKeyEventArgs args)
    {
        try
        {
            var host = NormalizeHost(profile.Host);
            if (string.IsNullOrWhiteSpace(host))
                return false;

            var port = profile.Port > 0 ? profile.Port : 22;
            var algorithm = string.IsNullOrWhiteSpace(args.HostKeyName) ? "unknown" : args.HostKeyName.Trim();
            var fingerprint = BuildSha256Fingerprint(args.HostKey);
            if (string.IsNullOrWhiteSpace(fingerprint))
                return false;

            lock (_gate)
            {
                var entries = LoadTrustedKeys();
                var entry =
                    entries.FirstOrDefault(
                        e =>
                            e.Port == port &&
                            e.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                            e.KeyAlgorithm.Equals(algorithm, StringComparison.Ordinal));

                if (entry is null)
                {
                    var trusted = PromptForFirstSeenHost(host, port, algorithm, args.KeyLength, fingerprint);
                    if (!trusted)
                        return false;

                    entries.Add(
                        new TrustedHostKeyEntry
                        {
                            Host = host,
                            Port = port,
                            KeyAlgorithm = algorithm,
                            FingerprintSha256 = fingerprint,
                            TrustedAtUtc = DateTime.UtcNow
                        });
                    SaveTrustedKeys(entries);
                    return true;
                }

                if (entry.FingerprintSha256.Equals(fingerprint, StringComparison.Ordinal))
                    return true;

                var replaceTrust =
                    PromptForHostKeyMismatch(host,
                        port,
                        algorithm,
                        entry.FingerprintSha256,
                        fingerprint,
                        args.KeyLength);
                if (!replaceTrust)
                    return false;

                entry.FingerprintSha256 = fingerprint;
                entry.TrustedAtUtc = DateTime.UtcNow;
                SaveTrustedKeys(entries);
                return true;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Host key trust verification failed", ex);
            return false;
        }
    }

    public IReadOnlyList<TrustedHostKeyEntry> GetTrustedHostKeys()
    {
        lock (_gate)
        {
            return LoadTrustedKeys()
                .OrderBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Port)
                .ThenBy(e => e.KeyAlgorithm, StringComparer.Ordinal)
                .Select(CloneEntry)
                .ToList();
        }
    }

    public bool RemoveTrustedHostKey(TrustedHostKeyEntry entry)
    {
        var host = NormalizeHost(entry.Host);
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var algorithm = (entry.KeyAlgorithm ?? string.Empty).Trim();
        if (algorithm.Length == 0)
            return false;

        lock (_gate)
        {
            var entries = LoadTrustedKeys();
            var removed = entries.RemoveAll(
                existing =>
                    existing.Port == entry.Port &&
                    existing.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                    existing.KeyAlgorithm.Equals(algorithm, StringComparison.Ordinal)) > 0;

            if (!removed)
                return false;

            SaveTrustedKeys(entries);
            return true;
        }
    }

    public void ClearTrustedHostKeys()
    {
        lock (_gate)
        {
            SaveTrustedKeys([]);
        }
    }

    private bool PromptForFirstSeenHost(string host,
        int port,
        string algorithm,
        int keyLength,
        string fingerprint)
    {
        var message = $"First-time host key verification for {host}:{port}";
        var details =
            $"Host: {host}\n" +
            $"Port: {port}\n" +
            $"Algorithm: {algorithm}\n" +
            $"Key length: {keyLength}\n" +
            "Fingerprint (SHA256):\n" +
            $"{fingerprint}\n\n" +
            "Trust this host key and continue?";

        return PromptForTrust("Verify SSH Host Key", message, details, isWarning: false);
    }

    private bool PromptForHostKeyMismatch(string host,
        int port,
        string algorithm,
        string storedFingerprint,
        string presentedFingerprint,
        int keyLength)
    {
        var message = $"Host key mismatch detected for {host}:{port}";
        var details =
            $"Host: {host}\n" +
            $"Port: {port}\n" +
            $"Algorithm: {algorithm}\n" +
            $"Key length: {keyLength}\n\n" +
            "Stored fingerprint (SHA256):\n" +
            $"{storedFingerprint}\n\n" +
            "Presented fingerprint (SHA256):\n" +
            $"{presentedFingerprint}\n\n" +
            "If you were not expecting this change, reject and investigate.\n" +
            "Trust the new host key and continue?";

        return PromptForTrust("SSH Host Key Mismatch", message, details, isWarning: true);
    }

    private bool PromptForTrust(string title,
        string message,
        string details,
        bool isWarning)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            LoggingService.Warn("Host key prompt requested on UI thread; rejecting connection for safety.");
            return false;
        }

        var task = Dispatcher.UIThread.InvokeAsync(
            async () => await _dialogService.ConfirmHostKeyAsync(title, message, details, isWarning))
            .GetAwaiter()
            .GetResult();
        return task;
    }

    private List<TrustedHostKeyEntry> LoadTrustedKeys()
    {
        if (!File.Exists(_knownHostsPath))
            return [];

        try
        {
            var json = File.ReadAllText(_knownHostsPath);
            return JsonSerializer.Deserialize(json, SerializationContext.Default.ListTrustedHostKeyEntry) ?? [];
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Failed to load trusted host key store. {ex.Message}");
            return [];
        }
    }

    private static TrustedHostKeyEntry CloneEntry(TrustedHostKeyEntry source)
    {
        return new TrustedHostKeyEntry
        {
            Host = source.Host,
            Port = source.Port,
            KeyAlgorithm = source.KeyAlgorithm,
            FingerprintSha256 = source.FingerprintSha256,
            TrustedAtUtc = source.TrustedAtUtc
        };
    }

    private void SaveTrustedKeys(List<TrustedHostKeyEntry> entries)
    {
        try
        {
            var json = JsonSerializer.Serialize(entries, SerializationContext.Default.ListTrustedHostKeyEntry);
            File.WriteAllText(_knownHostsPath, json);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Failed to save trusted host key store. {ex.Message}");
        }
    }

    private static string BuildSha256Fingerprint(byte[]? hostKey)
    {
        if (hostKey is not { Length: > 0 })
            return string.Empty;

        var digest = SHA256.HashData(hostKey);
        return $"SHA256:{Convert.ToBase64String(digest).TrimEnd('=')}";
    }

    private static string NormalizeHost(string host)
    {
        return host.Trim().ToLowerInvariant();
    }
}
