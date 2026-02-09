using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using NoBSSftp.Models;
using NoBSSftp.Services;
using NoBSSftp.ViewModels;

namespace NoBSSftp.Views;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> ServerIdFormat =
        DataFormat.CreateStringApplicationFormat("NoBSSftp.ServerProfileId");

    private Point? _dragStartPoint;
    private ServerProfile? _draggedProfile;
    private ListBoxItem? _dropTargetItem;
    private bool _dropTargetAfter;
    private int? _dropTargetIndex;
    private ListBox? _dropTargetList;
    private ListBox? _appendDropTargetList;
    private Border? _folderDropTarget;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnServerItemPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnServerItemPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnServerItemPointerReleased, RoutingStrategies.Tunnel);
    }

    private async void OnManageTrustedHostKeysMenuItemClick(object? sender,
        EventArgs e)
    {
        var hostKeyTrustService = new HostKeyTrustService();
        var dialogService = new DialogService();
        var rows = new ObservableCollection<TrustedHostKeyRow>();

        var dialog =
            new Window
            {
                Title = "Trusted Host Keys",
                Width = 1060,
                MinWidth = 860,
                Height = 520,
                MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var description =
            new TextBlock
            {
                Text =
                    "Review trusted SSH host keys. Removing an entry forces re-verification on the next connection.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

        var keyList =
            new ListBox
            {
                ItemsSource = rows,
                SelectionMode = SelectionMode.Single
            };

        static TextBlock MakeCell(string value)
        {
            return new TextBlock
            {
                Text = value,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        static void AddHeader(Grid grid,
            string text,
            int column)
        {
            var headerCell =
                new TextBlock
                {
                    Text = text,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
            Grid.SetColumn(headerCell, column);
            grid.Children.Add(headerCell);
        }

        keyList.ItemTemplate = new FuncDataTemplate<TrustedHostKeyRow>(
            (item, _) =>
            {
                var rowGrid =
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("1.1*,90,160,2.6*,190"),
                        ColumnSpacing = 10,
                        Margin = new Thickness(8, 6)
                    };

                var host = MakeCell(item.Host);
                var port = MakeCell(item.Port.ToString());
                var algorithm = MakeCell(item.KeyAlgorithm);
                var fingerprint = MakeCell(item.FingerprintSha256);
                var firstSeen = MakeCell(item.FirstSeen);

                Grid.SetColumn(host, 0);
                Grid.SetColumn(port, 1);
                Grid.SetColumn(algorithm, 2);
                Grid.SetColumn(fingerprint, 3);
                Grid.SetColumn(firstSeen, 4);

                rowGrid.Children.Add(host);
                rowGrid.Children.Add(port);
                rowGrid.Children.Add(algorithm);
                rowGrid.Children.Add(fingerprint);
                rowGrid.Children.Add(firstSeen);
                return rowGrid;
            },
            supportsRecycling: true);

        var headerGrid =
            new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1.1*,90,160,2.6*,190"),
                ColumnSpacing = 10,
                Margin = new Thickness(8, 6)
            };
        AddHeader(headerGrid, "Host", 0);
        AddHeader(headerGrid, "Port", 1);
        AddHeader(headerGrid, "Algorithm", 2);
        AddHeader(headerGrid, "Fingerprint (SHA256)", 3);
        AddHeader(headerGrid, "First Seen", 4);

        var headerBorder =
            new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Avalonia.Media.Brushes.DimGray,
                Child = headerGrid
            };

        var listBorder =
            new Border
            {
                BorderThickness = new Thickness(1, 0, 1, 1),
                BorderBrush = Avalonia.Media.Brushes.DimGray,
                Child = keyList
            };

        var tableLayout =
            new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*")
            };
        Grid.SetRow(headerBorder, 0);
        Grid.SetRow(listBorder, 1);
        tableLayout.Children.Add(headerBorder);
        tableLayout.Children.Add(listBorder);

        var statusBlock =
            new TextBlock
            {
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Opacity = 0.85
            };

        var refreshButton = new Button { Content = "Refresh" };
        var removeSelectedButton = new Button { Content = "Remove Selected", IsEnabled = false };
        var clearAllButton = new Button { Content = "Clear All" };
        var closeButton =
            new Button
            {
                Content = "Close",
                IsDefault = true
            };

        void ReloadRows(string? message = null)
        {
            rows.Clear();
            foreach (var entry in hostKeyTrustService.GetTrustedHostKeys())
            {
                rows.Add(new TrustedHostKeyRow(entry));
            }

            keyList.SelectedItem = null;
            removeSelectedButton.IsEnabled = false;

            if (!string.IsNullOrWhiteSpace(message))
            {
                statusBlock.Text = message;
                return;
            }

            statusBlock.Text =
                rows.Count == 0
                    ? "No trusted host keys found."
                    : $"Showing {rows.Count} trusted host key {(rows.Count == 1 ? "entry" : "entries")}.";
        }

        keyList.SelectionChanged += (_, _) => { removeSelectedButton.IsEnabled = keyList.SelectedItem is TrustedHostKeyRow; };

        refreshButton.Click += (_, _) => { ReloadRows(); };

        removeSelectedButton.Click += async (_, _) =>
        {
            if (keyList.SelectedItem is not TrustedHostKeyRow selected)
                return;

            var confirmed =
                await dialogService.ConfirmAsync(
                    "Remove Trusted Host Key",
                    $"Remove trusted key for {selected.Host}:{selected.Port} ({selected.KeyAlgorithm})?\n\n" +
                    "This host will require verification again on next connect.");
            if (!confirmed)
                return;

            if (!hostKeyTrustService.RemoveTrustedHostKey(selected.Entry))
            {
                ReloadRows("Failed to remove trusted host key entry.");
                return;
            }

            ReloadRows($"Removed trusted key for {selected.Host}:{selected.Port}.");
        };

        clearAllButton.Click += async (_, _) =>
        {
            if (rows.Count == 0)
            {
                statusBlock.Text = "No trusted host keys to clear.";
                return;
            }

            var confirmed =
                await dialogService.ConfirmAsync(
                    "Clear Trusted Host Keys",
                    "Clear all trusted host keys?\n\nAll hosts will require verification on next connect.");
            if (!confirmed)
                return;

            hostKeyTrustService.ClearTrustedHostKeys();
            ReloadRows("Cleared all trusted host keys.");
        };

        closeButton.Click += (_, _) => { dialog.Close(); };

        var actions =
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        actions.Children.Add(refreshButton);
        actions.Children.Add(removeSelectedButton);
        actions.Children.Add(clearAllButton);

        var footer =
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
        footer.Children.Add(closeButton);

        var layout =
            new Grid
            {
                Margin = new Thickness(14),
                RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
                RowSpacing = 10
            };
        Grid.SetRow(description, 0);
        Grid.SetRow(tableLayout, 1);
        Grid.SetRow(statusBlock, 2);
        Grid.SetRow(actions, 3);
        layout.Children.Add(description);
        layout.Children.Add(tableLayout);
        layout.Children.Add(statusBlock);
        layout.Children.Add(actions);

        var root =
            new DockPanel
            {
                LastChildFill = true
            };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(layout);
        dialog.Content = root;

        ReloadRows();
        await dialog.ShowDialog(this);
    }

    private async void OnAboutMenuItemClick(object? sender,
        EventArgs e)
    {
        var version = DataContext is MainWindowViewModel vm ? vm.AppVersion : "unknown";

        var window =
            new Window
            {
                Title = "About NoBSSftp",
                Width = 420,
                MinWidth = 420,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SystemDecorations = SystemDecorations.BorderOnly
            };

        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = $"NoBSSftp v{version}", FontWeight = Avalonia.Media.FontWeight.Bold });
        stack.Children.Add(
            new TextBlock
            {
                Text = "No-BS SFTP client focused on secure transfer workflows.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
        stack.Children.Add(new TextBlock { Text = "Author: Beka Pukhashvili" });
        stack.Children.Add(new TextBlock { Text = "License: MIT" });

        var closeButton =
            new Button
            {
                Content = "Close",
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true
            };
        closeButton.Click += (_, _) => window.Close();
        stack.Children.Add(closeButton);
        window.Content = stack;

        await window.ShowDialog(this);
    }

    private void OnShowTransferQueueMenuItemClick(object? sender,
        EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.ShowTransferQueue = !vm.ShowTransferQueue;

        if (sender is NativeMenuItem item)
            item.IsChecked = vm.ShowTransferQueue;
    }

    private void OnShowServerExplorerMenuItemClick(object? sender,
        EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.IsSidebarOpen = !vm.IsSidebarOpen;

        if (sender is NativeMenuItem item)
            item.IsChecked = vm.IsSidebarOpen;
    }

    private void OnLockCredentialSessionMenuItemClick(object? sender,
        EventArgs e)
    {
        CredentialUnlockSession.Invalidate();
        LoggingService.Info("Credential unlock session locked by user.");

        if (DataContext is MainWindowViewModel { SelectedTabItem: SessionViewModel session })
            session.StatusMessage = "Credential session locked";
    }

    private void OnServerListDoubleTapped(object? sender,
        TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: ServerProfile profile }) return;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.ConnectToServerCommand.Execute(profile);
        }
    }

    private void OnServerListPointerPressed(object? sender,
        PointerPressedEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        var properties = e.GetCurrentPoint(listBox).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            _dragStartPoint = null;
            _draggedProfile = null;
            return;
        }

        var point = e.GetPosition(listBox);
        var item = GetItemAtPoint(listBox, point);
        _draggedProfile = item?.DataContext as ServerProfile;
        _dragStartPoint = point;
    }

    private async void OnServerListPointerMoved(object? sender,
        PointerEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (_dragStartPoint is null || _draggedProfile is null) return;

        var properties = e.GetCurrentPoint(listBox).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            _dragStartPoint = null;
            _draggedProfile = null;
            return;
        }

        var currentPoint = e.GetPosition(listBox);
        if (Math.Abs(currentPoint.X - _dragStartPoint.Value.X) < 3 &&
            Math.Abs(currentPoint.Y - _dragStartPoint.Value.Y) < 3)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ServerIdFormat, _draggedProfile.Id));
        _dragStartPoint = null;

        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private void OnServerListPointerReleased(object? sender,
        PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
        _draggedProfile = null;
        ClearDropIndicator();
    }

    private void OnServerItemPointerPressed(object? sender,
        PointerPressedEventArgs e)
    {
        if (e.Source is not Control control) return;
        var item = control.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not ServerProfile profile) return;

        var listBox = item.FindAncestorOfType<ListBox>();
        if (listBox is null) return;

        var properties = e.GetCurrentPoint(listBox).Properties;
        if (!properties.IsLeftButtonPressed) return;

        _draggedProfile = profile;
        _dragStartPoint = e.GetPosition(this);
    }

    private async void OnServerItemPointerMoved(object? sender,
        PointerEventArgs e)
    {
        if (_dragStartPoint is null || _draggedProfile is null) return;
        if (e.Source is not Control control) return;

        var listBox = control.FindAncestorOfType<ListBox>();
        if (listBox is null) return;

        var properties = e.GetCurrentPoint(listBox).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            _dragStartPoint = null;
            _draggedProfile = null;
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - _dragStartPoint.Value.X) < 3 &&
            Math.Abs(currentPoint.Y - _dragStartPoint.Value.Y) < 3)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ServerIdFormat, _draggedProfile.Id));
        _dragStartPoint = null;

        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private void OnServerItemPointerReleased(object? sender,
        PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
        _draggedProfile = null;
        ClearDropIndicator();
    }

    private void OnServerListDragOver(object? sender,
        DragEventArgs e)
    {
        var hasServerDrag =
            e.DataTransfer is not null &&
            e.DataTransfer.Items.Any(item => item.Formats.Contains(ServerIdFormat));

        if (!hasServerDrag)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        e.DragEffects = DragDropEffects.Move;

        if (sender is not ListBox listBox) return;
        UpdateDropIndicator(listBox, e);
        e.Handled = true;
    }

    private async void OnServerListDrop(object? sender,
        DragEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var serverId = TryGetDraggedServerId(e.DataTransfer);
        if (serverId is null) return;

        var source = FindServerLocation(vm, serverId);
        if (source is not { } src) return;

        var targetList = GetServerList(listBox, vm);
        if (targetList is null) return;

        var targetIndex =
            _dropTargetList == listBox && _dropTargetIndex.HasValue
                ? _dropTargetIndex.Value
                : GetDropIndex(listBox, e, targetList);
        if (targetIndex < 0) return;

        if (ReferenceEquals(src.List, targetList))
        {
            if (src.Index < targetIndex)
                targetIndex--;

            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex >= targetList.Count) targetIndex = targetList.Count - 1;

            if (src.Index == targetIndex)
            {
                ClearDropIndicator();
                return;
            }

            targetList.Move(src.Index, targetIndex);
        }
        else
        {
            src.List.RemoveAt(src.Index);
            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex > targetList.Count) targetIndex = targetList.Count;
            targetList.Insert(targetIndex, src.Profile);
        }

        await vm.SaveLibraryAsync();
        e.Handled = true;
        ClearDropIndicator();
    }

    private void OnServerListDragLeave(object? sender,
        DragEventArgs e)
    {
        ClearDropIndicator();
    }

    private static ListBoxItem? GetItemAtPoint(ListBox listBox,
        Point point)
    {
        var hit = listBox.InputHitTest(point) as Control;
        return hit?.FindAncestorOfType<ListBoxItem>();
    }

    private static int GetDropIndex(ListBox listBox,
        DragEventArgs e,
        System.Collections.ObjectModel.ObservableCollection<ServerProfile> list)
    {
        if (list.Count == 0) return 0;

        var item = GetItemAtPoint(listBox, e.GetPosition(listBox));
        if (item?.DataContext is ServerProfile targetProfile)
        {
            var index = list.IndexOf(targetProfile);
            var bounds = item.Bounds;
            var point = e.GetPosition(item);
            var after = point.Y > bounds.Height / 2;
            return after ? index + 1 : index;
        }

        return list.Count;
    }

    private void UpdateDropIndicator(ListBox listBox,
        DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            ClearDropIndicator();
            return;
        }

        var list = GetServerList(listBox, vm);
        if (list is null)
        {
            ClearDropIndicator();
            return;
        }

        var item = GetItemAtPoint(listBox, e.GetPosition(listBox));
        if (item?.DataContext is ServerProfile targetProfile)
        {
            var bounds = item.Bounds;
            var point = e.GetPosition(item);
            var after = point.Y > bounds.Height / 2;
            SetDropIndicator(listBox, item, after);
            var index = list.IndexOf(targetProfile);
            _dropTargetIndex = after ? index + 1 : index;
            return;
        }

        SetAppendDropIndicator(listBox, list.Count);
    }

    private void SetDropIndicator(ListBox listBox,
        ListBoxItem item,
        bool after)
    {
        if (_dropTargetItem == item && _dropTargetAfter == after && _dropTargetList == listBox)
            return;

        ClearDropIndicator();
        _dropTargetItem = item;
        _dropTargetAfter = after;
        _dropTargetList = listBox;
        if (after)
            item.Classes.Add("DropAfter");
        else
            item.Classes.Add("DropBefore");
    }

    private void SetAppendDropIndicator(ListBox listBox,
        int index)
    {
        if (_appendDropTargetList == listBox && _dropTargetIndex == index && _dropTargetItem is null)
            return;

        ClearDropIndicator();
        _appendDropTargetList = listBox;
        _dropTargetList = listBox;
        _dropTargetIndex = index;
        listBox.Classes.Add("DropAppend");
    }

    private void ClearDropIndicator()
    {
        if (_dropTargetItem is not null)
        {
            _dropTargetItem.Classes.Remove("DropAfter");
            _dropTargetItem.Classes.Remove("DropBefore");
        }

        if (_appendDropTargetList is not null)
            _appendDropTargetList.Classes.Remove("DropAppend");

        _dropTargetItem = null;
        _dropTargetAfter = false;
        _dropTargetIndex = null;
        _dropTargetList = null;
        _appendDropTargetList = null;
        ClearFolderDropIndicator();
    }

    private void OnFolderHeaderDragOver(object? sender,
        DragEventArgs e)
    {
        var hasServerDrag =
            e.DataTransfer is not null &&
            e.DataTransfer.Items.Any(item => item.Formats.Contains(ServerIdFormat));

        if (hasServerDrag)
            e.DragEffects = DragDropEffects.Move;
        else
            e.DragEffects = DragDropEffects.None;

        if (hasServerDrag && sender is Border border)
            SetFolderDropIndicator(border);
        else
            ClearFolderDropIndicator();

        e.Handled = true;
    }

    private async void OnFolderHeaderDrop(object? sender,
        DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Control control) return;
        if (control.DataContext is not ServerFolder folder) return;

        var serverId = TryGetDraggedServerId(e.DataTransfer);
        if (serverId is null) return;

        var source = FindServerLocation(vm, serverId);
        if (source is not { } src) return;

        if (!ReferenceEquals(src.List, folder.Servers))
        {
            src.List.RemoveAt(src.Index);
            folder.Servers.Add(src.Profile);
            await vm.SaveLibraryAsync();
        }

        e.Handled = true;
        ClearFolderDropIndicator();
    }

    private void OnFolderHeaderDragLeave(object? sender,
        DragEventArgs e)
    {
        ClearFolderDropIndicator();
    }

    private void OnEmptyFolderDragOver(object? sender,
        DragEventArgs e)
    {
        var hasServerDrag =
            e.DataTransfer is not null &&
            e.DataTransfer.Items.Any(item => item.Formats.Contains(ServerIdFormat));

        e.DragEffects = hasServerDrag ? DragDropEffects.Move : DragDropEffects.None;

        if (hasServerDrag && sender is Border border)
            SetFolderDropIndicator(border);
        else
            ClearFolderDropIndicator();

        e.Handled = true;
    }

    private void OnEmptyFolderDragLeave(object? sender,
        DragEventArgs e)
    {
        ClearFolderDropIndicator();
    }

    private async void OnEmptyFolderDrop(object? sender,
        DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Border border) return;
        if (border.Tag is not ServerFolder folder) return;

        var serverId = TryGetDraggedServerId(e.DataTransfer);
        if (serverId is null) return;

        var source = FindServerLocation(vm, serverId);
        if (source is not { } src) return;

        if (!ReferenceEquals(src.List, folder.Servers))
        {
            src.List.RemoveAt(src.Index);
            folder.Servers.Add(src.Profile);
            await vm.SaveLibraryAsync();
        }

        e.Handled = true;
        ClearFolderDropIndicator();
    }

    private void SetFolderDropIndicator(Border border)
    {
        if (_folderDropTarget == border)
            return;

        ClearFolderDropIndicator();
        _folderDropTarget = border;
        border.Classes.Add("FolderDropTarget");
    }

    private void ClearFolderDropIndicator()
    {
        if (_folderDropTarget is not null)
            _folderDropTarget.Classes.Remove("FolderDropTarget");
        _folderDropTarget = null;
    }

    private static System.Collections.ObjectModel.ObservableCollection<ServerProfile>? GetServerList(
        ListBox listBox,
        MainWindowViewModel vm)
    {
        if (listBox.Tag is ServerFolder folder)
            return folder.Servers;

        return vm.RootServers;
    }


    private static (System.Collections.ObjectModel.ObservableCollection<ServerProfile> List, ServerProfile Profile, int
        Index)? FindServerLocation(
            MainWindowViewModel vm,
            string serverId)
    {
        var index = vm.RootServers.ToList().FindIndex(s => s.Id == serverId);
        if (index >= 0)
            return (vm.RootServers, vm.RootServers[index], index);

        foreach (var folder in vm.Folders)
        {
            var folderIndex = folder.Servers.ToList().FindIndex(s => s.Id == serverId);
            if (folderIndex >= 0)
                return (folder.Servers, folder.Servers[folderIndex], folderIndex);
        }

        return null;
    }

    private static string? TryGetDraggedServerId(IDataTransfer? dataTransfer)
    {
        if (dataTransfer is null) return null;
        foreach (var item in dataTransfer.Items)
        {
            if (!item.Formats.Contains(ServerIdFormat)) continue;
            if (item.TryGetRaw(ServerIdFormat) is string id)
                return id;
        }

        return null;
    }

    private sealed class TrustedHostKeyRow(TrustedHostKeyEntry entry)
    {
        public TrustedHostKeyEntry Entry { get; } = entry;
        public string Host { get; } = entry.Host;
        public int Port { get; } = entry.Port;
        public string KeyAlgorithm { get; } = entry.KeyAlgorithm;
        public string FingerprintSha256 { get; } = entry.FingerprintSha256;
        public string FirstSeen { get; } = entry.TrustedAtUtc == default
            ? "-"
            : entry.TrustedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
}
