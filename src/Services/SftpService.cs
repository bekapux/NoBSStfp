using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NoBSSftp.Models;
using Renci.SshNet;
using System.Threading;

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
        CancellationToken cancellationToken = default);

    Task DownloadFileAsync(string remotePath,
        string localPath,
        IProgress<double> progress,
        CancellationToken cancellationToken = default);

    Task RenameFileAsync(string oldPath,
        string newPath);

    Task DeleteFileAsync(string path);
    Task DeleteDirectoryAsync(string path);
    Task CreateDirectoryAsync(string path);
    Task CreateFileAsync(string path);

    Task CopyFileAsync(string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default);

    Task CopyDirectoryAsync(string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default);

    Task<bool> PathExistsAsync(string path);
    Task<bool> IsDirectoryAsync(string path);
    Task<(bool IsDirectory, long Size, DateTime LastWriteTime)> GetEntryInfoAsync(string path);
    SshClient CreateSshClient(ServerProfile profile);
}

public class SftpService : ISftpService
{
    private SftpClient? _sftpClient;

    public bool IsConnected => _sftpClient?.IsConnected ?? false;

    public async Task ConnectAsync(ServerProfile profile)
    {
        await Task.Run(() =>
        {
            if (_sftpClient is { IsConnected: true })
                _sftpClient.Disconnect();

            ConnectionInfo connectionInfo;
            if (profile.UsePrivateKey)
            {
                if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                    throw new InvalidOperationException("Private key path is required for key authentication.");

                var keyPath = ResolvePrivateKeyPath(profile.PrivateKeyPath);
                var keyFile =
                    string.IsNullOrEmpty(profile.PrivateKeyPassphrase)
                        ? new PrivateKeyFile(keyPath)
                        : new PrivateKeyFile(keyPath, profile.PrivateKeyPassphrase);
                var keyAuth = new PrivateKeyAuthenticationMethod(profile.Username, keyFile);
                connectionInfo = new ConnectionInfo(profile.Host, profile.Port, profile.Username, keyAuth);
            }
            else
            {
                var passwordAuth = new PasswordAuthenticationMethod(profile.Username, profile.Password);
                connectionInfo = new ConnectionInfo(profile.Host, profile.Port, profile.Username, passwordAuth);
            }

            _sftpClient = new SftpClient(connectionInfo);
            _sftpClient.Connect();
        });
    }

    public async Task DisconnectAsync()
    {
        await Task.Run(() =>
        {
            _sftpClient?.Disconnect();
            _sftpClient?.Dispose();
            _sftpClient = null;
        });
    }

    public async Task<string> GetCurrentDirectoryAsync()
    {
        if (_sftpClient is not { IsConnected: true })
            throw new InvalidOperationException("Not connected");

        return await Task.Run(() => _sftpClient.WorkingDirectory);
    }

