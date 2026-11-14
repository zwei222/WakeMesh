using Microsoft.Maui.Controls;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;
    private bool _initialized;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch
        {
            // Keep page usable for local-only operation; status text comes from auth service.
        }
    }
}
