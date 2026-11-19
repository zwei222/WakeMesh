using Microsoft.Maui.Controls;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.Views;

public partial class DiagnosticsPage : ContentPage
{
    private readonly DiagnosticsViewModel _viewModel;
    private Window? _observedWindow;

    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshReadiness();
        if (Window is not null && !ReferenceEquals(_observedWindow, Window))
        {
            if (_observedWindow is not null)
                _observedWindow.Resumed -= OnWindowResumed;
            _observedWindow = Window;
            _observedWindow.Resumed += OnWindowResumed;
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_observedWindow is not null)
        {
            _observedWindow.Resumed -= OnWindowResumed;
            _observedWindow = null;
        }
    }

    private void OnWindowResumed(object? sender, EventArgs e) => RefreshReadiness();

    private void RefreshReadiness()
    {
        if (_viewModel.RefreshCommand.CanExecute(null))
            _viewModel.RefreshCommand.Execute(null);
    }

    private async void OnAccountTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

    private void OnToggleDeviceDetails(object? sender, EventArgs e)
    {
        DeviceIdLabel.IsVisible = !DeviceIdLabel.IsVisible;
    }
}
