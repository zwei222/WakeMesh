using System;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Thrown when the sync manifest file is unreadable, invalid JSON, or has mismatched binding parameters.
/// Manifest must never be silently treated as empty on corruption.
/// </summary>
public sealed class CorruptedSyncManifestException : Exception
{
    public CorruptedSyncManifestException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
