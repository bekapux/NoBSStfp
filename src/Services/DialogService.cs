using System;
using System.Collections.Generic;
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
                SizeToContent = SizeToContent.Height,
                CanResize = false,
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

    public async Task<bool> ConfirmHostKeyAsync(string title,
        string message,
        string details,
        bool isWarning)
    {
        var window =
            new Window
            {
                Title = title,
                Width = 620,
                MinWidth = 620,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(12), Spacing = 10 };
        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        };
        var detailsBorder =
            new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = isWarning ? Avalonia.Media.Brushes.IndianRed : Avalonia.Media.Brushes.Gray,
                Background = Avalonia.Media.Brushes.Black,
                Padding = new Thickness(8),
                MinHeight = 120
            };
        var detailsScroll =
            new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                MaxHeight = 280,
                Content =
                    new TextBlock
                    {
                        Text = details,
                        TextWrapping = Avalonia.Media.TextWrapping.WrapWithOverflow,
                        FontFamily = "Menlo,Consolas,Courier New,monospace",
                        Foreground = Avalonia.Media.Brushes.White
                    }
            };
        detailsBorder.Child = detailsScroll;

        var btnPanel =
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };
        var trustBtn = new Button { Content = "Trust and Continue", IsDefault = true };
        var rejectBtn = new Button { Content = "Reject", IsCancel = true };

        btnPanel.Children.Add(trustBtn);
        btnPanel.Children.Add(rejectBtn);
        stack.Children.Add(messageBlock);
        stack.Children.Add(detailsBorder);
        stack.Children.Add(btnPanel);
        window.Content = stack;

        var trusted = false;
        trustBtn.Click += (_, _) =>
        {
            trusted = true;
            window.Close();
        };
        rejectBtn.Click += (_, _) => { window.Close(); };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return trusted;
    }

    public async Task<ConflictChoice> ConfirmConflictAsync(string title,
        string message,
        string sourceDetails,
        string destinationDetails)
    {
        var result = await ConfirmConflictWithScopeAsync(
            title,
            message,
            sourceDetails,
            destinationDetails,
            allowApplyToAll: false);
        return result.Choice;
    }

    public async Task<ConflictDialogResult> ConfirmConflictWithScopeAsync(string title,
        string message,
        string sourceDetails,
        string destinationDetails,
        bool allowApplyToAll)
    {
        var window =
            new Window
            {
                Title = title,
                Width = 520,
                MinWidth = 520,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
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
        var applyToAllBox =
            new CheckBox
            {
                Content = "Apply this choice to all conflicts in this upload",
                IsVisible = allowApplyToAll
            };

        btnPanel.Children.Add(overwriteBtn);
        btnPanel.Children.Add(duplicateBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(label);
        stack.Children.Add(sourceLabel);
        stack.Children.Add(destLabel);
        stack.Children.Add(applyToAllBox);
        stack.Children.Add(btnPanel);
        window.Content = stack;

        var choice = ConflictChoice.Cancel;
        var applyToAll = false;

        overwriteBtn.Click +=
            (_,
                _) =>
            {
                choice = ConflictChoice.Overwrite;
                applyToAll = allowApplyToAll && (applyToAllBox.IsChecked ?? false);
                window.Close();
            };
        duplicateBtn.Click +=
            (_,
                _) =>
            {
                choice = ConflictChoice.Duplicate;
                applyToAll = allowApplyToAll && (applyToAllBox.IsChecked ?? false);
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

        return new ConflictDialogResult(choice, applyToAll);
    }

    public async Task<SymlinkDialogResult> ConfirmSymlinkBehaviorAsync(string title,
        string message,
        string details,
        bool allowApplyToAll)
    {
        var window =
            new Window
            {
                Title = title,
                Width = 560,
                MinWidth = 560,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(12), Spacing = 10 };
        var messageBlock = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var detailsBlock =
            new TextBlock
            {
                Text = details,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = Avalonia.Media.Brushes.Gray
            };
        var applyToAllBox =
            new CheckBox
            {
                Content = "Apply this choice to all symbolic links in this operation",
                IsVisible = allowApplyToAll
            };
        var buttonPanel =
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };

        var linkEntryButton = new Button { Content = "Operate On Link", IsDefault = true };
        var followTargetButton = new Button { Content = "Follow Target" };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true };

        buttonPanel.Children.Add(linkEntryButton);
        buttonPanel.Children.Add(followTargetButton);
        buttonPanel.Children.Add(cancelButton);

        stack.Children.Add(messageBlock);
        stack.Children.Add(detailsBlock);
        stack.Children.Add(applyToAllBox);
        stack.Children.Add(buttonPanel);
        window.Content = stack;

        var choice = SymlinkBehaviorChoice.Cancel;
        var applyToAll = false;

        linkEntryButton.Click +=
            (_, _) =>
            {
                choice = SymlinkBehaviorChoice.OperateOnLinkEntry;
                applyToAll = allowApplyToAll && (applyToAllBox.IsChecked ?? false);
                window.Close();
            };
        followTargetButton.Click +=
            (_, _) =>
            {
                choice = SymlinkBehaviorChoice.FollowLinkTarget;
                applyToAll = allowApplyToAll && (applyToAllBox.IsChecked ?? false);
                window.Close();
            };
        cancelButton.Click += (_, _) => { window.Close(); };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return new SymlinkDialogResult(choice, applyToAll);
    }

    public async Task<RemotePropertiesDialogResult?> ShowRemotePropertiesDialogAsync(RemotePropertiesDialogRequest request)
    {
        var window =
            new Window
            {
                Title = $"Properties - {request.Name}",
                Width = 520,
                MinWidth = 520,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 10 };

        stack.Children.Add(new TextBlock { Text = $"Path: {request.RemotePath}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        stack.Children.Add(
            new TextBlock
            {
                Text =
                    $"Type: {(request.IsDirectory ? "Directory" : "File")}  |  Size: {request.Size:N0} bytes  |  Modified: {(request.LastWriteTime == DateTime.MinValue ? "Unknown" : request.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
        stack.Children.Add(
            new TextBlock
            {
                Text =
                    $"Current permissions: {request.SymbolicPermissions} ({request.OctalPermissions})  |  {FormatIdentityLabel("Owner UID", request.UserId, request.OwnerName)}  |  {FormatIdentityLabel("Group GID", request.GroupId, request.GroupName)}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });

        var separator = new Border { Height = 1, Background = Avalonia.Media.Brushes.DimGray, Margin = new Thickness(0, 4) };
        stack.Children.Add(separator);

        var applyPermissionsBox = new CheckBox { Content = "Apply permissions (chmod)", IsChecked = true };
        var permissionsBox =
            new TextBox
            {
                Watermark = "Octal mode (eg. 755 or 0755)",
                Text = request.OctalPermissions
            };

        var applyOwnerBox = new CheckBox { Content = "Apply owner (chown)", IsChecked = false };
        var ownerBox = new TextBox { Watermark = "Owner UID", Text = request.UserId.ToString(), IsEnabled = false };

        var applyGroupBox = new CheckBox { Content = "Apply group (chgrp)", IsChecked = false };
        var groupBox = new TextBox { Watermark = "Group GID", Text = request.GroupId.ToString(), IsEnabled = false };

        var recursiveBox =
            new CheckBox
            {
                Content = "Apply recursively",
                IsChecked = false,
                IsVisible = request.IsDirectory
            };

        var errorBlock =
            new TextBlock
            {
                Foreground = Avalonia.Media.Brushes.IndianRed,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

        void UpdateInputState()
        {
            permissionsBox.IsEnabled = applyPermissionsBox.IsChecked ?? false;
            ownerBox.IsEnabled = applyOwnerBox.IsChecked ?? false;
            groupBox.IsEnabled = applyGroupBox.IsChecked ?? false;
        }

        applyPermissionsBox.IsCheckedChanged += (_, _) => UpdateInputState();
        applyOwnerBox.IsCheckedChanged += (_, _) => UpdateInputState();
        applyGroupBox.IsCheckedChanged += (_, _) => UpdateInputState();
        UpdateInputState();

        stack.Children.Add(applyPermissionsBox);
        stack.Children.Add(permissionsBox);
        stack.Children.Add(applyOwnerBox);
        stack.Children.Add(ownerBox);
        stack.Children.Add(applyGroupBox);
        stack.Children.Add(groupBox);
        stack.Children.Add(recursiveBox);
        stack.Children.Add(errorBlock);

        var buttonPanel =
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Margin = new Thickness(0, 8, 0, 0)
            };
        var applyButton = new Button { Content = "Apply", IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true };
        buttonPanel.Children.Add(applyButton);
        buttonPanel.Children.Add(cancelButton);
        stack.Children.Add(buttonPanel);
        window.Content = stack;

        RemotePropertiesDialogResult? result = null;

        applyButton.Click +=
            (_, _) =>
            {
                var applyPermissions = applyPermissionsBox.IsChecked ?? false;
                var applyOwner = applyOwnerBox.IsChecked ?? false;
                var applyGroup = applyGroupBox.IsChecked ?? false;

                if (!applyPermissions && !applyOwner && !applyGroup)
                {
                    errorBlock.Text = "Select at least one change to apply.";
                    return;
                }

                short mode = 0;
                if (applyPermissions && !TryParseOctalPermissionMode(permissionsBox.Text, out mode))
                {
                    errorBlock.Text = "Invalid permission mode. Use octal format like 755 or 0755.";
                    return;
                }

                int ownerId = request.UserId;
                if (applyOwner && (!int.TryParse(ownerBox.Text, out ownerId) || ownerId < 0))
                {
                    errorBlock.Text = "Invalid owner UID. Enter a numeric value.";
                    return;
                }

                int groupId = request.GroupId;
                if (applyGroup && (!int.TryParse(groupBox.Text, out groupId) || groupId < 0))
                {
                    errorBlock.Text = "Invalid group GID. Enter a numeric value.";
                    return;
                }

                result =
                    new RemotePropertiesDialogResult
                    {
                        ApplyPermissions = applyPermissions,
                        PermissionMode = mode,
                        ApplyOwnerId = applyOwner,
                        OwnerId = ownerId,
                        ApplyGroupId = applyGroup,
                        GroupId = groupId,
                        ApplyRecursively = request.IsDirectory && (recursiveBox.IsChecked ?? false)
                    };
                window.Close();
            };

        cancelButton.Click += (_, _) => { window.Close(); };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return result;
    }

    private static bool TryParseOctalPermissionMode(string? text,
        out short mode)
    {
        mode = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        if (value.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        else if (value.Length > 1 && value.StartsWith('0'))
            value = value[1..];

        if (value.Length is < 3 or > 4)
            return false;

        var parsed = 0;
        foreach (var ch in value)
        {
            if (ch is < '0' or > '7')
                return false;

            parsed = (parsed * 8) + (ch - '0');
        }

        if (parsed < 0 || parsed > 0x0FFF)
            return false;

        mode = (short)parsed;
        return true;
    }

    private static string FormatIdentityLabel(string label,
        int id,
        string? resolvedName)
    {
        var name = string.IsNullOrWhiteSpace(resolvedName) ? "unknown" : resolvedName.Trim();
        return $"{label}: {id} ({name})";
    }

    public async Task<ServerProfile?> ShowServerFormAsync(ServerProfile? existing = null)
    {
        static AuthMethodPreference ResolveSelected(ComboBox comboBox,
            AuthMethodPreference fallback)
        {
            return comboBox.SelectedItem is AuthMethodPreference selected
                ? selected
                : fallback;
        }

        static bool OrdersEqual(IReadOnlyList<AuthMethodPreference> left,
            IReadOnlyList<AuthMethodPreference> right)
        {
            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        static string FormatOrder(IReadOnlyList<AuthMethodPreference> order)
        {
            var labels = new string[order.Count];
            for (var i = 0; i < order.Count; i++)
                labels[i] = AuthPreferenceOrder.ToLabel(order[i]);

            return string.Join(" -> ", labels);
        }

        var window =
            new Window
            {
                Title = existing is null ? "Add Server" : "Edit Server",
                Width = 520,
                MinWidth = 520,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var existingUsePrivateKey = existing?.UsePrivateKey ?? false;
        var initialAuthOrder =
            existing is null
                ? new List<AuthMethodPreference>
                {
                    AuthMethodPreference.Agent,
                    AuthMethodPreference.PrivateKey,
                    AuthMethodPreference.Password
                }
                : AuthPreferenceOrder.Normalize(existing.AuthPreferenceOrder, existingUsePrivateKey);
        var authOptions = new[] { AuthMethodPreference.Agent, AuthMethodPreference.PrivateKey, AuthMethodPreference.Password };
        var authPresets = new[]
        {
            (
                Name: "Automatic Secure (Recommended)",
                Description: "Best for modern setups. Prioritizes key-based auth and falls back automatically.",
                Order: new[] { AuthMethodPreference.Agent, AuthMethodPreference.PrivateKey, AuthMethodPreference.Password }
            ),
            (
                Name: "Key File First",
                Description: "Use the configured key file before agent identities, then fall back to password.",
                Order: new[] { AuthMethodPreference.PrivateKey, AuthMethodPreference.Agent, AuthMethodPreference.Password }
            ),
            (
                Name: "Compatibility Password First",
                Description: "For older/strict servers that limit auth attempts and may reject multiple key tries.",
                Order: new[] { AuthMethodPreference.Password, AuthMethodPreference.Agent, AuthMethodPreference.PrivateKey }
            )
        };
        const string customPresetName = "Custom Order";

        var selectedPresetName = customPresetName;
        for (var i = 0; i < authPresets.Length; i++)
        {
            if (OrdersEqual(initialAuthOrder, authPresets[i].Order))
            {
                selectedPresetName = authPresets[i].Name;
                break;
            }
        }

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        var tabs = new TabControl();
        var mainTabStack = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 10 };
        var advancedTabStack = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 10 };
        const string authModeSimple = "Simple";
        const string authModeAdvanced = "Advanced";

        var aliasBox = new TextBox { Watermark = "Server Alias (Name)", Text = existing?.Name ?? "" };
        var hostBox = new TextBox { Watermark = "Server IP Address / Host", Text = existing?.Host ?? "" };
        var portBox = new TextBox { Watermark = "Port", Text = existing?.Port.ToString() ?? "22" };
        var userBox = new TextBox { Watermark = "Username", Text = existing?.Username ?? "root" };
        var defaultModeOrder =
            AuthPreferenceOrder.Normalize(
                existingUsePrivateKey
                    ? new[] { AuthMethodPreference.PrivateKey, AuthMethodPreference.Password, AuthMethodPreference.Agent }
                    : new[] { AuthMethodPreference.Password, AuthMethodPreference.PrivateKey, AuthMethodPreference.Agent },
                existingUsePrivateKey);
        var authModeBox =
            new ComboBox
            {
                ItemsSource = new[] { authModeSimple, authModeAdvanced },
                SelectedItem = authModeSimple
            };
        var modeRow =
            new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
                ColumnSpacing = 10
            };
        var modeLabel = new TextBlock { Text = "Mode:", VerticalAlignment = VerticalAlignment.Center };
        var usePrivateKeyDefaultBox =
            new CheckBox
            {
                Content = "Use private key",
                IsChecked = existingUsePrivateKey,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
        Grid.SetColumn(modeLabel, 0);
        Grid.SetColumn(authModeBox, 1);
        Grid.SetColumn(usePrivateKeyDefaultBox, 3);
        modeRow.Children.Add(modeLabel);
        modeRow.Children.Add(authModeBox);
        modeRow.Children.Add(usePrivateKeyDefaultBox);
        var authPresetBox =
            new ComboBox
            {
                ItemsSource = new[] { authPresets[0].Name, authPresets[1].Name, authPresets[2].Name, customPresetName },
                SelectedItem = selectedPresetName
            };
        var authLabelRow =
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
        var authLabel = new TextBlock { Text = "Authentication Strategy:" };
        var authInfoTooltipContent =
            new TextBlock
            {
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 360
            };
        var authInfoBadgeText =
            new TextBlock
            {
                Text = "?",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Avalonia.Media.FontWeight.Bold
            };
        var authInfoBadge =
            new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = Avalonia.Media.Brushes.Gray,
                Background = Avalonia.Media.Brushes.Transparent,
                Child = authInfoBadgeText
            };
        ToolTip.SetTip(authInfoBadge, new ToolTip { Content = authInfoTooltipContent });
        authLabelRow.Children.Add(authLabel);
        authLabelRow.Children.Add(authInfoBadge);
        var authHint =
            new TextBlock
            {
                FontSize = 11,
                Foreground = Avalonia.Media.Brushes.Gray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
        var customOrderHeader = new TextBlock { Text = "Custom Failover Order (highest first):" };
        var customPrimaryLabel = new TextBlock { Text = "1st:" };
        var authPrimaryBox = new ComboBox { ItemsSource = authOptions, SelectedItem = initialAuthOrder[0] };
        var customSecondaryLabel = new TextBlock { Text = "2nd:" };
        var authSecondaryBox = new ComboBox { ItemsSource = authOptions, SelectedItem = initialAuthOrder[1] };
        var customTertiaryLabel = new TextBlock { Text = "3rd:" };
        var authTertiaryBox = new ComboBox { ItemsSource = authOptions, SelectedItem = initialAuthOrder[2] };
        var passLabel = new TextBlock { Text = "Password (optional):" };
        var passBox = new TextBox { PasswordChar = '*', Watermark = "Password", Text = existing?.Password ?? "" };
        var keyPathLabel = new TextBlock { Text = "Private Key Path (optional):" };
        var keyPathBox = new TextBox { Watermark = "Private key path", Text = existing?.PrivateKeyPath ?? "" };
        var keyBrowseBtn = new Button { Content = "Browse..." };
        var keyPassLabel = new TextBlock { Text = "Key Passphrase (optional):" };
        var keyPassBox =
            new TextBox
            {
                PasswordChar = '*', Watermark = "Key passphrase (optional)", Text = existing?.PrivateKeyPassphrase ?? ""
            };
        var timeoutBox =
            new TextBox
            {
                Watermark = "Connection timeout (seconds)",
                Text = (existing?.ConnectionTimeoutSeconds ?? 15).ToString()
            };
        var keepAliveBox =
            new TextBox
            {
                Watermark = "Keepalive interval (seconds)",
                Text = (existing?.KeepAliveIntervalSeconds ?? 30).ToString()
            };
        var reconnectStrategyLabel = new TextBlock { Text = "Reconnect Strategy:" };
        var reconnectStrategyBox =
            new ComboBox
            {
                ItemsSource = new[] { ReconnectStrategy.None, ReconnectStrategy.FixedInterval },
                SelectedItem = existing?.ReconnectStrategy ?? ReconnectStrategy.FixedInterval
            };
        var reconnectAttemptsLabel = new TextBlock { Text = "Reconnect Attempts:" };
        var reconnectAttemptsBox =
            new TextBox
            {
                Watermark = "Reconnect attempts",
                Text = (existing?.ReconnectAttempts ?? 2).ToString()
            };
        var reconnectDelayLabel = new TextBlock { Text = "Reconnect Delay (seconds):" };
        var reconnectDelayBox =
            new TextBox
            {
                Watermark = "Reconnect delay",
                Text = (existing?.ReconnectDelaySeconds ?? 2).ToString()
            };

        mainTabStack.Children.Add(modeRow);
        mainTabStack.Children.Add(new TextBlock { Text = "Server Alias:" });
        mainTabStack.Children.Add(aliasBox);
        mainTabStack.Children.Add(new TextBlock { Text = "Host / IP:" });
        mainTabStack.Children.Add(hostBox);
        mainTabStack.Children.Add(new TextBlock { Text = "Port:" });
        mainTabStack.Children.Add(portBox);
        mainTabStack.Children.Add(new TextBlock { Text = "Username:" });
        mainTabStack.Children.Add(userBox);
        mainTabStack.Children.Add(authLabelRow);
        mainTabStack.Children.Add(authPresetBox);
        mainTabStack.Children.Add(authHint);
        mainTabStack.Children.Add(customOrderHeader);
        mainTabStack.Children.Add(customPrimaryLabel);
        mainTabStack.Children.Add(authPrimaryBox);
        mainTabStack.Children.Add(customSecondaryLabel);
        mainTabStack.Children.Add(authSecondaryBox);
        mainTabStack.Children.Add(customTertiaryLabel);
        mainTabStack.Children.Add(authTertiaryBox);
        mainTabStack.Children.Add(passLabel);
        mainTabStack.Children.Add(passBox);
        mainTabStack.Children.Add(keyPathLabel);
        var keyPathRow =
            new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8
            };
        keyPathBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(keyPathBox, 0);
        Grid.SetColumn(keyBrowseBtn, 1);
        keyPathRow.Children.Add(keyPathBox);
        keyPathRow.Children.Add(keyBrowseBtn);
        mainTabStack.Children.Add(keyPathRow);
        mainTabStack.Children.Add(keyPassLabel);
        mainTabStack.Children.Add(keyPassBox);

        advancedTabStack.Children.Add(new TextBlock { Text = "Connection Timeout (seconds):" });
        advancedTabStack.Children.Add(timeoutBox);
        advancedTabStack.Children.Add(new TextBlock { Text = "Keepalive Interval (seconds):" });
        advancedTabStack.Children.Add(keepAliveBox);
        advancedTabStack.Children.Add(reconnectStrategyLabel);
        advancedTabStack.Children.Add(reconnectStrategyBox);
        advancedTabStack.Children.Add(reconnectAttemptsLabel);
        advancedTabStack.Children.Add(reconnectAttemptsBox);
        advancedTabStack.Children.Add(reconnectDelayLabel);
        advancedTabStack.Children.Add(reconnectDelayBox);

        tabs.Items.Add(new TabItem { Header = "Main", Content = mainTabStack });
        tabs.Items.Add(new TabItem { Header = "Advanced", Content = advancedTabStack });
        root.Children.Add(tabs);

        var btnPanel =
            new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
                Margin = new Thickness(0, 8, 0, 0)
            };
        var saveBtn = new Button { Content = "Save", IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
        btnPanel.Children.Add(saveBtn);
        btnPanel.Children.Add(cancelBtn);
        root.Children.Add(btnPanel);
        window.Content = root;

        IReadOnlyList<AuthMethodPreference> ResolveAuthOrder()
        {
            var isAdvancedMode =
                string.Equals(authModeBox.SelectedItem as string, authModeAdvanced, StringComparison.Ordinal);
            if (!isAdvancedMode)
            {
                var useKey = usePrivateKeyDefaultBox.IsChecked ?? false;
                return AuthPreferenceOrder.Normalize(
                    useKey
                        ? new[]
                        {
                            AuthMethodPreference.PrivateKey,
                            AuthMethodPreference.Password,
                            AuthMethodPreference.Agent
                        }
                        : new[]
                        {
                            AuthMethodPreference.Password,
                            AuthMethodPreference.PrivateKey,
                            AuthMethodPreference.Agent
                        },
                    useKey);
            }

            var selectedPreset = authPresetBox.SelectedItem as string;
            for (var i = 0; i < authPresets.Length; i++)
            {
                if (string.Equals(authPresets[i].Name, selectedPreset, StringComparison.Ordinal))
                    return authPresets[i].Order;
            }

            return AuthPreferenceOrder.Normalize(
                new[]
                {
                    ResolveSelected(authPrimaryBox, initialAuthOrder[0]),
                    ResolveSelected(authSecondaryBox, initialAuthOrder[1]),
                    ResolveSelected(authTertiaryBox, initialAuthOrder[2])
                },
                existingUsePrivateKey);
        }

        void UpdateVisibility()
        {
            var reconnectEnabled = reconnectStrategyBox.SelectedItem is ReconnectStrategy.FixedInterval;
            reconnectAttemptsLabel.IsVisible = reconnectEnabled;
            reconnectAttemptsBox.IsVisible = reconnectEnabled;
            reconnectDelayLabel.IsVisible = reconnectEnabled;
            reconnectDelayBox.IsVisible = reconnectEnabled;

            var isAdvancedMode =
                string.Equals(authModeBox.SelectedItem as string, authModeAdvanced, StringComparison.Ordinal);
            usePrivateKeyDefaultBox.IsVisible = !isAdvancedMode;
            authLabelRow.IsVisible = isAdvancedMode;
            authPresetBox.IsVisible = isAdvancedMode;
            authHint.IsVisible = isAdvancedMode;

            var useCustom = isAdvancedMode &&
                            string.Equals(authPresetBox.SelectedItem as string, customPresetName, StringComparison.Ordinal);
            customOrderHeader.IsVisible = useCustom;
            customPrimaryLabel.IsVisible = useCustom;
            authPrimaryBox.IsVisible = useCustom;
            customSecondaryLabel.IsVisible = useCustom;
            authSecondaryBox.IsVisible = useCustom;
            customTertiaryLabel.IsVisible = useCustom;
            authTertiaryBox.IsVisible = useCustom;

            var resolvedOrder = ResolveAuthOrder();
            if (isAdvancedMode && !useCustom)
            {
                var selectedPresetNameLocal = authPresetBox.SelectedItem as string;
                var presetDescription = "Authentication fallback strategy.";
                for (var i = 0; i < authPresets.Length; i++)
                {
                    if (string.Equals(authPresets[i].Name, selectedPresetNameLocal, StringComparison.Ordinal))
                    {
                        presetDescription = authPresets[i].Description;
                        break;
                    }
                }

                authHint.Text = $"Failover: {FormatOrder(resolvedOrder)}.";
                authInfoTooltipContent.Text = $"{presetDescription} Failover: {FormatOrder(resolvedOrder)}.";
            }
            else if (isAdvancedMode)
            {
                authHint.Text = $"Custom failover: {FormatOrder(resolvedOrder)}.";
                authInfoTooltipContent.Text = $"Manual authentication order. Failover: {FormatOrder(resolvedOrder)}.";
            }

            if (isAdvancedMode)
            {
                passLabel.Text = "Password (optional):";
                keyPathLabel.Text = "Private Key Path (optional):";
                keyPassLabel.Text = "Key Passphrase (optional):";
                passLabel.IsVisible = true;
                passBox.IsVisible = true;
                keyPathLabel.IsVisible = true;
                keyPathRow.IsVisible = true;
                keyPassLabel.IsVisible = true;
                keyPassBox.IsVisible = true;
            }
            else
            {
                var useKey = usePrivateKeyDefaultBox.IsChecked ?? false;
                passLabel.Text = "Password:";
                keyPathLabel.Text = "Private Key Path:";
                keyPassLabel.Text = "Key Passphrase:";
                passLabel.IsVisible = !useKey;
                passBox.IsVisible = !useKey;
                keyPathLabel.IsVisible = useKey;
                keyPathRow.IsVisible = useKey;
                keyPassLabel.IsVisible = useKey;
                keyPassBox.IsVisible = useKey;
            }
        }

        reconnectStrategyBox.SelectionChanged +=
            (_,
                _) => UpdateVisibility();
        authModeBox.SelectionChanged +=
            (_,
                _) => UpdateVisibility();
        usePrivateKeyDefaultBox.IsCheckedChanged +=
            (_,
                _) => UpdateVisibility();
        authPresetBox.SelectionChanged +=
            (_,
                _) => UpdateVisibility();
        authPrimaryBox.SelectionChanged +=
            (_,
                _) => UpdateVisibility();
        authSecondaryBox.SelectionChanged +=
            (_,
                _) => UpdateVisibility();
        authTertiaryBox.SelectionChanged +=
            (_,
                _) => UpdateVisibility();
        UpdateVisibility();

        ServerProfile? result = null;

        saveBtn.Click +=
            (_,
                _) =>
            {
                if (!int.TryParse(portBox.Text, out var port))
                    return;

                var reconnectStrategy =
                    reconnectStrategyBox.SelectedItem is ReconnectStrategy selected
                        ? selected
                        : ReconnectStrategy.FixedInterval;

                if (!int.TryParse(timeoutBox.Text, out var timeoutSeconds))
                    timeoutSeconds = existing?.ConnectionTimeoutSeconds ?? 15;
                timeoutSeconds = Math.Clamp(timeoutSeconds, 3, 300);

                if (!int.TryParse(keepAliveBox.Text, out var keepAliveSeconds))
                    keepAliveSeconds = existing?.KeepAliveIntervalSeconds ?? 30;
                keepAliveSeconds = Math.Clamp(keepAliveSeconds, 0, 300);

                if (!int.TryParse(reconnectAttemptsBox.Text, out var reconnectAttempts))
                    reconnectAttempts = existing?.ReconnectAttempts ?? 2;
                reconnectAttempts =
                    reconnectStrategy == ReconnectStrategy.None
                        ? 0
                        : Math.Clamp(reconnectAttempts, 1, 10);

                if (!int.TryParse(reconnectDelayBox.Text, out var reconnectDelaySeconds))
                    reconnectDelaySeconds = existing?.ReconnectDelaySeconds ?? 2;
                reconnectDelaySeconds =
                    reconnectStrategy == ReconnectStrategy.None
                        ? 0
                        : Math.Clamp(reconnectDelaySeconds, 1, 30);

                var isAdvancedMode =
                    string.Equals(authModeBox.SelectedItem as string, authModeAdvanced, StringComparison.Ordinal);
                var selectedAuthOrder = new List<AuthMethodPreference>(ResolveAuthOrder());
                var usePrivateKeyLegacy = isAdvancedMode
                    ? selectedAuthOrder.Count > 0 &&
                      selectedAuthOrder[0] == AuthMethodPreference.PrivateKey
                    : (usePrivateKeyDefaultBox.IsChecked ?? false);
                var passwordValue =
                    isAdvancedMode
                        ? passBox.Text ?? string.Empty
                        : (usePrivateKeyLegacy ? string.Empty : (passBox.Text ?? string.Empty));
                var privateKeyPathValue =
                    isAdvancedMode
                        ? keyPathBox.Text ?? string.Empty
                        : (usePrivateKeyLegacy ? keyPathBox.Text ?? string.Empty : string.Empty);
                var keyPassphraseValue =
                    isAdvancedMode
                        ? keyPassBox.Text ?? string.Empty
                        : (usePrivateKeyLegacy ? keyPassBox.Text ?? string.Empty : string.Empty);

                result =
                    new ServerProfile
                    {
                        Id = existing?.Id ?? System.Guid.NewGuid().ToString(),
                        Name = aliasBox.Text ?? "New Server",
                        Host = hostBox.Text ?? "localhost",
                        Port = port,
                        Username = userBox.Text ?? "root",
                        ConnectionTimeoutSeconds = timeoutSeconds,
                        KeepAliveIntervalSeconds = keepAliveSeconds,
                        ReconnectStrategy = reconnectStrategy,
                        ReconnectAttempts = reconnectAttempts,
                        ReconnectDelaySeconds = reconnectDelaySeconds,
                        Password = passwordValue,
                        UsePrivateKey = usePrivateKeyLegacy,
                        AuthPreferenceOrder = selectedAuthOrder,
                        PrivateKeyPath = privateKeyPathValue,
                        PrivateKeyPassphrase = keyPassphraseValue
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
                aliasBox.Focus();
                aliasBox.SelectAll();
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
        static AuthMethodPreference ResolveSelected(ComboBox comboBox,
            AuthMethodPreference fallback)
        {
            return comboBox.SelectedItem is AuthMethodPreference selected
                ? selected
                : fallback;
        }

        var window =
            new Window
            {
                Title = title,
                Width = 440,
                MinWidth = 440,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var authOptions = new[] { AuthMethodPreference.Agent, AuthMethodPreference.PrivateKey, AuthMethodPreference.Password };
        var legacyUsePrivateKey = defaults?.UsePrivateKey ?? false;
        var authOrder =
            defaults is null
                ? new List<AuthMethodPreference>
                {
                    AuthMethodPreference.Agent,
                    AuthMethodPreference.PrivateKey,
                    AuthMethodPreference.Password
                }
                : AuthPreferenceOrder.Normalize(defaults.AuthPreferenceOrder, legacyUsePrivateKey);

        var userBox = new TextBox { Text = defaults?.Username ?? "root" };
        var authPrimaryBox = new ComboBox { ItemsSource = authOptions, SelectedItem = authOrder[0] };
        var authSecondaryBox = new ComboBox { ItemsSource = authOptions, SelectedItem = authOrder[1] };
        var authTertiaryBox = new ComboBox { ItemsSource = authOptions, SelectedItem = authOrder[2] };
        var passBox = new TextBox { PasswordChar = '*', Watermark = "Password (optional)" };
        var passHint =
            new TextBlock
            {
                Text = "Leave blank to use saved password after device verification, or to skip password auth.",
                FontSize = 11,
                Foreground = Avalonia.Media.Brushes.Gray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
        var keyPathBox = new TextBox { Watermark = "Private key path (optional)", Text = defaults?.PrivateKeyPath ?? "" };
        var keyBrowseBtn = new Button { Content = "Browse..." };
        var keyPathHint =
            new TextBlock
            {
                Text = "Leave blank to skip private-key file auth.",
                FontSize = 11,
                Foreground = Avalonia.Media.Brushes.Gray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
        var keyPassBox =
            new TextBox
            {
                PasswordChar = '*', Watermark = "Key passphrase (optional)"
            };
        var keyPassHint =
            new TextBlock
            {
                Text = "Leave blank to use saved key passphrase after device verification.",
                FontSize = 11,
                Foreground = Avalonia.Media.Brushes.Gray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
        var agentHint =
            new TextBlock
            {
                Text = "SSH agent auth uses identities already loaded in your agent (SSH_AUTH_SOCK/Pageant).",
                FontSize = 11,
                Foreground = Avalonia.Media.Brushes.Gray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

        stack.Children.Add(new TextBlock { Text = $"Connect to {host}" });
        stack.Children.Add(new TextBlock { Text = "Username:" });
        stack.Children.Add(userBox);
        stack.Children.Add(new TextBlock { Text = "Auth Order (highest first):" });
        stack.Children.Add(new TextBlock { Text = "1st:" });
        stack.Children.Add(authPrimaryBox);
        stack.Children.Add(new TextBlock { Text = "2nd:" });
        stack.Children.Add(authSecondaryBox);
        stack.Children.Add(new TextBlock { Text = "3rd:" });
        stack.Children.Add(authTertiaryBox);
        stack.Children.Add(agentHint);
        stack.Children.Add(new TextBlock { Text = "Password:" });
        stack.Children.Add(passBox);
        stack.Children.Add(passHint);
        stack.Children.Add(new TextBlock { Text = "Private Key Path:" });
        var keyPathRow =
            new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8
            };
        keyPathBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(keyPathBox, 0);
        Grid.SetColumn(keyBrowseBtn, 1);
        keyPathRow.Children.Add(keyPathBox);
        keyPathRow.Children.Add(keyBrowseBtn);
        stack.Children.Add(keyPathRow);
        stack.Children.Add(keyPathHint);
        stack.Children.Add(new TextBlock { Text = "Key Passphrase:" });
        stack.Children.Add(keyPassBox);
        stack.Children.Add(keyPassHint);

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
        window.Content = stack;

        ConnectInfo? result = null;

        connectBtn.Click +=
            (_,
                _) =>
            {
                var selectedAuthOrder =
                    AuthPreferenceOrder.Normalize(
                        new[]
                        {
                            ResolveSelected(authPrimaryBox, authOrder[0]),
                            ResolveSelected(authSecondaryBox, authOrder[1]),
                            ResolveSelected(authTertiaryBox, authOrder[2])
                        },
                        legacyUsePrivateKey);
                result =
                    new ConnectInfo
                    {
                        Username = userBox.Text ?? string.Empty,
                        AuthPreferenceOrder = selectedAuthOrder,
                        Password = passBox.Text ?? string.Empty,
                        PrivateKeyPath = keyPathBox.Text ?? string.Empty,
                        PrivateKeyPassphrase = keyPassBox.Text ?? string.Empty
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
                passBox.Focus();
                passBox.SelectAll();
            };

        var owner = GetMainWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return result;
    }
}
