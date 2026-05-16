using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Avalonia.Shared.Contracts;

namespace Feil.ViewModels;

public partial class DepotSelectionItemViewModel : ObservableObject
{
    [ObservableProperty] private int _depotId;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private bool _isOsSpecific;
    [ObservableProperty] private string _osList = string.Empty;
}

public partial class DepotSelectionDialogViewModel : ObservableObject, IDialogContext
{
    public ObservableCollection<DepotSelectionItemViewModel> Depots { get; } = [];

    [ObservableProperty] private string _gameName = string.Empty;

    public event EventHandler<object?>? RequestClose;

    [RelayCommand]
    private void Confirm()
    {
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(this, false);
    }

    public void Close()
    {
    }
}
