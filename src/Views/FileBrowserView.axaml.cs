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
using System.ComponentModel;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace NoBSSftp.Views;

public partial class FileBrowserView : UserControl
{
    private SessionViewModel? _viewModel;
    private Grid? _rootGrid;
    private GridLength _cachedFileRowHeight = new(1, GridUnitType.Star);
    private GridLength _cachedSplitterRowHeight = GridLength.Auto;
    private GridLength _cachedTerminalRowHeight = GridLength.Auto;

    public FileBrowserView()
    {
        InitializeComponent();
        _rootGrid = this.FindControl<Grid>("RootGrid");
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
        UpdateTerminalRows(true);
    }

    private static readonly DataFormat<string> InternalPathFormat =
        DataFormat.CreateStringApplicationFormat("NoBSSftp.InternalPath");

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
    }

    private Point? _dragStartPoint;
    private Control? _dragSource;

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is FileEntry)
        {
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
            if (sender is Control control && control.DataContext is FileEntry targetEntry && targetEntry.IsDirectory && targetEntry.Name != "..")
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
        }
        
        e.DragEffects = DragDropEffects.None;
    }

    private async void OnRowDrop(object? sender, DragEventArgs e)
    {
        var sourcePath = TryGetInternalPath(e.DataTransfer);
        if (!string.IsNullOrEmpty(sourcePath) && sender is Control control && control.DataContext is FileEntry targetEntry && targetEntry.IsDirectory)
        {
            if (DataContext is SessionViewModel vm)
            {
                // Dest path is implicit: we are moving INTO targetEntry folder.
                var destFolderPath = vm.CurrentPath.EndsWith("/") ? vm.CurrentPath + targetEntry.Name : $"{vm.CurrentPath}/{targetEntry.Name}";
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
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DragOverlay.IsVisible = false;
        
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as SessionViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdateTerminalRows(_viewModel?.IsTerminalVisible ?? true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsTerminalVisible))
            UpdateTerminalRows(_viewModel?.IsTerminalVisible ?? true);
    }

    private void UpdateTerminalRows(bool isVisible)
    {
        if (_rootGrid is null || _rootGrid.RowDefinitions.Count < 5)
            return;

        var fileRow = _rootGrid.RowDefinitions[2];
        var splitterRow = _rootGrid.RowDefinitions[3];
        var terminalRow = _rootGrid.RowDefinitions[4];

        if (isVisible)
        {
            fileRow.Height = _cachedFileRowHeight;
            splitterRow.Height = _cachedSplitterRowHeight;
            terminalRow.Height = _cachedTerminalRowHeight;
            return;
        }

        _cachedFileRowHeight = fileRow.Height;
        _cachedSplitterRowHeight = splitterRow.Height;
        _cachedTerminalRowHeight = terminalRow.Height;

        fileRow.Height = new GridLength(1, GridUnitType.Star);
        splitterRow.Height = new GridLength(0);
        terminalRow.Height = new GridLength(0);
    }
}
