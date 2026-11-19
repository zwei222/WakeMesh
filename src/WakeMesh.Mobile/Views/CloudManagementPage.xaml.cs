using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.Views;

public partial class CloudManagementPage : ContentPage
{
    private const string RetireOption = "登録を解除";
    private const string RevokeOption = "接続を無効にする";
    private const string EditOption = "編集";
    private const string DeleteOption = "削除";
    private const string CancelOption = "キャンセル";

    private const string ExpandText = "詳細を表示";
    private const string CollapseText = "詳細を閉じる";

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
        if (_viewModel.IsConfiguredAndSignedIn && _viewModel.IsRegistered)
        {
            RunCommandSafely(_viewModel.ManualSyncCommand);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"クラウド情報を読み込めませんでした: {ex.Message}";
        }
    }

    private void OnToggleDeviceDetails(object? sender, EventArgs e)
    {
        DeviceDetails.IsVisible = !DeviceDetails.IsVisible;
        if (sender is Button button)
        {
            button.Text = DeviceDetails.IsVisible ? CollapseText : ExpandText;
        }
    }

    private void OnGroupTapped(object? sender, TappedEventArgs e)
    {
        var group = (sender as BindableObject)?.BindingContext as CloudAlarmGroupItemViewModel;
        if (group == null) return;
        RunCommandSafely(_viewModel.NavigateToEditGroupCommand, group);
    }

    private void OnToggleGroupDetails(object? sender, EventArgs e) =>
        ToggleItemDetails(sender, "GroupDetails");

    private void OnToggleDeviceRowDetails(object? sender, EventArgs e) =>
        ToggleItemDetails(sender, "DeviceRowDetails");

    /// <summary>
    /// The details block lives inside the item template, so it is found by its StyleId within the
    /// row that owns the toggle button instead of an x:Name (names are not reachable per item).
    /// </summary>
    private static void ToggleItemDetails(object? sender, string detailsStyleId)
    {
        if (sender is not Button button || button.Parent is not Layout parent) return;

        var details = parent.Children
            .OfType<VisualElement>()
            .FirstOrDefault(child => string.Equals(child.StyleId, detailsStyleId, StringComparison.Ordinal));
        if (details == null) return;

        details.IsVisible = !details.IsVisible;
        button.Text = details.IsVisible ? CollapseText : ExpandText;
    }

    private async void OnGroupMenuClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: CloudAlarmGroupItemViewModel group }) return;

        var choice = await DisplayActionSheetAsync(group.Label, CancelOption, null, EditOption, DeleteOption);
        if (choice == EditOption)
        {
            RunCommandSafely(_viewModel.NavigateToEditGroupCommand, group);
        }
        else if (choice == DeleteOption)
        {
            await OnGroupDeleteClickedAsync(group);
        }
    }

    private async Task OnGroupDeleteClickedAsync(CloudAlarmGroupItemViewModel group)
    {
        var confirmed = await ConfirmAsync(
            $"「{group.Label}」を削除しますか",
            "アラームを鳴らす端末から、このグループのアラームが外れます（各端末の次回の同期で反映されます）。",
            "削除する");
        if (!confirmed) return;

        RunCommandSafely(_viewModel.DeleteGroupCommand, group);
    }

    private async void OnDeviceMenuClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: CloudDeviceItemViewModel device } button) return;

        // Items the user cannot run are not offered in the sheet at all (§3 integrated menu).
        var options = new List<string>();
        if (device.CanRetire) options.Add(RetireOption);
        if (device.CanRevoke) options.Add(RevokeOption);
        if (options.Count == 0) return;

        var choice = await DisplayActionSheetAsync(device.DisplayName, CancelOption, null, options.ToArray());
        if (choice == RetireOption)
        {
            await OnRetireClickedAsync(button);
        }
        else if (choice == RevokeOption)
        {
            await OnRevokeClickedAsync(button);
        }
    }

    private async Task OnRetireClickedAsync(Button button)
    {
        if (button.CommandParameter is not CloudDeviceItemViewModel device) return;

        var confirmed = await ConfirmAsync(
            $"「{device.DisplayName}」の登録を解除しますか",
            "この端末に、クラウドのアラーム設定を削除するよう依頼します。端末から確認が返るまで「端末での解除待ち」と表示されます。",
            "登録を解除");
        if (!confirmed) return;

        RunCommandSafely(_viewModel.RetireDeviceCommand, device.Id);
    }

    private async Task OnRevokeClickedAsync(Button button)
    {
        if (button.CommandParameter is not CloudDeviceItemViewModel device) return;

        var body = "この端末からの接続はすぐに拒否します。ただし、端末がオフラインのときは、端末に設定済みのアラームを遠隔で止められません。端末のアラーム削除は未確認のままになります。";
        if (device.IsCurrentDevice)
        {
            body += "接続を無効にすると、この端末のクラウド同期も止まります。";
        }

        var confirmed = await ConfirmAsync(
            $"「{device.DisplayName}」の接続を無効にしますか",
            body,
            "接続を無効にする");
        if (!confirmed) return;

        RunCommandSafely(_viewModel.RevokeDeviceCommand, device.Id);
    }

    private async void OnResetRecoveryClicked(object? sender, EventArgs e)
    {
        var confirmed = await ConfirmAsync(
            "登録結果を確認しましたか",
            "この端末がサーバーに登録されていないことを確認してから続けてください。確認が不十分だと、同じ端末が二重に登録されるおそれがあります。",
            "確認済みにする");
        if (!confirmed) return;

        RunCommandSafely(_viewModel.ClearRecoveryCommand);
    }

    private Task<bool> ConfirmAsync(string title, string message, string accept) =>
        DisplayAlertAsync(title, message, accept, CancelOption);

    private void RunCommandSafely(System.Windows.Input.ICommand command, object? parameter = null)
    {
        try
        {
            if (command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"操作に失敗しました: {ex.Message}";
        }
    }
}
