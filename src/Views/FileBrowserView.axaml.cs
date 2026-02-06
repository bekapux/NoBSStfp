using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NoBSSftp.Models;
using NoBSSftp.ViewModels;
using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;

namespace NoBSSftp.Views;

public partial class FileBrowserView : UserControl
{
    private SessionViewModel? _observedVm;

    public FileBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private static readonly DataFormat<string> InternalPathFormat =
        DataFormat.CreateStringApplicationFormat("NoBSSftp.InternalPath");

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_observedVm, DataContext))
            return;

        if (_observedVm is not null)
            _observedVm.PropertyChanged -= OnSessionViewModelPropertyChanged;

        _observedVm = DataContext as SessionViewModel;
        if (_observedVm is not null)
            _observedVm.PropertyChanged += OnSessionViewModelPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observedVm is not null)
            _observedVm.PropertyChanged -= OnSessionViewModelPropertyChanged;
        _observedVm = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSessionViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SessionViewModel vm)
            return;

        if (e.PropertyName != nameof(SessionViewModel.ConnectFailureFocusRequestId))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(DataContext, vm) || vm.IsConnected)
                return;

            // On failed password auth, keep keyboard in credential input for quick retry.
            if (!vm.Profile.UsePrivateKey && ConnectPasswordBox.IsVisible)
            {
                ConnectPasswordBox.Focus();
                ConnectPasswordBox.SelectAll();
                return;
            }

            if (ConnectKeyPassphraseBox.IsVisible)
            {
                ConnectKeyPassphraseBox.Focus();
                ConnectKeyPassphraseBox.SelectAll();
                return;
            }

            if (ConnectKeyPathBox.IsVisible)
            {
                ConnectKeyPathBox.Focus();
                ConnectKeyPathBox.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private async void OnDataGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is FileEntry entry)
        {
            if (DataContext is SessionViewModel vm)
            {
                await vm.OpenItem(entry);
            }
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasExternalFiles(e.DataTransfer))
        {
            DragOverlay.IsVisible = true;
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DragOverlay.IsVisible = false;
        var position = e.GetPosition(this);
        if (position.X < 0 || position.Y < 0 || position.X > Bounds.Width || position.Y > Bounds.Height)
        {
            ClearDropTargetHighlight();
        }
    }

    private Point? _dragStartPoint;
    private Control? _dragSource;
    private Control? _highlightedDropTarget;

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is FileEntry entry)
        {
            if (entry.Name == "..")
            {
                _dragStartPoint = null;
                _dragSource = null;
                return;
            }

            var properties = e.GetCurrentPoint(control).Properties;
            if (!properties.IsLeftButtonPressed)
            {
                _dragStartPoint = null;
                _dragSource = null;
                return;
            }

            _dragStartPoint = e.GetPosition(null);
            _dragSource = control;
        }
    }

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartPoint.HasValue && _dragSource is not null)
        {
            var properties = e.GetCurrentPoint(_dragSource).Properties;
            if (!properties.IsLeftButtonPressed)
            {
                _dragStartPoint = null;
                _dragSource = null;
                return;
            }

            var currentPoint = e.GetPosition(null);
            if (Math.Abs(currentPoint.X - _dragStartPoint.Value.X) > 3 ||
                Math.Abs(currentPoint.Y - _dragStartPoint.Value.Y) > 3)
            {
                // Threshold exceeded, start drag
                var source = _dragSource;
                _dragStartPoint = null; // Reset to avoid re-triggering
                _dragSource = null;

                if (source.DataContext is FileEntry fileEntry && DataContext is SessionViewModel vm)
                {
                    if (fileEntry.Name == "..")
                        return;

                    var sourcePath = vm.CurrentPath.EndsWith("/")
                        ? vm.CurrentPath + fileEntry.Name
                        : $"{vm.CurrentPath}/{fileEntry.Name}";
                    var dataTransfer = new DataTransfer();
                    dataTransfer.Add(DataTransferItem.Create(InternalPathFormat, sourcePath));
                    await DragDrop.DoDragDropAsync(e, dataTransfer, DragDropEffects.Move);
                }
            }
        }
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
        _dragSource = null;
    }

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        // Check if we are dragging an internal path
        var sourcePath = TryGetInternalPath(e.DataTransfer);
        if (!string.IsNullOrEmpty(sourcePath))
        {
            // Check if the target is a directory
            if (sender is Control control && control.DataContext is FileEntry targetEntry && targetEntry.IsDirectory)
            {
                SetDropTargetHighlight(control);
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
        }

        ClearDropTargetHighlight();
        e.DragEffects = DragDropEffects.None;
    }

    private async void OnRowDrop(object? sender, DragEventArgs e)
    {
        ClearDropTargetHighlight();

        var sourcePath = TryGetInternalPath(e.DataTransfer);
        if (!string.IsNullOrEmpty(sourcePath) && sender is Control control && control.DataContext is FileEntry targetEntry && targetEntry.IsDirectory)
        {
            if (DataContext is SessionViewModel vm)
            {
                var destFolderPath =
                    targetEntry.Name == ".."
                        ? GetParentRemotePath(vm.CurrentPath)
                        : (vm.CurrentPath.EndsWith("/") ? vm.CurrentPath + targetEntry.Name : $"{vm.CurrentPath}/{targetEntry.Name}");
                await vm.MoveItem(sourcePath, destFolderPath);
            }
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Only allow if dragging files and we are connected (DataContext check logic could go here)
        if (HasExternalFiles(e.DataTransfer))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            // If not external files, and NOT already handled (e.g. by Row for internal move), cancel it.
            if (!e.Handled)
            {
                e.DragEffects = DragDropEffects.None;
            }
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DragOverlay.IsVisible = false;
        ClearDropTargetHighlight();
        
        if (HasExternalFiles(e.DataTransfer))
        {
            var files = GetExternalFiles(e.DataTransfer);
            var paths = files.Select(f => f.Path.LocalPath).ToList();
            if (paths.Count > 0 && DataContext is SessionViewModel vm)
            {
                await vm.UploadFiles(paths);
            }
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is SessionViewModel vm)
        {
            vm.SelectedFiles = grid.SelectedItems;
        }
    }

    private async void OnPickKeyPathClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel vm) return;
        if (this.GetVisualRoot() is not Window window) return;

        var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Private Key",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            vm.Profile.PrivateKeyPath = result[0].Path.LocalPath;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SessionViewModel vm) return;
        if (!FileGrid.IsKeyboardFocusWithin) return;
        var isMac = OperatingSystem.IsMacOS();
        var commandPressed = isMac
            ? e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            : e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var renamePressed = isMac
            ? commandPressed && e.Key == Key.Enter
            : e.Key == Key.F2;
        var deletePressed = isMac
            ? commandPressed && e.Key == Key.Back
            : e.Key == Key.Delete;

        if (commandPressed)
        {
            switch (e.Key)
            {
                case Key.C:
                    vm.CopyCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.X:
                    vm.CutCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.V:
                    vm.PasteCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (renamePressed)
        {
            vm.RenameCommand.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                if (vm.SelectedFile is not null)
                {
                    _ = vm.OpenItem(vm.SelectedFile);
                    e.Handled = true;
                }
                break;
            case Key.Back:
                if (deletePressed)
                {
                    vm.DeleteCommand.Execute(null);
                    e.Handled = true;
                }
                else
                {
                    _ = vm.Navigate("..");
                    e.Handled = true;
                }
                break;
            case Key.Delete:
                if (!deletePressed) break;
                vm.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private static bool HasExternalFiles(IDataTransfer? dataTransfer)
    {
        return dataTransfer?.Items.Any(item => item.Formats.Contains(DataFormat.File)) == true;
    }

    private static IEnumerable<IStorageItem> GetExternalFiles(IDataTransfer? dataTransfer)
    {
        if (dataTransfer is null) return Enumerable.Empty<IStorageItem>();
        return dataTransfer.Items
            .Where(item => item.Formats.Contains(DataFormat.File))
            .Select(item => item.TryGetRaw(DataFormat.File))
            .OfType<IStorageItem>();
    }

    private static string? TryGetInternalPath(IDataTransfer? dataTransfer)
    {
        if (dataTransfer is null) return null;
        foreach (var item in dataTransfer.Items)
        {
            if (!item.Formats.Contains(InternalPathFormat)) continue;
            if (item.TryGetRaw(InternalPathFormat) is string path)
                return path;
        }
        return null;
    }

    private static string GetParentRemotePath(string currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return "/";

        var normalized = currentPath.Trim();
        if (normalized == "/")
            return "/";

        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0)
            return "/";

        var separatorIndex = normalized.LastIndexOf('/');
        if (separatorIndex <= 0)
            return "/";

        return normalized[..separatorIndex];
    }

    private void SetDropTargetHighlight(Control control)
    {
        var target = control.FindAncestorOfType<DataGridRow>() ?? control;

        if (ReferenceEquals(_highlightedDropTarget, target))
            return;

        ClearDropTargetHighlight();
        _highlightedDropTarget = target;
        _highlightedDropTarget.Classes.Add("DropTargetRow");
    }

    private void ClearDropTargetHighlight()
    {
        if (_highlightedDropTarget is null)
            return;

        _highlightedDropTarget.Classes.Remove("DropTargetRow");
        _highlightedDropTarget = null;
    }

}
