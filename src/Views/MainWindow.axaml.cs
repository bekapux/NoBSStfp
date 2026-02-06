using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NoBSSftp.Models;
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
}
