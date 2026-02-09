using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NoBSSftp.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SshNet.Agent;

namespace NoBSSftp.Services;

public interface ISftpService
{
    bool IsConnected { get; }
    Task ConnectAsync(ServerProfile profile);
    Task<string> GetCurrentDirectoryAsync();
    Task DisconnectAsync();
    Task<List<FileEntry>> ListDirectoryAsync(string path);

    Task UploadFileAsync(string localPath,
        string remotePath,
        IProgress<double> progress,
        CancellationToken cancellationToken = default,
        bool resume = false);

    Task DownloadFileAsync(string remotePath,
        string localPath,
        IProgress<double> progress,
        CancellationToken cancellationToken = default,
        bool resume = false);

    Task<string> ComputeRemoteFileSha256Async(string remotePath,
        CancellationToken cancellationToken = default);

    Task RenameFileAsync(string oldPath,
        string newPath);

    Task DeleteFileAsync(string path);
    Task DeleteDirectoryAsync(string path);
    Task CreateSymbolicLinkAsync(string targetPath,
        string linkPath,
        CancellationToken cancellationToken = default);
    Task<string?> GetSymbolicLinkTargetAsync(string linkPath,
        CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path);
    Task CreateFileAsync(string path);

    Task CopyFileAsync(string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default);

    Task CopyDirectoryAsync(string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default);

    Task PromoteUploadedFileAtomicallyAsync(string temporaryRemotePath,
        string destinationRemotePath,
        CancellationToken cancellationToken = default);

    Task<RemotePosixAttributes> GetPosixAttributesAsync(string path,
        CancellationToken cancellationToken = default);
    Task SetPosixAttributesAsync(string path,
        short? permissionMode = null,
        int? userId = null,
        int? groupId = null,
        CancellationToken cancellationToken = default);

    Task<bool> PathExistsAsync(string path);
    Task<bool> IsDirectoryAsync(string path);
    Task<(bool IsDirectory, long Size, DateTime LastWriteTime)> GetEntryInfoAsync(string path);
    SshClient CreateSshClient(ServerProfile profile);
}

public class SftpService : ISftpService
{
    private SftpClient? _sftpClient;
    private readonly IHostKeyTrustService _hostKeyTrustService;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private ServerProfile? _activeProfile;
    private SshAgent? _agentClient;
    private ConnectionResiliencePolicy _activePolicy = ConnectionResiliencePolicy.Default;

    public SftpService()
    {
        _hostKeyTrustService = new HostKeyTrustService();
    }

    public bool IsConnected => _sftpClient?.IsConnected ?? false;

    private readonly record struct ConnectionResiliencePolicy(
        int ConnectionTimeoutSeconds,
        int KeepAliveIntervalSeconds,
        ReconnectStrategy ReconnectStrategy,
        int ReconnectAttempts,
        int ReconnectDelaySeconds)
    {
        public static ConnectionResiliencePolicy Default =>
            new(
                ConnectionTimeoutSeconds: 15,
                KeepAliveIntervalSeconds: 30,
                ReconnectStrategy.FixedInterval,
                ReconnectAttempts: 2,
                ReconnectDelaySeconds: 2);

        public static ConnectionResiliencePolicy FromProfile(ServerProfile profile)
        {
            var timeoutSeconds = Math.Clamp(profile.ConnectionTimeoutSeconds <= 0 ? 15 : profile.ConnectionTimeoutSeconds, 3, 300);
            var keepAliveSeconds = Math.Clamp(profile.KeepAliveIntervalSeconds, 0, 300);
            var strategy = profile.ReconnectStrategy;
            var reconnectAttempts =
                strategy == ReconnectStrategy.None
                    ? 0
                    : Math.Clamp(profile.ReconnectAttempts <= 0 ? 2 : profile.ReconnectAttempts, 1, 10);
            var reconnectDelaySeconds =
                strategy == ReconnectStrategy.None
                    ? 0
                    : Math.Clamp(profile.ReconnectDelaySeconds <= 0 ? 2 : profile.ReconnectDelaySeconds, 1, 30);

            return new ConnectionResiliencePolicy(
                timeoutSeconds,
                keepAliveSeconds,
                strategy,
                reconnectAttempts,
                reconnectDelaySeconds);
        }
    }

