using Android.Content;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

/// <summary>
/// T196: the ringing screen's background theme. It lives in its own SharedPreferences file so that no
/// existing key (auth settings, alarms, device registration, push token) is touched, and it is read
/// synchronously from the plain ringing Activity without needing a MAUI window or MAUI initialization.
/// Microsoft.Maui.Storage.Preferences is deliberately not used for the same reason.
/// </summary>
public sealed class SharedPreferencesRingingThemeStore : IRingingThemeStore
{
    const string FileName = "wakemesh.ringing";

    /// <summary>
    /// One synchronous read for AlarmAlertActivity.OnCreate. Never throws: a missing value, a value stored
    /// with another type (Java ClassCastException) or an unreadable preferences file all mean "sunrise",
    /// so the ringing screen can never be brought down by the theme.
    /// </summary>
    public static string Read(Context context)
    {
        try
        {
            var prefs = context.GetSharedPreferences(FileName, FileCreationMode.Private);
            return RingingThemes.Normalize(prefs?.GetString(RingingThemes.PreferenceKey, RingingThemes.Sunrise));
        }
        catch (global::Java.Lang.Exception)
        {
            return RingingThemes.Sunrise;
        }
        catch (global::System.Exception)
        {
            return RingingThemes.Sunrise;
        }
    }

    public string Get() => Read(global::Android.App.Application.Context);

    /// <summary>
    /// Small synchronous write. Commit() rather than Apply() because the settings screen must know whether
    /// the value really landed: a false result or an exception is thrown, and the ViewModel keeps the
    /// previous selection and shows the error instead of pretending the theme was saved.
    /// </summary>
    public void Set(string id)
    {
        var prefs = global::Android.App.Application.Context.GetSharedPreferences(FileName, FileCreationMode.Private)
                    ?? throw new global::System.InvalidOperationException($"SharedPreferences '{FileName}' is unavailable.");
        var editor = prefs.Edit()
                     ?? throw new global::System.InvalidOperationException($"SharedPreferences editor for '{FileName}' is unavailable.");
        editor.PutString(RingingThemes.PreferenceKey, RingingThemes.Normalize(id));
        if (!editor.Commit())
        {
            throw new global::System.InvalidOperationException($"Could not persist '{RingingThemes.PreferenceKey}' to '{FileName}'.");
        }
    }
}
