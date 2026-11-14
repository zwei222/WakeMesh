using System;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.Views;

public partial class AlarmsPage : ContentPage
{
    private readonly AlarmListViewModel _viewModel;

    public AlarmsPage(AlarmListViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.SubscribeRingingEvents();
        await _viewModel.LoadAlarmsAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.UnsubscribeRingingEvents();
    }

    private async void OnAddAlarmClicked(object? sender, EventArgs e)
    {
        // 新規アラーム編集画面へ遷移
        await Shell.Current.GoToAsync(nameof(AlarmEditPage));
    }

    private async void OnEditAlarmClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: LocalAlarmModel alarm })
        {
            var parameters = new Dictionary<string, object>
            {
                { "Alarm", alarm }
            };
            await Shell.Current.GoToAsync(nameof(AlarmEditPage), parameters);
        }
    }

    private async void OnDeleteAlarmClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: LocalAlarmModel alarm })
        {
            bool confirm = await DisplayAlertAsync(
                "アラームの削除",
                $"「{alarm.Name}」を削除してもよろしいですか？",
                "削除",
                "キャンセル");

            if (confirm)
            {
                await _viewModel.DeleteAlarmAsync(alarm);
            }
        }
    }

    private async void OnAlarmToggled(object? sender, ToggledEventArgs e)
    {
        if (sender is Switch { BindingContext: LocalAlarmModel alarm })
        {
            if (alarm.IsEnabled != e.Value)
            {
                alarm.IsEnabled = e.Value;
            }
            await _viewModel.ToggleAlarmAsync(alarm);
        }
    }
}
