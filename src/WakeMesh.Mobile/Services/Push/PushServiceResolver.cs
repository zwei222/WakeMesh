using System;
using Microsoft.Extensions.DependencyInjection;

namespace WakeMesh.Mobile.Services.Push;

public static class PushServiceResolver
{
    private static readonly object Lock = new();
    private static IServiceProvider? _currentServices;
    private static Func<IServiceProvider?>? _fallbackResolver;

    /// <summary>
    /// Optional fallback resolver used during cold startup or test runs
    /// when MauiApp UI has not initialized.
    /// </summary>
    public static Func<IServiceProvider?>? FallbackResolver
    {
        get { lock (Lock) return _fallbackResolver; }
        set { lock (Lock) _fallbackResolver = value; }
    }

    public static IServiceProvider? CurrentServices
    {
        get
        {
            lock (Lock)
            {
                if (_currentServices != null)
                {
                    return _currentServices;
                }

                // Cold start recovery: resolve DI container even without Maui Window
                if (_fallbackResolver != null)
                {
                    _currentServices = _fallbackResolver();
                    if (_currentServices != null) return _currentServices;
                }

                try
                {
                    var mauiProgramType = Type.GetType("WakeMesh.Mobile.MauiProgram, WakeMesh.Mobile") 
                                          ?? Type.GetType("WakeMesh.Mobile.MauiProgram");
                    if (mauiProgramType != null)
                    {
                        var method = mauiProgramType.GetMethod("CreateMauiApp", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (method != null)
                        {
                            var app = method.Invoke(null, null);
                            if (app != null)
                            {
                                var prop = app.GetType().GetProperty("Services");
                                if (prop != null)
                                {
                                    _currentServices = prop.GetValue(app) as IServiceProvider;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Contain when running in isolated test suites without MAUI bootstrap
                }

                return _currentServices;
            }
        }
        set
        {
            lock (Lock)
            {
                _currentServices = value;
            }
        }
    }

    public static IServiceProvider? ResolveServices() => CurrentServices;
}
