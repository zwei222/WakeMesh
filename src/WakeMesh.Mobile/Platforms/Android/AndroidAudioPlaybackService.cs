#if ANDROID
using System;
using System.Threading.Tasks;
using Android.Content;
using Android.Media;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class AndroidAudioPlaybackService : IAudioPlaybackService, IDisposable
{
    private MediaPlayer? _mediaPlayer;
    private readonly object _lock = new();

    public bool IsPlaying { get; private set; }

    public Task PlayPreviewAsync(string? soundUri, int volumePercent)
    {
        lock (_lock)
        {
            StopPreviewInternal();

            try
            {
                var context = global::Android.App.Application.Context;
                _mediaPlayer = new MediaPlayer();

                var audioBuilder = new AudioAttributes.Builder();
                audioBuilder.SetUsage(AudioUsageKind.Alarm);
                audioBuilder.SetContentType(AudioContentType.Sonification);
                _mediaPlayer.SetAudioAttributes(audioBuilder.Build());

                bool dataSourceSet = false;

                if (SoundOption.FromUri(soundUri).Id == SoundOption.BuiltinChimeId)
                {
                    try
                    {
                        var afd = context.Assets?.OpenFd("wakemesh_chime.wav");
                        if (afd != null)
                        {
                            _mediaPlayer.SetDataSource(afd.FileDescriptor, afd.StartOffset, afd.Length);
                            afd.Close();
                            dataSourceSet = true;
                        }
                    }
                    catch
                    {
                        // Fall back to ringtone manager
                    }
                }

                if (!dataSourceSet)
                {
                    var defaultUri = RingtoneManager.GetDefaultUri(RingtoneType.Alarm)
                                     ?? RingtoneManager.GetDefaultUri(RingtoneType.Notification);
                    if (defaultUri != null)
                    {
                        _mediaPlayer.SetDataSource(context, defaultUri);
                        dataSourceSet = true;
                    }
                }

                if (dataSourceSet)
                {
                    _mediaPlayer.Looping = true;
                    _mediaPlayer.Prepare();
                    float vol = Math.Clamp(volumePercent / 100f, 0.05f, 1.0f);
                    _mediaPlayer.SetVolume(vol, vol);
                    _mediaPlayer.Start();
                    IsPlaying = true;
                }
            }
            catch
            {
                StopPreviewInternal();
            }
        }

        return Task.CompletedTask;
    }

    public Task StopPreviewAsync()
    {
        lock (_lock)
        {
            StopPreviewInternal();
        }
        return Task.CompletedTask;
    }

    private void StopPreviewInternal()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                if (_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Stop();
                }
                _mediaPlayer.Release();
                _mediaPlayer = null;
            }
        }
        catch
        {
            // Ignore teardown errors
        }
        finally
        {
            IsPlaying = false;
        }
    }

    public void Dispose()
    {
        StopPreviewInternal();
    }
}
#endif