    public async Task ConnectAsync(ServerProfile profile)
    {
        if (profile is null)
            throw new ArgumentNullException(nameof(profile));

        var profileSnapshot = CloneProfile(profile);
        var policy = ConnectionResiliencePolicy.FromProfile(profileSnapshot);

        await _connectionGate.WaitAsync();
        try
        {
            _activeProfile = null;
            _activePolicy = ConnectionResiliencePolicy.Default;
            await ConnectWithPolicyAsync(profileSnapshot, policy, allowRetry: true, CancellationToken.None);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            await Task.Run(DisconnectAndDisposeClient);
            _activeProfile = null;
            _activePolicy = ConnectionResiliencePolicy.Default;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ConnectWithPolicyAsync(ServerProfile profile,
        ConnectionResiliencePolicy policy,
        bool allowRetry,
        CancellationToken cancellationToken)
    {
        var attempts =
            allowRetry && policy.ReconnectStrategy == ReconnectStrategy.FixedInterval
                ? 1 + policy.ReconnectAttempts
                : 1;

        Exception? lastException = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ConnectOnceAsync(profile, policy, cancellationToken);
                _activeProfile = CloneProfile(profile);
                _activePolicy = policy;
                return;
            }
            catch (Exception ex) when (
                allowRetry &&
                attempt < attempts &&
                IsRetryableConnectException(ex))
            {
                lastException = ex;
                LoggingService.Warn(
                    $"Connect attempt {attempt}/{attempts} failed. Retrying in {policy.ReconnectDelaySeconds}s. {ex.Message}");

                if (policy.ReconnectDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(policy.ReconnectDelaySeconds), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("Failed to establish SFTP connection.");
    }

    private async Task ConnectOnceAsync(ServerProfile profile,
        ConnectionResiliencePolicy policy,
        CancellationToken cancellationToken)
    {
        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                DisconnectAndDisposeClient();

                var connectionInfo = BuildConnectionInfo(profile, policy.ConnectionTimeoutSeconds);
                var candidateClient =
                    new SftpClient(connectionInfo)
                    {
                        KeepAliveInterval = TimeSpan.FromSeconds(policy.KeepAliveIntervalSeconds),
                        OperationTimeout = TimeSpan.FromSeconds(policy.ConnectionTimeoutSeconds)
                    };

                candidateClient.HostKeyReceived +=
                    (_, args) =>
                    {
                        args.CanTrust = _hostKeyTrustService.IsTrustedHostKey(profile, args);
                    };

                try
                {
                    candidateClient.Connect();
                    _sftpClient = candidateClient;
                }
                catch
                {
                    try
                    {
                        candidateClient.Dispose();
                    }
                    catch
                    {
                        // Ignore cleanup failures for failed connection attempts.
                    }

                    _sftpClient = null;
                    throw;
                }
            },
            cancellationToken);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_sftpClient is { IsConnected: true })
            return;

        var profile = _activeProfile;
        var policy = _activePolicy;
        if (profile is null)
            throw new InvalidOperationException("Not connected");

        if (policy.ReconnectStrategy == ReconnectStrategy.None)
            throw new InvalidOperationException("Not connected");

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_sftpClient is { IsConnected: true })
                return;

            await ConnectWithPolicyAsync(profile, policy, allowRetry: true, cancellationToken);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ForceReconnectAsync(CancellationToken cancellationToken)
    {
        var profile = _activeProfile;
        var policy = _activePolicy;
        if (profile is null)
            throw new InvalidOperationException("Not connected");

        if (policy.ReconnectStrategy == ReconnectStrategy.None)
            throw new InvalidOperationException("Not connected");

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            await ConnectWithPolicyAsync(profile, policy, allowRetry: false, cancellationToken);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<T> ExecuteWithReconnectRetryAsync<T>(Func<T> action,
        CancellationToken cancellationToken = default)
    {
        var retries =
            _activePolicy.ReconnectStrategy == ReconnectStrategy.FixedInterval
                ? Math.Max(0, _activePolicy.ReconnectAttempts)
                : 0;

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureConnectedAsync(cancellationToken);

            try
            {
                return await Task.Run(action, cancellationToken);
            }
            catch (Exception ex) when (
                attempt < retries &&
                IsRetryableOperationException(ex))
            {
                LoggingService.Warn(
                    $"SFTP operation failed due to connection issue. Reconnecting ({attempt + 1}/{retries}). {ex.Message}");
                await ForceReconnectAsync(cancellationToken);
            }
        }
    }

