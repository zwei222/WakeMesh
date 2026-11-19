using Android.App;
using Android.Content;
using Android.Content.PM;
using Microsoft.Identity.Client;

namespace WakeMesh.Mobile.Platforms.Android;

/// <summary>
/// Continuation activity for MSAL browser-based authentication redirect.
/// Uses the exact compile-time scheme (msal{ClientId}) and host (auth) generated from MSBuild WakeMeshAuthClientId.
/// </summary>
[Activity(Exported = true, NoHistory = true, LaunchMode = LaunchMode.SingleTask)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = CompiledAuthConstants.CompileTimeScheme,
    DataHost = CompiledAuthConstants.CompileTimeHost)]
public class MsalActivity : BrowserTabActivity
{
}
