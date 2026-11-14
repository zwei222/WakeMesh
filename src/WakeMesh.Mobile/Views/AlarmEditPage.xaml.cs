using System;
using System.Collections.Generic;
using System.ComponentModel;
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

    private void OnSelectWeekly(object? sender, EventArgs e) { _viewModel.IsWeekly = true; UpdateWeekdayLayout(Width); }
    private void OnSelectOnce(object? sender, EventArgs e) { _viewModel.IsWeekly = false; UpdateWeekdayLayout(Width); }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // T99: the weekday block follows the repeat mode even when it changes from outside this page
        // (loading an alarm, the "1回" suggestion). Subscribe for the page's visible lifetime only,
        // and never write the bound properties back from here.
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdateWeekdayLayout(Width);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlarmEditViewModel.IsWeekly))
        {
            UpdateWeekdayLayout(Width);
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateWeekdayLayout(width);
    }

    private void UpdateWeekdayLayout(double width)
    {
        if (WeekdaysWide is null || WeekdaysNarrow is null) return;
        WeekdaysWide.IsVisible = _viewModel.IsWeekly && width >= 480;
        WeekdaysNarrow.IsVisible = _viewModel.IsWeekly && width < 480;
    }

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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_viewModel.IsPlayingPreview)
        {
            _viewModel.TogglePreviewSoundCommand.Execute(null);
        }
    }
}
