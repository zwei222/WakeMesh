using Android.App;
using Android.Content;
using Android.Content.Res;
using AColor = Android.Graphics.Color;
using Android.Views;
using Microsoft.Maui.Handlers;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class WakeMeshTimePickerHandler : TimePickerHandler
{
    private static readonly int EnabledState = (int)global::Android.Resource.Attribute.StateEnabled;

    protected override TimePickerDialog CreateTimePickerDialog(int hour, int minute)
    {
        var dialog = base.CreateTimePickerDialog(hour, minute);
        dialog.ShowEvent += OnDialogShown;
        return dialog;
    }

    private void OnDialogShown(object? sender, EventArgs e)
    {
        if (sender is not TimePickerDialog dialog) return;
        dialog.ShowEvent -= OnDialogShown;
        SetButtonColor(dialog.GetButton(-1));
        SetButtonColor(dialog.GetButton(-2));
    }

    private static void SetButtonColor(global::Android.Widget.Button? button)
    {
        if (button is null) return;
        var enabled = AColor.ParseColor("#F2766B");
        var disabled = button.TextColors?.GetColorForState(new[] { -EnabledState }, new AColor(button.CurrentTextColor)) ?? new AColor(button.CurrentTextColor);
        button.SetTextColor(new ColorStateList(
            new[] { new[] { EnabledState }, new[] { -EnabledState } },
            new[] { enabled, disabled }));
    }
}
