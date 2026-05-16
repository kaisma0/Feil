using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Feil.ViewModels.Pages;

namespace Feil.Views.Pages;

public partial class QueuePage : UserControl
{
    public QueuePage()
    {
        InitializeComponent();

        DragDrop.AddDragEnterHandler(DropZone, OnDragEnter);
        DragDrop.AddDragLeaveHandler(DropZone, OnDragLeave);
        DragDrop.AddDragOverHandler(DropZone, OnDragOver);
        DragDrop.AddDropHandler(DropZone, OnDrop);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            DropZone.Classes.Add("drag-over");
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Remove("drag-over");
    }

    private async void OnAddJobClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Select Job ZIP files",
            FileTypeFilter = [new FilePickerFileType("Job Archive") { Patterns = ["*.zip"] }]
        });

        if (files.Count > 0 && DataContext is QueuePageViewModel vm)
        {
            var paths = files.Select(f => f.Path.LocalPath).ToList();
            await vm.ProcessZipFilesAsync(paths);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
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
        DropZone.Classes.Remove("drag-over");

        if (e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            if (e.DataTransfer.TryGetFiles() is { } files && DataContext is QueuePageViewModel vm)
            {
                var zipPaths = files
                    .Where(f => f.Path.LocalPath.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Path.LocalPath)
                    .ToList();

                if (zipPaths.Count > 0)
                {
                    await vm.ProcessZipFilesAsync(zipPaths);
                }
            }
        }
    }
}
