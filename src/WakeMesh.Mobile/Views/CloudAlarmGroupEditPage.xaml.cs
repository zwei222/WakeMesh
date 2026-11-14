using System;
using System.Threading.Tasks;
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
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"グループ設定を読み込めませんでした: {ex.Message}";
        }
    }

    /// <summary>
    /// Weekday chips throw no state of their own: the day flags are the record, and the chip look
    /// follows the flags through its bindings and triggers. This keeps the existing selection logic
    /// (which the save path reads) unchanged.
    /// </summary>
    private void OnWeekdayClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button) return;

        switch (button.Text)
        {
            case "月":
                _viewModel.DayMonday = !_viewModel.DayMonday;
                break;
            case "火":
                _viewModel.DayTuesday = !_viewModel.DayTuesday;
                break;
            case "水":
                _viewModel.DayWednesday = !_viewModel.DayWednesday;
                break;
            case "木":
                _viewModel.DayThursday = !_viewModel.DayThursday;
                break;
            case "金":
                _viewModel.DayFriday = !_viewModel.DayFriday;
                break;
            case "土":
                _viewModel.DaySaturday = !_viewModel.DaySaturday;
                break;
            case "日":
                _viewModel.DaySunday = !_viewModel.DaySunday;
                break;
        }
    }

    private async void OnReloadClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "サーバーの最新を読み込みますか",
            "入力中の内容は上書きされます。",
            "読み込む",
            "キャンセル");
        if (!confirmed) return;

        RunCommandSafely(_viewModel.ReloadFromServerCommand);
    }

    private void OnRemoveTargetClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: CloudAlarmTargetItemViewModel target }) return;

        try
        {
            if (_viewModel.RemoveTargetCommand.CanExecute(target))
            {
                _viewModel.RemoveTargetCommand.Execute(target);
            }
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"端末を外せませんでした: {ex.Message}";
        }
    }

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
            _viewModel.ErrorMessage = $"操作に失敗しました: {ex.Message}";
        }
    }
}
