using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoBSSftp.Models;
using NoBSSftp.Services;
using Renci.SshNet;

namespace NoBSSftp.ViewModels;

public partial class TerminalViewModel(ISftpService sftpService) : ViewModelBase
{
    private SshClient? _sshClient;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _readCts;
    private TerminalEmulator? _emulator;

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _historyScratch = string.Empty;

    public async Task ConnectAsync(ServerProfile profile)
    {
        await DisconnectAsync();

        await Task.Run(() =>
        {
            _sshClient = sftpService.CreateSshClient(profile);
            _shellStream = _sshClient.CreateShellStream("xterm", 80, 24, 800, 600, 4096);
            _emulator = new TerminalEmulator(24, 80);
        });

        Dispatcher.UIThread.Post(() => IsConnected = true);
        StartReadLoop();
    }

    public async Task DisconnectAsync()
    {
        await Task.Run(() =>
        {
            _readCts?.Cancel();
            _readCts?.Dispose();
            _readCts = null;

            _shellStream?.Dispose();
            _shellStream = null;

            _sshClient?.Disconnect();
            _sshClient?.Dispose();
            _sshClient = null;
            _emulator = null;
        });
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            OutputText = string.Empty;
        });
    }

    public void SendControlC()
    {
        SendRaw("\x03");
    }

    public void SendControlL()
    {
        SendRaw("\x0c");
    }

    public void SendSequence(string sequence)
    {
        SendRaw(sequence);
    }

    private void SendRaw(string data)
    {
        if (!IsConnected || _shellStream is null)
            return;

        try
        {
            _shellStream.Write(data);
            _shellStream.Flush();
        }
        catch (Exception ex)
        {
            AppendOutput($"\r\n[terminal error] {ex.Message}\r\n");
        }
    }

    [RelayCommand]
    private void SubmitInput()
    {
        if (!IsConnected || _shellStream is null)
            return;

        var text = InputText;
        try
        {
            if (text.Length > 0)
                AddHistory(text);

            if (text.Length == 0)
                _shellStream.Write("\n");
            else
                _shellStream.WriteLine(text);

            _shellStream.Flush();
            InputText = string.Empty;
        }
        catch (Exception ex)
        {
            AppendOutput($"\r\n[terminal error] {ex.Message}\r\n");
        }
    }

    public void HistoryUp()
    {
        if (_history.Count == 0)
            return;

        if (_historyIndex < 0)
        {
            _historyScratch = InputText;
            _historyIndex = _history.Count;
        }

        _historyIndex = Math.Max(0, _historyIndex - 1);
        InputText = _history[_historyIndex];
    }

    public void HistoryDown()
    {
        if (_history.Count == 0 || _historyIndex < 0)
            return;

        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        if (_historyIndex == _history.Count)
        {
            InputText = _historyScratch;
            _historyIndex = -1;
            return;
        }

        InputText = _history[_historyIndex];
    }

    private void AddHistory(string text)
    {
        if (_history.Count == 0 || !_history[^1].Equals(text, StringComparison.Ordinal))
            _history.Add(text);

        _historyIndex = -1;
        _historyScratch = string.Empty;
    }

    private void StartReadLoop()
    {
        if (_shellStream is null)
            return;

        _readCts = new CancellationTokenSource();
        var token = _readCts.Token;

        Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested && _shellStream is { CanRead: true })
                {
                    var read = _shellStream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        var text = Encoding.UTF8.GetString(buffer, 0, read);
                        AppendOutput(text);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendOutput($"\r\n[terminal disconnected] {ex.Message}\r\n");
            }
        }, token);
    }

    private void AppendOutput(string text)
    {
        if (string.IsNullOrEmpty(text) || _emulator is null)
            return;

        _emulator.Write(text);
        var fullText = _emulator.GetFullText();

        Dispatcher.UIThread.Post(() =>
        {
            OutputText = fullText;
        });
    }
}
