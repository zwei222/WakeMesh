using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services;

/// <summary>
/// Service for previewing alarm sounds, adjusting volume, and testing audio playback.
/// </summary>
public interface IAudioPlaybackService
{
    bool IsPlaying { get; }
    Task PlayPreviewAsync(string? soundUri, int volumePercent);
    Task StopPreviewAsync();
}
