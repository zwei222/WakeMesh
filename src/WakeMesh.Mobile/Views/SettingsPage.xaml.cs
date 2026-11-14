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
            return;

        _initialized = true;
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("設定を読み込めません", ex.Message, "閉じる");
        }
    }

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "サインアウトしますか",
            "この端末の接続情報も削除されます。再びクラウドを使うには、サインインと端末登録の確認が必要になる場合があります。この端末のアラームは削除されません。",
            "サインアウト",
            "キャンセル");
        if (confirmed && _viewModel.SignOutCommand.CanExecute(null))
            _viewModel.SignOutCommand.Execute(null);
    }

    private async void OnClearSettingsClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "接続設定を消去しますか",
            "接続先の設定と、この端末の接続情報が削除されます。アラームは削除されません。",
            "消去する",
            "キャンセル");
        if (confirmed && _viewModel.ClearSettingsCommand.CanExecute(null))
            _viewModel.ClearSettingsCommand.Execute(null);
    }

    private void OnToggleTechnicalDetails(object? sender, EventArgs e)
    {
        TechnicalDetails.IsVisible = !TechnicalDetails.IsVisible;
    }
}
