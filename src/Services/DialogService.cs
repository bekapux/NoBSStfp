using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using NoBSSftp.Models;

namespace NoBSSftp.Services;

public class DialogService : IDialogService
{
    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }

    public async Task<string?> PickFolderAsync()
    {
        var window = GetMainWindow();
        if (window is null) return null;

        var result =
            await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Download Destination",
                AllowMultiple = false
            });

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    public async Task<string?> PickFileAsync(string title)
    {
        var window = GetMainWindow();
        if (window is null) return null;

        var result =
            await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    public async Task<string> PromptAsync(string title,
        string message,
        string defaultValue = "")
    {
        var window =
            new Window
            {
                Title = title,
                Width = 300,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(10), Spacing = 10 };
        var label = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var textBox = new TextBox { Text = defaultValue };
        var btnPanel =
            new StackPanel
                { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var okBtn = new Button { Content = "OK", IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(label);
        stack.Children.Add(textBox);
        stack.Children.Add(btnPanel);
        window.Content = stack;

        var result = string.Empty;
        var confirmed = false;

        okBtn.Click +=
            (_,
                _) =>
            {
                confirmed = true;
                result = textBox.Text ?? "";
                window.Close();
            };
        cancelBtn.Click +=
        (_,
            _) => { window.Close(); };

        window.Opened +=
            (_,
                _) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return confirmed ? result : string.Empty;
    }

    public async Task<bool> ConfirmAsync(string title,
        string message)
    {
        var window =
            new Window
            {
                Title = title,
                Width = 300,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(10), Spacing = 10 };
        var label = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var btnPanel =
            new StackPanel
                { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var okBtn = new Button { Content = "Yes", IsDefault = true };
        var cancelBtn = new Button { Content = "No", IsCancel = true };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(label);
        stack.Children.Add(btnPanel);
        window.Content = stack;

        bool confirmed = false;

        okBtn.Click +=
            (_,
                _) =>
            {
                confirmed = true;
                window.Close();
            };
        cancelBtn.Click +=
        (_,
            _) => { window.Close(); };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return confirmed;
    }

    public async Task<ConflictChoice> ConfirmConflictAsync(string title,
        string message,
        string sourceDetails,
        string destinationDetails)
    {
        var window =
            new Window
            {
                Title = title,
                Width = 420,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(10), Spacing = 10 };
        var label = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var sourceLabel =
            new TextBlock { Text = $"Source: {sourceDetails}", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var destLabel =
            new TextBlock
                { Text = $"Destination: {destinationDetails}", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var btnPanel =
            new StackPanel
                { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var overwriteBtn = new Button { Content = "Overwrite" };
        var duplicateBtn = new Button { Content = "Duplicate" };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true };

        btnPanel.Children.Add(overwriteBtn);
        btnPanel.Children.Add(duplicateBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(label);
        stack.Children.Add(sourceLabel);
        stack.Children.Add(destLabel);
        stack.Children.Add(btnPanel);
        window.Content = stack;

        var choice = ConflictChoice.Cancel;

        overwriteBtn.Click +=
            (_,
                _) =>
            {
                choice = ConflictChoice.Overwrite;
                window.Close();
            };
        duplicateBtn.Click +=
            (_,
                _) =>
            {
                choice = ConflictChoice.Duplicate;
                window.Close();
            };
        cancelBtn.Click +=
        (_,
            _) => { window.Close(); };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return choice;
    }

    public async Task<ServerProfile?> ShowServerFormAsync(ServerProfile? existing = null)
    {
        var window =
            new Window
            {
                Title = existing is null ? "Add Server" : "Edit Server",
                Width = 420,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var scroll = new ScrollViewer { Content = stack };

        var aliasBox = new TextBox { Watermark = "Server Alias (Name)", Text = existing?.Name ?? "" };
        var hostBox = new TextBox { Watermark = "Server IP Address / Host", Text = existing?.Host ?? "" };
        var portBox = new TextBox { Watermark = "Port", Text = existing?.Port.ToString() ?? "22" };
        var userBox = new TextBox { Watermark = "Username", Text = existing?.Username ?? "root" };
        var useKeyBox = new CheckBox { Content = "Use private key", IsChecked = existing?.UsePrivateKey ?? false };
        var keyPathBox = new TextBox { Watermark = "Private key path", Text = existing?.PrivateKeyPath ?? "" };
        var keyBrowseBtn = new Button { Content = "Browse..." };
        var keyPassBox =
            new TextBox
            {
                PasswordChar = '*', Watermark = "Key passphrase (optional)", Text = existing?.PrivateKeyPassphrase ?? ""
            };

        stack.Children.Add(new TextBlock { Text = "Server Alias:" });
        stack.Children.Add(aliasBox);
        stack.Children.Add(new TextBlock { Text = "Host / IP:" });
        stack.Children.Add(hostBox);
        stack.Children.Add(new TextBlock { Text = "Port:" });
        stack.Children.Add(portBox);
        stack.Children.Add(new TextBlock { Text = "Username:" });
        stack.Children.Add(userBox);
        stack.Children.Add(useKeyBox);
        stack.Children.Add(new TextBlock { Text = "Private Key Path:" });
        var keyPathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keyPathRow.Children.Add(keyPathBox);
        keyPathRow.Children.Add(keyBrowseBtn);
        stack.Children.Add(keyPathRow);
        stack.Children.Add(new TextBlock { Text = "Key Passphrase:" });
        stack.Children.Add(keyPassBox);

        var btnPanel =
            new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
                Margin = new Thickness(0, 20, 0, 0)
            };
        var saveBtn = new Button { Content = "Save", IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true };

        btnPanel.Children.Add(saveBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);
        window.Content = scroll;

        void UpdateVisibility()
        {
            var useKey = useKeyBox.IsChecked ?? false;
            keyPathBox.IsVisible = useKey;
            keyBrowseBtn.IsVisible = useKey;
            keyPassBox.IsVisible = useKey;
        }

        useKeyBox.IsCheckedChanged +=
        (_,
            _) => UpdateVisibility();
        UpdateVisibility();

        ServerProfile? result = null;

        saveBtn.Click +=
            (_,
                _) =>
            {
                if (!int.TryParse(portBox.Text, out var port)) return;

                result =
                    new ServerProfile
                    {
                        Id = existing?.Id ?? System.Guid.NewGuid().ToString(),
                        Name = aliasBox.Text ?? "New Server",
                        Host = hostBox.Text ?? "localhost",
                        Port = port,
                        Username = userBox.Text ?? "root",
                        UsePrivateKey = useKeyBox.IsChecked ?? false,
                        PrivateKeyPath = keyPathBox.Text ?? "",
                        PrivateKeyPassphrase = keyPassBox.Text ?? ""
                    };
                window.Close();
            };
        cancelBtn.Click +=
        (_,
            _) => { window.Close(); };

        keyBrowseBtn.Click +=
            async (_,
                _) =>
            {
                var selected = await PickFileAsync("Select Private Key");
                if (!string.IsNullOrEmpty(selected))
                    keyPathBox.Text = selected;
            };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return result;
    }

    public async Task<ConnectInfo?> ShowConnectDialogAsync(string title,
        string host,
        ServerProfile? defaults = null)
    {
        var window =
            new Window
            {
                Title = title,
                Width = 380,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var scroll = new ScrollViewer { Content = stack };

        var userBox = new TextBox { Text = defaults?.Username ?? "root" };
        var useKeyBox = new CheckBox { Content = "Use private key", IsChecked = defaults?.UsePrivateKey ?? false };
        var passBox = new TextBox { PasswordChar = '*', Watermark = "Password" };
        var keyPathBox = new TextBox { Watermark = "Private key path", Text = defaults?.PrivateKeyPath ?? "" };
        var keyBrowseBtn = new Button { Content = "Browse..." };
        var keyPassBox =
            new TextBox
            {
                PasswordChar = '*', Watermark = "Key passphrase (optional)", Text = defaults?.PrivateKeyPassphrase ?? ""
            };

        stack.Children.Add(new TextBlock { Text = $"Connect to {host}" });
        stack.Children.Add(new TextBlock { Text = "Username:" });
        stack.Children.Add(userBox);
        stack.Children.Add(useKeyBox);
        stack.Children.Add(new TextBlock { Text = "Password:" });
        stack.Children.Add(passBox);
        stack.Children.Add(new TextBlock { Text = "Private Key Path:" });
        var keyPathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keyPathRow.Children.Add(keyPathBox);
        keyPathRow.Children.Add(keyBrowseBtn);
        stack.Children.Add(keyPathRow);
        stack.Children.Add(new TextBlock { Text = "Key Passphrase:" });
        stack.Children.Add(keyPassBox);

        var btnPanel =
            new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
                Margin = new Thickness(0, 20, 0, 0)
            };
        var connectBtn = new Button { Content = "Connect", IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true };

        btnPanel.Children.Add(connectBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);
        window.Content = scroll;

        void UpdateVisibility()
        {
            var useKey = useKeyBox.IsChecked ?? false;
            passBox.IsVisible = !useKey;
            keyPathBox.IsVisible = useKey;
            keyBrowseBtn.IsVisible = useKey;
            keyPassBox.IsVisible = useKey;
        }

        useKeyBox.IsCheckedChanged +=
        (_,
            _) => UpdateVisibility();
        UpdateVisibility();

        ConnectInfo? result = null;

        connectBtn.Click +=
            (_,
                _) =>
            {
                result =
                    new ConnectInfo
                    {
                        Username = userBox.Text ?? "",
                        Password = passBox.Text ?? "",
                        UsePrivateKey = useKeyBox.IsChecked ?? false,
                        PrivateKeyPath = keyPathBox.Text ?? "",
                        PrivateKeyPassphrase = keyPassBox.Text ?? ""
                    };
                window.Close();
            };

        cancelBtn.Click +=
        (_,
            _) => { window.Close(); };

        keyBrowseBtn.Click +=
            async (_,
                _) =>
            {
                var selected = await PickFileAsync("Select Private Key");
                if (!string.IsNullOrEmpty(selected))
                    keyPathBox.Text = selected;
            };

        window.Opened +=
            (_,
                _) =>
            {
                if (useKeyBox.IsChecked ?? false)
                {
                    keyPathBox.Focus();
                    keyPathBox.SelectAll();
                }
                else
                {
                    passBox.Focus();
                    passBox.SelectAll();
                }
            };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return result;
    }
}