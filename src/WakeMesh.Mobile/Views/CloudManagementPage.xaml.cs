using System;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.Views;

public partial class CloudManagementPage : ContentPage
{
    private readonly CloudManagementViewModel _viewModel;
    private bool _resumedHooked;

    public CloudManagementPage(CloudManagementViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        BindingContext = _viewModel;

        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object? sender, EventArgs e)
    {
        if (!_resumedHooked && Window != null)
        {
            Window.Resumed += OnWindowResumed;
            _resumedHooked = true;
        }
    }

    private void OnWindowResumed(object? sender, EventArgs e)
    {
        // Foreground return automatic sync
        if (_viewModel.IsConfiguredAndSignedIn && _viewModel.IsRegistered)
        {
            _viewModel.ManualSyncCommand.Execute(null);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch
        {
            // Initialization errors are handled in VM StatusMessage
        }
    }
}
