using System;
using System.Collections.Generic;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.ViewModels;
namespace WakeMesh.Mobile.Views;
public partial class AlarmsPage : ContentPage
{
 private readonly AlarmListViewModel _viewModel;
 public AlarmsPage(AlarmListViewModel viewModel){InitializeComponent();BindingContext=_viewModel=viewModel;}
 protected override async void OnAppearing(){base.OnAppearing();_viewModel.SubscribeRingingEvents();_viewModel.RefreshReadiness();await _viewModel.LoadAlarmsAsync();if(Window is not null)Window.Resumed+=OnWindowResumed;}
 protected override void OnDisappearing(){if(Window is not null)Window.Resumed-=OnWindowResumed;base.OnDisappearing();_viewModel.UnsubscribeRingingEvents();}
 private void OnWindowResumed(object? sender,EventArgs e)=>_viewModel.RefreshReadiness();
 private async void OnAddAlarmClicked(object? sender,EventArgs e)=>await Shell.Current.GoToAsync(nameof(AlarmEditPage));
 private async void OnEditAlarmClicked(object? sender,EventArgs e){var alarm=(sender as BindableObject)?.BindingContext as LocalAlarmModel;if(alarm is null)return;await Shell.Current.GoToAsync(nameof(AlarmEditPage),new Dictionary<string,object>{{"Alarm",alarm}});}
 private async void OnAlarmMenuClicked(object? sender,EventArgs e){var alarm=(sender as BindableObject)?.BindingContext as LocalAlarmModel;if(alarm is null)return;var action=await DisplayActionSheetAsync("操作","キャンセル",null,"編集","削除");if(action=="編集")await Shell.Current.GoToAsync(nameof(AlarmEditPage),new Dictionary<string,object>{{"Alarm",alarm}});else if(action=="削除"&&await DisplayAlertAsync("アラームの削除",$"「{alarm.Name}」を削除してもよろしいですか？","削除","キャンセル"))await _viewModel.DeleteAlarmAsync(alarm);}
 private async void OnAlarmToggled(object? sender,ToggledEventArgs e){if(sender is Switch {BindingContext:LocalAlarmModel alarm})await _viewModel.SetAlarmEnabledFromUserAsync(alarm,e.Value);}
 private async void OnReadinessClicked(object? sender,EventArgs e)=>await Shell.Current.GoToAsync("//DiagnosticsPage");
}