    public async Task<List<FileEntry>> ListDirectoryAsync(string path)
    {
        if (_sftpClient is null || !_sftpClient.IsConnected)
            throw new InvalidOperationException("Not connected");

        return await Task.Run(() =>
        {
            var files = _sftpClient.ListDirectory(path);
            return files.Where(f => f.Name != "." && f.Name != "..")
                .Select(f =>
                    new FileEntry
                    {
                        Name = f.Name,
                        Size = f.Length,
                        IsDirectory = f.IsDirectory,
                        Permissions = f.Attributes?.ToString() ?? string.Empty, // Simplified
                        LastWriteTime = f.LastWriteTime
                    })
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name)
                .ToList();
        });
    }

    public async Task UploadFileAsync(string localPath,
        string remotePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (_sftpClient is not { IsConnected: true })
            throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            using var fileStream = File.OpenRead(localPath);
            var length = fileStream.Length;

            using var ctr =
                cancellationToken.Register(
                    static state => ((Stream)state!).Dispose(),
                    fileStream);

            try
            {
                _sftpClient.UploadFile(fileStream, remotePath, (uploaded) =>
                {
                    progress?.Report((double)uploaded / length * 100);
                });
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
        }, cancellationToken);
    }

    public async Task DownloadFileAsync(string remotePath,
        string localPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (_sftpClient is null || !_sftpClient.IsConnected)
            throw new InvalidOperationException("Not connected");

        await Task.Run(() =>
        {
            using var fileStream = File.Create(localPath);
            var attributes = _sftpClient.GetAttributes(remotePath);
            var fileSize = attributes.Size;

            using var ctr =
                cancellationToken.Register(
                    static state => ((Stream)state!).Dispose(),
                    fileStream);

            try
            {
                _sftpClient.DownloadFile(remotePath, fileStream,
                    (downloaded) => { progress?.Report((double)downloaded / fileSize * 100); });
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
        }, cancellationToken);
    }

    public async Task RenameFileAsync(string oldPath,
        string newPath)
    {
        if (_sftpClient is null || !_sftpClient.IsConnected) return;
        await Task.Run(() => _sftpClient.RenameFile(oldPath, newPath));
    }

    public async Task DeleteFileAsync(string path)
    {
        if (_sftpClient is not { IsConnected: true }) return;
        await Task.Run(() => _sftpClient.DeleteFile(path));
    }

    public async Task DeleteDirectoryAsync(string path)
    {
        if (_sftpClient is null || !_sftpClient.IsConnected) return;
        await Task.Run(() => DeleteDirectoryRecursive(path));
    }

    private void DeleteDirectoryRecursive(string path)
    {
        if (_sftpClient is null) return;

        foreach (var file in _sftpClient.ListDirectory(path))
        {
            if (file.Name is "." or "..") continue;

            if (file.IsDirectory)
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

    public async Task CreateDirectoryAsync(string path)
    {
        if (_sftpClient is not { IsConnected: true }) return;
        await Task.Run(() => _sftpClient.CreateDirectory(path));
    }

    public async Task CreateFileAsync(string path)
    {
        if (_sftpClient is not { IsConnected: true }) return;
        await Task.Run(() => { _sftpClient.Create(path).Dispose(); });
    }

    public async Task CopyFileAsync(string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default)
    {
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
        if (_sftpClient is not { IsConnected: true }) return;

        await Task.Run(() => { CopyDirectoryRecursive(sourcePath, destPath, cancellationToken); }, cancellationToken);
    }

    public async Task<bool> PathExistsAsync(string path)
    {
        if (_sftpClient is not { IsConnected: true }) return false;
        return await Task.Run(() => _sftpClient.Exists(path));
    }

    public async Task<bool> IsDirectoryAsync(string path)
    {
        if (_sftpClient is not { IsConnected: true }) return false;
        return await Task.Run(() => _sftpClient.GetAttributes(path).IsDirectory);
    }

    public async Task<(bool IsDirectory, long Size, DateTime LastWriteTime)> GetEntryInfoAsync(string path)
    {
        if (_sftpClient is not { IsConnected: true })
            return (false, 0, DateTime.MinValue);

        return await Task.Run(() =>
        {
            var attrs = _sftpClient.GetAttributes(path);
            return (attrs.IsDirectory, attrs.Size, attrs.LastWriteTime);
        });
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

    public SshClient CreateSshClient(ServerProfile profile)
    {
        ConnectionInfo connectionInfo;
        if (profile.UsePrivateKey)
        {
            if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                throw new InvalidOperationException("Private key path is required for key authentication.");

            var keyPath = ResolvePrivateKeyPath(profile.PrivateKeyPath);
            var keyFile =
                string.IsNullOrEmpty(profile.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(keyPath)
                    : new PrivateKeyFile(keyPath, profile.PrivateKeyPassphrase);
            var keyAuth = new PrivateKeyAuthenticationMethod(profile.Username, keyFile);
            connectionInfo = new ConnectionInfo(profile.Host, profile.Port, profile.Username, keyAuth);
        }
        else
        {
            var passwordAuth = new PasswordAuthenticationMethod(profile.Username, profile.Password);
            connectionInfo = new ConnectionInfo(profile.Host, profile.Port, profile.Username, passwordAuth);
        }

        var client = new SshClient(connectionInfo);
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
