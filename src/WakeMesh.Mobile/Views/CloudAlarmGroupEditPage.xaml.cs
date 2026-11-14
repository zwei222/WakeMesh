using System;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.Views;

[QueryProperty(nameof(GroupId), "groupId")]
public partial class CloudAlarmGroupEditPage : ContentPage
{
    private readonly CloudAlarmGroupEditViewModel _viewModel;
    private string? _groupId;

    public string? GroupId
    {
        get => _groupId;
        set => _groupId = Uri.UnescapeDataString(value ?? string.Empty);
    }

    public CloudAlarmGroupEditPage(CloudAlarmGroupEditViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.InitializeAsync(GroupId);
        }
        catch
        {
            // Error handled in ViewModel
        }
    }
}
