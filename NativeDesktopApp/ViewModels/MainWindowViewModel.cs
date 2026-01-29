// Purpose
//   ViewModel controlling the main window tab navigation and active sub-view model.
//   Keeps track of the selected tab index and updates CurrentViewModel accordingly.
// -----------------------------------------------------------------------------

using NativeDesktopApp.ViewModels;
using System;
using System.Collections.Generic;
using NativeDesktopApp.Views;
using DatabaseAccess;
using NativeDesktopApp.Models;
using RabbitMQHelper;

namespace NativeDesktopApp.ViewModels;

/// <summary>
///     The main window view model that manages active tab navigation.
///     <para>
///         Handles switching between various page ViewModels based on the selected tab index.
///     </para>
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    // Backing field for the currently active ViewModel
    private ViewModelBase? _currentViewModel;

    // Backing field for SelectedTabIndex
    private int _selectedTabIndex;

    public MainWindowViewModel(AppStateModel appStateModel) : base(appStateModel)
    {
        // Default to the Home tab
        SelectedTabIndex = 0;
        UpdateCurrentViewModel();
    }

    /// <summary>
    /// The index of the currently selected tab.
    /// Changing this property triggers a ViewModel switch.
    /// </summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                UpdateCurrentViewModel();
            }
        }
    }

    /// <summary>
    /// The active <see cref="ViewModelBase"/> instance corresponding to the selected tab.
    /// </summary>
    public ViewModelBase? CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    /// <summary>
    /// Updates the <see cref="CurrentViewModel"/> to match the selected tab.
    /// Each tab index corresponds to a specific ViewModel.
    /// </summary>
    private void UpdateCurrentViewModel()
    {
        switch (SelectedTabIndex)
        {
            case 0:
                CurrentViewModel = new HomeViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            case 1:
                CurrentViewModel = new PrintJobsViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            case 2:
                CurrentViewModel = new StaffReviewViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            case 3:
                CurrentViewModel = new PrintersViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            case 4:
                CurrentViewModel = new MessagesViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            case 5:
                CurrentViewModel = new UsersViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            case 6:
                CurrentViewModel = new StatsViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            case 7:
                CurrentViewModel = new ConfigViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            case 8:
                CurrentViewModel = new MaintenanceViewModel(_databaseAccessHelper, _rmqHelper);
                break;
            default:
                CurrentViewModel = new HomeViewModel(_databaseAccessHelper, _rmqHelper);
                break;
        }
    }
}