    private static bool IsRetryableOperationException(Exception ex)
    {
        if (ex is OperationCanceledException)
            return false;

        if (ex is SftpPathNotFoundException or SftpPermissionDeniedException)
            return false;

        if (ex is InvalidOperationException invalid &&
            !invalid.Message.Contains("Not connected", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ex is SshConnectionException ||
               ex is SshOperationTimeoutException ||
               ex is SocketException ||
               ex is IOException ||
               ex is ObjectDisposedException ||
               ex is InvalidOperationException;
    }

    private ConnectionInfo BuildConnectionInfo(ServerProfile profile,
        int timeoutSeconds)
    {
        var host = string.IsNullOrWhiteSpace(profile.Host) ? "localhost" : profile.Host;
        var port = profile.Port <= 0 ? 22 : profile.Port;
        var username = string.IsNullOrWhiteSpace(profile.Username) ? "root" : profile.Username;
        var authMethods = BuildAuthenticationMethods(profile, username);
        var connectionInfo = new ConnectionInfo(host, port, username, authMethods.ToArray());
        connectionInfo.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        return connectionInfo;
    }

    private List<AuthenticationMethod> BuildAuthenticationMethods(ServerProfile profile,
        string username)
    {
        var methods = new List<AuthenticationMethod>(3);
        var unavailableReasons = new List<string>(3);
        var authOrder = AuthPreferenceOrder.Normalize(profile.AuthPreferenceOrder, profile.UsePrivateKey);

        foreach (var authMethod in authOrder)
        {
            switch (authMethod)
            {
                case AuthMethodPreference.Agent:
                    if (!TryAppendAgentAuthenticationMethod(methods, username, out var agentReason) &&
                        !string.IsNullOrWhiteSpace(agentReason))
                    {
                        unavailableReasons.Add(agentReason);
                    }

                    break;
                case AuthMethodPreference.PrivateKey:
                    if (!TryAppendPrivateKeyAuthenticationMethod(methods, username, profile, out var keyReason) &&
                        !string.IsNullOrWhiteSpace(keyReason))
                    {
                        unavailableReasons.Add(keyReason);
                    }

                    break;
                case AuthMethodPreference.Password:
                    if (!TryAppendPasswordAuthenticationMethod(methods, username, profile, out var passwordReason) &&
                        !string.IsNullOrWhiteSpace(passwordReason))
                    {
                        unavailableReasons.Add(passwordReason);
                    }

                    break;
            }
        }

        if (methods.Count > 0)
            return methods;

        var details =
            unavailableReasons.Count == 0
                ? "Configure an SSH agent identity, private key path, or password."
                : string.Join(" ", unavailableReasons.Distinct(StringComparer.Ordinal));
        throw new InvalidOperationException($"No authentication methods are available. {details}");
    }

    private bool TryAppendAgentAuthenticationMethod(List<AuthenticationMethod> methods,
        string username,
        out string reason)
    {
        try
        {
            var agent = GetOrCreateAgentClient();
            var identities = agent.RequestIdentities();
            if (identities.Length == 0)
            {
                reason = "SSH agent has no loaded identities.";
                return false;
            }

            methods.Add(new PrivateKeyAuthenticationMethod(username, identities));
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            DisposeAgentClient();
            reason = $"SSH agent unavailable ({ex.Message}).";
            LoggingService.Warn(reason);
            return false;
        }
    }

    private static bool TryAppendPrivateKeyAuthenticationMethod(List<AuthenticationMethod> methods,
        string username,
        ServerProfile profile,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            reason = "Private key path is not configured.";
            return false;
        }

        try
        {
            var keyPath = ResolvePrivateKeyPath(profile.PrivateKeyPath);
            var keyFile =
                string.IsNullOrEmpty(profile.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(keyPath)
                    : new PrivateKeyFile(keyPath, profile.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Private key authentication unavailable ({ex.Message}).";
            LoggingService.Warn(reason);
            return false;
        }
    }

    private static bool TryAppendPasswordAuthenticationMethod(List<AuthenticationMethod> methods,
        string username,
        ServerProfile profile,
        out string reason)
    {
        if (string.IsNullOrEmpty(profile.Password))
        {
            reason = "Password is not configured.";
            return false;
        }

        methods.Add(new PasswordAuthenticationMethod(username, profile.Password));
        reason = string.Empty;
        return true;
    }

    private SshAgent GetOrCreateAgentClient()
    {
        if (_agentClient is not null)
            return _agentClient;

        _agentClient =
            OperatingSystem.IsWindows()
                ? new Pageant()
                : string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSH_AUTH_SOCK"))
                    ? new SshAgent()
                    : new SshAgent(Environment.GetEnvironmentVariable("SSH_AUTH_SOCK")!);
        return _agentClient;
    }

    private static bool IsRetryableConnectException(Exception ex)
    {
        if (ex is OperationCanceledException)
            return false;

        if (ex is SshAuthenticationException)
            return false;

        return ex is SshConnectionException ||
               ex is SshOperationTimeoutException ||
               ex is SocketException ||
               ex is IOException ||
               ex is ObjectDisposedException;
    }

    private static ServerProfile CloneProfile(ServerProfile profile)
    {
        return new ServerProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            ConnectionTimeoutSeconds = profile.ConnectionTimeoutSeconds,
            KeepAliveIntervalSeconds = profile.KeepAliveIntervalSeconds,
            ReconnectStrategy = profile.ReconnectStrategy,
            ReconnectAttempts = profile.ReconnectAttempts,
            ReconnectDelaySeconds = profile.ReconnectDelaySeconds,
            UsePrivateKey = profile.UsePrivateKey,
            AuthPreferenceOrder = AuthPreferenceOrder.Normalize(profile.AuthPreferenceOrder, profile.UsePrivateKey),
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
            Password = profile.Password
        };
    }

    private void DisconnectAndDisposeClient()
    {
        try
        {
            _sftpClient?.Disconnect();
        }
        catch
        {
            // Ignore disconnect failures during cleanup.
        }

        try
        {
            _sftpClient?.Dispose();
        }
        catch
        {
            // Ignore dispose failures during cleanup.
        }
        finally
        {
            _sftpClient = null;
            DisposeAgentClient();
        }
    }

    private void DisposeAgentClient()
    {
        _agentClient = null;
    }

    public async Task<string> GetCurrentDirectoryAsync()
    {
        return await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is not { IsConnected: true })
                    throw new InvalidOperationException("Not connected");

                return _sftpClient.WorkingDirectory;
            });
    }

    public async Task<List<FileEntry>> ListDirectoryAsync(string path)
    {
        return await ExecuteWithReconnectRetryAsync(() =>
        {
            if (_sftpClient is null || !_sftpClient.IsConnected)
                throw new InvalidOperationException("Not connected");

            var files = _sftpClient.ListDirectory(path);
            return files.Where(f => f.Name != "." && f.Name != "..")
                .Select(
                    f =>
                    {
                        var isSymbolicLink = f.IsSymbolicLink || f.Attributes?.IsSymbolicLink == true;
                        var symbolicLinkTargetIsDirectory = false;
                        if (isSymbolicLink)
                        {
                            try
                            {
                                symbolicLinkTargetIsDirectory = _sftpClient.GetAttributes(f.FullName).IsDirectory;
                            }
                            catch
                            {
                                symbolicLinkTargetIsDirectory = false;
                            }
                        }

                        return new FileEntry
                        {
                            Name = f.Name,
                            RemotePath = f.FullName,
                            Size = f.Length,
                            IsDirectory = f.IsDirectory,
                            IsSymbolicLink = isSymbolicLink,
                            SymbolicLinkTargetIsDirectory = symbolicLinkTargetIsDirectory,
                            Permissions = f.Attributes?.ToString() ?? string.Empty, // Simplified
                            LastWriteTime = f.LastWriteTime
                        };
                    })
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name)
                .ToList();
        });
    }

    public async Task UploadFileAsync(string localPath,
        string remotePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default,
        bool resume = false)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sftpClient is not { IsConnected: true })
            throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            var localInfo = new FileInfo(localPath);
            var totalLength = localInfo.Length;
            long offset = 0;

            if (resume && _sftpClient.Exists(remotePath))
            {
                try
                {
                    var existing = _sftpClient.GetAttributes(remotePath);
                    if (!existing.IsDirectory)
                    {
                        var existingSize = (long)existing.Size;
                        if (existingSize == totalLength)
                        {
                            progress?.Report(100);
                            return;
                        }

                        offset = existingSize > totalLength ? 0 : existingSize;
                    }
                }
                catch
                {
                    offset = 0;
                }
            }

            if (totalLength == 0)
            {
                using var emptyTarget = _sftpClient.Open(remotePath, FileMode.Create, FileAccess.Write);
                progress?.Report(100);
                return;
            }

            using var sourceStream = File.Open(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            sourceStream.Seek(offset, SeekOrigin.Begin);

            Stream targetStream;
            if (offset > 0)
            {
                try
                {
                    targetStream = _sftpClient.Open(remotePath, FileMode.OpenOrCreate, FileAccess.Write);
                    targetStream.Seek(offset, SeekOrigin.Begin);
                }
                catch
                {
                    targetStream = _sftpClient.Open(remotePath, FileMode.Create, FileAccess.Write);
                    offset = 0;
                    sourceStream.Seek(0, SeekOrigin.Begin);
                }
            }
            else
            {
                targetStream = _sftpClient.Open(remotePath, FileMode.Create, FileAccess.Write);
            }

            using (targetStream)
            {
                var streams = (Source: (Stream)sourceStream, Target: targetStream);
                using var ctr =
                    cancellationToken.Register(
                        static state =>
                        {
                            var pair = ((ValueTuple<Stream, Stream>)state!);
                            pair.Item1.Dispose();
                            pair.Item2.Dispose();
                        },
                        streams);

                try
                {
                    var buffer = new byte[64 * 1024];
                    long uploaded = offset;
                    int read;
                    while ((read = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        targetStream.Write(buffer, 0, read);
                        uploaded += read;
                        progress?.Report(uploaded * 100.0 / totalLength);
                    }

                    targetStream.Flush();
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    TryDeleteRemoteFile(remotePath);
                    throw new OperationCanceledException();
                }
            }
        }, cancellationToken);
    }

    public async Task DownloadFileAsync(string remotePath,
        string localPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default,
        bool resume = false)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sftpClient is null || !_sftpClient.IsConnected)
            throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            var attributes = _sftpClient.GetAttributes(remotePath);
            var totalSize = (long)attributes.Size;
            long offset = 0;

            if (resume && File.Exists(localPath))
            {
                try
                {
                    var localSize = new FileInfo(localPath).Length;
                    if (localSize == totalSize)
                    {
                        progress?.Report(100);
                        return;
                    }

                    if (localSize > totalSize)
                    {
                        offset = 0;
                    }
                    else if (localSize > 0 && localSize < totalSize)
                        offset = localSize;
                }
                catch
                {
                    offset = 0;
                }
            }

            if (totalSize == 0)
            {
                using var emptyFile = File.Create(localPath);
                TrySetLocalLastWriteTime(localPath, attributes.LastWriteTime);
                progress?.Report(100);
                return;
            }

            using var sourceStream = _sftpClient.OpenRead(remotePath);
            if (offset > 0)
            {
                try
                {
                    sourceStream.Seek(offset, SeekOrigin.Begin);
                }
                catch
                {
                    offset = 0;
                }
            }

            var mode = offset > 0 ? FileMode.Append : FileMode.Create;
            using var targetStream = new FileStream(localPath, mode, FileAccess.Write, FileShare.None);

            var streams = (Source: (Stream)sourceStream, Target: (Stream)targetStream);
            using var ctr =
                cancellationToken.Register(
                    static state =>
                    {
                        var pair = ((ValueTuple<Stream, Stream>)state!);
                        pair.Item1.Dispose();
                        pair.Item2.Dispose();
                    },
                    streams);

            try
            {
                var buffer = new byte[64 * 1024];
                long downloaded = offset;
                int read;
                while ((read = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    targetStream.Write(buffer, 0, read);
                    downloaded += read;
                    progress?.Report(downloaded * 100.0 / Math.Max(totalSize, 1));
                }

                targetStream.Flush();
                TrySetLocalLastWriteTime(localPath, attributes.LastWriteTime);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
        }, cancellationToken);
    }

    public async Task<string> ComputeRemoteFileSha256Async(string remotePath,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sftpClient is not { IsConnected: true })
            throw new InvalidOperationException("Not connected");

        return await Task.Run(() =>
        {
            using var sourceStream = _sftpClient.OpenRead(remotePath);
            using var sha = SHA256.Create();
            var stream = (Stream)sourceStream;
            using var ctr =
                cancellationToken.Register(
                    static state =>
                    {
                        ((Stream)state!).Dispose();
                    },
                    stream);

            var buffer = new byte[64 * 1024];
            int read;
            while ((read = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, read, null, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
        }, cancellationToken);
    }

    public async Task RenameFileAsync(string oldPath,
        string newPath)
    {
        await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is null || !_sftpClient.IsConnected)
                    throw new InvalidOperationException("Not connected");
                _sftpClient.RenameFile(oldPath, newPath);
                return true;
            });
    }

    public async Task DeleteFileAsync(string path)
    {
        await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is not { IsConnected: true })
                    throw new InvalidOperationException("Not connected");
                _sftpClient.DeleteFile(path);
                return true;
            });
    }

    public async Task DeleteDirectoryAsync(string path)
    {
        await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is null || !_sftpClient.IsConnected)
                    throw new InvalidOperationException("Not connected");

                var directoryAttributes = _sftpClient.GetAttributes(path);
                if (directoryAttributes.IsSymbolicLink)
                {
                    _sftpClient.DeleteFile(path);
                    return true;
                }

                DeleteDirectoryRecursive(path);
                return true;
            });
    }

    private void DeleteDirectoryRecursive(string path)
    {
        if (_sftpClient is null) return;

        foreach (var file in _sftpClient.ListDirectory(path))
        {
            if (file.Name is "." or "..") continue;

            if (file.IsDirectory && !file.IsSymbolicLink)
            {
                DeleteDirectoryRecursive(file.FullName);
            }
            else
            {
                _sftpClient.DeleteFile(file.FullName);
            }
        }

        _sftpClient.DeleteDirectory(path);
    }

    public async Task CreateSymbolicLinkAsync(string targetPath,
        string linkPath,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is not { IsConnected: true })
                    throw new InvalidOperationException("Not connected");

                _sftpClient.SymbolicLink(targetPath, linkPath);
                return true;
            },
            cancellationToken);
    }

    public async Task<string?> GetSymbolicLinkTargetAsync(string linkPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
            throw new ArgumentException("Link path is required.", nameof(linkPath));

        await EnsureConnectedAsync(cancellationToken);
        if (_activeProfile is null)
            throw new InvalidOperationException("Not connected");

        var profileSnapshot = CloneProfile(_activeProfile);
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var sshClient = CreateSshClient(profileSnapshot);
                try
                {
                    var escapedPath = EscapePosixShellArgument(linkPath);
                    var command = sshClient.RunCommand($"readlink -- {escapedPath}");
                    var result = (command.Result ?? string.Empty).Trim();

                    if (command.ExitStatus != 0 || string.IsNullOrWhiteSpace(result))
                    {
                        command = sshClient.RunCommand($"readlink {escapedPath}");
                        result = (command.Result ?? string.Empty).Trim();
                    }

                    if (command.ExitStatus != 0)
                    {
                        var errorText = string.IsNullOrWhiteSpace(command.Error) ? "readlink failed" : command.Error.Trim();
                        throw new InvalidOperationException(
                            $"Unable to read symbolic link target for '{linkPath}': {errorText}");
                    }

                    return string.IsNullOrWhiteSpace(result) ? null : result;
                }
                finally
                {
                    if (sshClient.IsConnected)
                        sshClient.Disconnect();
                }
            },
            cancellationToken);
    }

    public async Task CreateDirectoryAsync(string path)
    {
        await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is not { IsConnected: true })
                    throw new InvalidOperationException("Not connected");
                _sftpClient.CreateDirectory(path);
                return true;
            });
    }

    public async Task CreateFileAsync(string path)
    {
        await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is not { IsConnected: true })
                    throw new InvalidOperationException("Not connected");

                _sftpClient.Create(path).Dispose();
                return true;
            });
    }

    public async Task CopyFileAsync(string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sftpClient is not { IsConnected: true }) return;

        await Task.Run(() =>
        {
            using var sourceStream = _sftpClient.OpenRead(sourcePath);
            using var destStream = _sftpClient.Create(destPath);
            var streams = (Source: (Stream)sourceStream, Dest: (Stream)destStream);

            using var ctr =
                cancellationToken.Register(
                    static state =>
                    {
                        var pair = ((ValueTuple<Stream, Stream>)state!);
                        pair.Item1.Dispose();
                        pair.Item2.Dispose();
                    },
                    streams);

            try
            {
                sourceStream.CopyTo(destStream);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
        }, cancellationToken);
    }

    public async Task CopyDirectoryAsync(string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sftpClient is not { IsConnected: true }) return;

        await Task.Run(() => { CopyDirectoryRecursive(sourcePath, destPath, cancellationToken); }, cancellationToken);
    }

    public async Task PromoteUploadedFileAtomicallyAsync(string temporaryRemotePath,
        string destinationRemotePath,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sftpClient is not { IsConnected: true })
            throw new InvalidOperationException("Not connected");

        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tempPath = NormalizeRemotePathForTransfer(temporaryRemotePath);
                var destinationPath = NormalizeRemotePathForTransfer(destinationRemotePath);
                if (string.Equals(tempPath, destinationPath, StringComparison.Ordinal))
                    return;

                if (!_sftpClient.Exists(tempPath))
                    throw new InvalidOperationException($"Temporary upload file not found: {tempPath}");

                var destinationExists = _sftpClient.Exists(destinationPath);
                if (!destinationExists)
                {
                    _sftpClient.RenameFile(tempPath, destinationPath);
                    return;
                }

                if (TryPosixAtomicRename(tempPath, destinationPath))
                    return;

                var backupPath = BuildSiblingTempPath(destinationPath, "replace-backup");
                var backupCreated = false;
                try
                {
                    _sftpClient.RenameFile(destinationPath, backupPath);
                    backupCreated = true;
                    _sftpClient.RenameFile(tempPath, destinationPath);
                    TryDeleteRemoteFile(backupPath);
                }
                catch
                {
                    TryRestoreBackup(destinationPath, backupPath, backupCreated);
                    throw;
                }
            },
            cancellationToken);
    }

    public async Task<RemotePosixAttributes> GetPosixAttributesAsync(string path,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is not { IsConnected: true })
                    throw new InvalidOperationException("Not connected");

                var attributes = _sftpClient.GetAttributes(path);
                var mode = BuildPermissionMode(attributes);
                var (ownerName, groupName) = ResolveIdentityNames(attributes.UserId, attributes.GroupId);
                return new RemotePosixAttributes
                {
                    IsDirectory = attributes.IsDirectory,
                    Size = attributes.Size,
                    LastWriteTime = attributes.LastWriteTime,
                    SymbolicPermissions = BuildSymbolicPermissions(attributes),
                    OctalPermissions = FormatPermissionMode(mode),
                    PermissionMode = mode,
                    UserId = attributes.UserId,
                    OwnerName = ownerName,
                    GroupId = attributes.GroupId,
                    GroupName = groupName
                };
            },
            cancellationToken);
    }

    public async Task SetPosixAttributesAsync(string path,
        short? permissionMode = null,
        int? userId = null,
        int? groupId = null,
        CancellationToken cancellationToken = default)
    {
        if (!permissionMode.HasValue && !userId.HasValue && !groupId.HasValue)
            return;

        await ExecuteWithReconnectRetryAsync(
            () =>
            {
                if (_sftpClient is not { IsConnected: true })
                    throw new InvalidOperationException("Not connected");

                var attributes = _sftpClient.GetAttributes(path);
                if (permissionMode.HasValue)
                    attributes.SetPermissions((short)(permissionMode.Value & 0x0FFF));
                if (userId.HasValue)
                    attributes.UserId = userId.Value;
                if (groupId.HasValue)
                    attributes.GroupId = groupId.Value;

                _sftpClient.SetAttributes(path, attributes);
                return true;
            },
            cancellationToken);
    }

    public async Task<bool> PathExistsAsync(string path)
    {
        try
        {
            return await ExecuteWithReconnectRetryAsync(
                () =>
                {
                    if (_sftpClient is not { IsConnected: true })
                        throw new InvalidOperationException("Not connected");
                    return _sftpClient.Exists(path);
                });
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<bool> IsDirectoryAsync(string path)
    {
        try
        {
            return await ExecuteWithReconnectRetryAsync(
                () =>
                {
                    if (_sftpClient is not { IsConnected: true })
                        throw new InvalidOperationException("Not connected");
                    return _sftpClient.GetAttributes(path).IsDirectory;
                });
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<(bool IsDirectory, long Size, DateTime LastWriteTime)> GetEntryInfoAsync(string path)
    {
        try
        {
            return await ExecuteWithReconnectRetryAsync(
                () =>
                {
                    if (_sftpClient is not { IsConnected: true })
                        throw new InvalidOperationException("Not connected");

                    var attrs = _sftpClient.GetAttributes(path);
                    return (attrs.IsDirectory, attrs.Size, attrs.LastWriteTime);
                });
        }
        catch (InvalidOperationException)
        {
            return (false, 0, DateTime.MinValue);
        }
    }

    private void CopyDirectoryRecursive(string sourcePath,
        string destPath,
        CancellationToken cancellationToken)
    {
        if (_sftpClient is null) return;
        cancellationToken.ThrowIfCancellationRequested();

        if (!_sftpClient.Exists(destPath))
        {
            _sftpClient.CreateDirectory(destPath);
        }

        foreach (var entry in _sftpClient.ListDirectory(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Name is "." or "..") continue;

            var targetPath = destPath.EndsWith('/') ? destPath + entry.Name : $"{destPath}/{entry.Name}";

            if (entry.IsDirectory)
            {
                CopyDirectoryRecursive(entry.FullName, targetPath, cancellationToken);
            }
            else
            {
                using var sourceStream = _sftpClient.OpenRead(entry.FullName);
                using var destStream = _sftpClient.Create(targetPath);
                sourceStream.CopyTo(destStream);
            }
        }
    }

    private void TryDeleteRemoteFile(string remotePath)
    {
        try
        {
            if (_sftpClient is not { IsConnected: true })
                return;

            if (_sftpClient.Exists(remotePath))
                _sftpClient.DeleteFile(remotePath);
        }
        catch
        {
            // Best-effort cleanup for canceled uploads; ignore secondary failures.
        }
    }

    private bool TryPosixAtomicRename(string sourcePath,
        string destinationPath)
    {
        try
        {
            var posixRenameMethod =
                typeof(SftpClient).GetMethod(
                    "RenameFile",
                    [typeof(string), typeof(string), typeof(bool)]);
            if (posixRenameMethod is null)
                return false;

            posixRenameMethod.Invoke(_sftpClient, [sourcePath, destinationPath, true]);
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            LoggingService.Warn(
                $"POSIX atomic rename failed for '{destinationPath}', falling back to guarded replacement. {ex.InnerException.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.Warn(
                $"POSIX atomic rename unavailable for '{destinationPath}', falling back to guarded replacement. {ex.Message}");
            return false;
        }
    }

    private void TryRestoreBackup(string destinationPath,
        string backupPath,
        bool backupCreated)
    {
        if (!backupCreated || _sftpClient is not { IsConnected: true })
            return;

        try
        {
            if (_sftpClient.Exists(destinationPath))
                return;

            if (_sftpClient.Exists(backupPath))
                _sftpClient.RenameFile(backupPath, destinationPath);
        }
        catch
        {
            // Best-effort rollback only.
        }
    }

    private static string NormalizeRemotePathForTransfer(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        if (normalized.Length > 1)
            normalized = normalized.TrimEnd('/');
        return normalized;
    }

    private static short BuildPermissionMode(SftpFileAttributes attributes)
    {
        var mode = 0;
        if (attributes.IsUIDBitSet)
            mode |= 0x0800;
        if (attributes.IsGroupIDBitSet)
            mode |= 0x0400;
        if (attributes.IsStickyBitSet)
            mode |= 0x0200;

        if (attributes.OwnerCanRead)
            mode |= 0x0100;
        if (attributes.OwnerCanWrite)
            mode |= 0x0080;
        if (attributes.OwnerCanExecute)
            mode |= 0x0040;

        if (attributes.GroupCanRead)
            mode |= 0x0020;
        if (attributes.GroupCanWrite)
            mode |= 0x0010;
        if (attributes.GroupCanExecute)
            mode |= 0x0008;

        if (attributes.OthersCanRead)
            mode |= 0x0004;
        if (attributes.OthersCanWrite)
            mode |= 0x0002;
        if (attributes.OthersCanExecute)
            mode |= 0x0001;

        return (short)mode;
    }

    private static string FormatPermissionMode(short mode)
    {
        return Convert.ToString(mode & 0x0FFF, 8).PadLeft(4, '0');
    }

    private static string BuildSymbolicPermissions(SftpFileAttributes attributes)
    {
        var mode = BuildPermissionMode(attributes);
        var chars = new[]
        {
            attributes.IsDirectory ? 'd' : attributes.IsSymbolicLink ? 'l' : '-',
            (mode & 0x0100) != 0 ? 'r' : '-',
            (mode & 0x0080) != 0 ? 'w' : '-',
            (mode & 0x0040) != 0 ? 'x' : '-',
            (mode & 0x0020) != 0 ? 'r' : '-',
            (mode & 0x0010) != 0 ? 'w' : '-',
            (mode & 0x0008) != 0 ? 'x' : '-',
            (mode & 0x0004) != 0 ? 'r' : '-',
            (mode & 0x0002) != 0 ? 'w' : '-',
            (mode & 0x0001) != 0 ? 'x' : '-'
        };

        if ((mode & 0x0800) != 0)
            chars[3] = chars[3] == 'x' ? 's' : 'S';
        if ((mode & 0x0400) != 0)
            chars[6] = chars[6] == 'x' ? 's' : 'S';
        if ((mode & 0x0200) != 0)
            chars[9] = chars[9] == 'x' ? 't' : 'T';

        return new string(chars);
    }

    private (string OwnerName, string GroupName) ResolveIdentityNames(int userId,
        int groupId)
    {
        if (_sftpClient is not { IsConnected: true })
            return (string.Empty, string.Empty);

        var ownerName = ResolveIdentityName("/etc/passwd", userId, idFieldIndex: 2, nameFieldIndex: 0);
        var groupName = ResolveIdentityName("/etc/group", groupId, idFieldIndex: 2, nameFieldIndex: 0);
        return (ownerName, groupName);
    }

    private string ResolveIdentityName(string remotePath,
        int id,
        int idFieldIndex,
        int nameFieldIndex)
    {
        try
        {
            if (_sftpClient is not { IsConnected: true })
                return string.Empty;

            if (!_sftpClient.Exists(remotePath))
                return string.Empty;

            using var stream = _sftpClient.OpenRead(remotePath);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                    continue;

                var fields = line.Split(':');
                var requiredLength = Math.Max(idFieldIndex, nameFieldIndex) + 1;
                if (fields.Length < requiredLength)
                    continue;

                if (!int.TryParse(fields[idFieldIndex], out var parsedId))
                    continue;

                if (parsedId == id)
                    return fields[nameFieldIndex];
            }
        }
        catch
        {
            // Name resolution is optional and should never block editing by numeric IDs.
        }

        return string.Empty;
    }

    private string BuildSiblingTempPath(string remotePath,
        string purpose)
    {
        var normalized = NormalizeRemotePathForTransfer(remotePath);
        var separatorIndex = normalized.LastIndexOf('/');
        var parentPath = separatorIndex <= 0 ? "/" : normalized[..separatorIndex];
        var fileName = separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;

        while (true)
        {
            var candidateName = $".nobssftp-{purpose}-{fileName}.{Guid.NewGuid():N}.tmp";
            var candidatePath = parentPath == "/" ? $"/{candidateName}" : $"{parentPath}/{candidateName}";
            if (!_sftpClient!.Exists(candidatePath))
                return candidatePath;
        }
    }

    private static void TrySetLocalLastWriteTime(string localPath,
        DateTime lastWriteTime)
    {
        try
        {
            if (lastWriteTime != DateTime.MinValue)
                File.SetLastWriteTime(localPath, lastWriteTime);
        }
        catch
        {
            // Last-write time preservation is best effort.
        }
    }

    private static string EscapePosixShellArgument(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    public SshClient CreateSshClient(ServerProfile profile)
    {
        var policy = ConnectionResiliencePolicy.FromProfile(profile);
        var connectionInfo = BuildConnectionInfo(profile, policy.ConnectionTimeoutSeconds);

        var client =
            new SshClient(connectionInfo)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(policy.KeepAliveIntervalSeconds)
            };
        client.HostKeyReceived +=
            (_, args) =>
            {
                args.CanTrust = _hostKeyTrustService.IsTrustedHostKey(profile, args);
            };
        client.Connect();
        return client;
    }

    private static string ResolvePrivateKeyPath(string configuredPath)
    {
        var normalized = NormalizePrivateKeyPath(configuredPath);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Private key path is required for key authentication.");

        if (!File.Exists(normalized))
            throw new InvalidOperationException($"Private key file was not found: {normalized}");

        return normalized;
    }

    private static string NormalizePrivateKeyPath(string rawPath)
    {
        var path = rawPath.Trim();

        if (path.Length >= 2 &&
            ((path.StartsWith('"') && path.EndsWith('"')) ||
             (path.StartsWith('\'') && path.EndsWith('\''))))
        {
            path = path[1..^1];
        }

        path = Environment.ExpandEnvironmentVariables(path);

        if (path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                path = home;
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal) ||
                 path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                var relative = path[2..]
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                path = Path.Combine(home, relative);
            }
        }

        if (!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path))
            path = Path.GetFullPath(path);

        return path;
    }
}
