using System;
using System.Collections.Generic;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.Views;

public partial class AlarmEditPage : ContentPage, IQueryAttributable
{
    private readonly AlarmEditViewModel _viewModel;

    public AlarmEditPage(AlarmEditViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

        _viewModel.OnSaveSuccess = async () =>
        {
            await Shell.Current.GoToAsync("..");
        };
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("Alarm", out var value) && value is LocalAlarmModel alarm)
        {
            _viewModel.LoadAlarm(alarm);
        }
        else
        {
            _viewModel.LoadAlarm(null);
        }
    }

    private void OnSelectWeekly(object? sender, EventArgs e) => _viewModel.IsWeekly = true;
    private void OnSelectOnce(object? sender, EventArgs e) => _viewModel.IsOnce = true;

    private void OnToggleMonday(object? sender, EventArgs e) => _viewModel.RepeatMonday = !_viewModel.RepeatMonday;
    private void OnToggleTuesday(object? sender, EventArgs e) => _viewModel.RepeatTuesday = !_viewModel.RepeatTuesday;
    private void OnToggleWednesday(object? sender, EventArgs e) => _viewModel.RepeatWednesday = !_viewModel.RepeatWednesday;
    private void OnToggleThursday(object? sender, EventArgs e) => _viewModel.RepeatThursday = !_viewModel.RepeatThursday;
    private void OnToggleFriday(object? sender, EventArgs e) => _viewModel.RepeatFriday = !_viewModel.RepeatFriday;
    private void OnToggleSaturday(object? sender, EventArgs e) => _viewModel.RepeatSaturday = !_viewModel.RepeatSaturday;
    private void OnToggleSunday(object? sender, EventArgs e) => _viewModel.RepeatSunday = !_viewModel.RepeatSunday;

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_viewModel.IsPlayingPreview)
        {
            _viewModel.TogglePreviewSoundCommand.Execute(null);
        }
    }
}
