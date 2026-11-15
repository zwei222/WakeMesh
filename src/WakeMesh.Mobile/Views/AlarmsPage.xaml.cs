using System;
using System.Collections.Generic;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.ViewModels;
namespace WakeMesh.Mobile.Views;
public partial class AlarmsPage : ContentPage
{
 private readonly AlarmListViewModel _viewModel;
 public AlarmsPage(AlarmListViewModel viewModel){InitializeComponent();BindingContext=_viewModel=viewModel;}
 protected override async void OnAppearing(){base.OnAppearing();_viewModel.SubscribeRingingEvents();_viewModel.SubscribeSyncEvents();_viewModel.RefreshReadiness();await _viewModel.LoadAlarmsAsync();if(Window is not null)Window.Resumed+=OnWindowResumed;}
 protected override void OnDisappearing(){if(Window is not null)Window.Resumed-=OnWindowResumed;base.OnDisappearing();_viewModel.UnsubscribeRingingEvents();_viewModel.UnsubscribeSyncEvents();}
 private void OnWindowResumed(object? sender,EventArgs e)=>_viewModel.RefreshReadiness();
 private async void OnAddAlarmClicked(object? sender,EventArgs e)=>await Shell.Current.GoToAsync(nameof(AlarmEditPage));
 private async void OnEditAlarmClicked(object? sender,EventArgs e){var alarm=(sender as BindableObject)?.BindingContext as LocalAlarmModel;if(alarm is null)return;await Shell.Current.GoToAsync(nameof(AlarmEditPage),new Dictionary<string,object>{{"Alarm",alarm}});}
 private async void OnAlarmMenuClicked(object? sender,EventArgs e){var alarm=(sender as BindableObject)?.BindingContext as LocalAlarmModel;if(alarm is null)return;var action=await DisplayActionSheetAsync("操作","キャンセル",null,"編集","削除");if(action=="編集")await Shell.Current.GoToAsync(nameof(AlarmEditPage),new Dictionary<string,object>{{"Alarm",alarm}});else if(action=="削除"&&await DisplayAlertAsync("アラームの削除",$"「{alarm.Name}」を削除してもよろしいですか？","削除","キャンセル"))await _viewModel.DeleteAlarmAsync(alarm);}
 private async void OnGroupRowTapped(object? sender,EventArgs e){if((sender as BindableObject)?.BindingContext is not GroupAlarmRow row)return;await OpenGroupAsync(row);}
 private async void OnGroupRowMenuClicked(object? sender,EventArgs e){if((sender as BindableObject)?.BindingContext is not GroupAlarmRow row)return;if(!row.CanOpenGroup){await DisplayAlertAsync("このアラームはここでは変更できません","クラウドの画面のアラームグループから変更してください。","OK");return;}var action=await DisplayActionSheetAsync(row.GroupName,"キャンセル",null,"グループを編集");if(action=="グループを編集")await OpenGroupAsync(row);}
 private async System.Threading.Tasks.Task OpenGroupAsync(GroupAlarmRow row){if(!row.CanOpenGroup){await DisplayAlertAsync("このアラームはここでは変更できません","クラウドの画面のアラームグループから変更してください。","OK");return;}if(!_viewModel.CanEditCloudGroups){await DisplayAlertAsync("グループの編集にはサインインが必要です","クラウドの画面からサインインしてください。この端末のアラームはそのまま鳴ります。","OK");return;}await _viewModel.OpenGroupCommand.ExecuteAsync(row);}
 private async void OnAlarmToggled(object? sender,ToggledEventArgs e){if(sender is Switch {BindingContext:LocalAlarmModel alarm})await _viewModel.SetAlarmEnabledFromUserAsync(alarm,e.Value);}
 private async void OnReadinessClicked(object? sender,EventArgs e)=>await Shell.Current.GoToAsync("//DiagnosticsPage");
}