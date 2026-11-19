using Microsoft.EntityFrameworkCore;
using WakeMesh.Api.Data;
using WakeMesh.Api.Models;

namespace WakeMesh.Api.Services;

public sealed class PushTokenService
{
    private readonly WakeMeshDbContext _db;

    public PushTokenService(WakeMeshDbContext db)
    {
        _db = db;
    }

    public async Task<(int StatusCode, object? Body)> PutAsync(
        Guid deviceId,
        string? credential,
        PushTokenPutRequest? request,
        CancellationToken ct)
    {
        var auth = await AuthenticateAsync(deviceId, credential, ct);
        if (auth.StatusCode != 200)
        {
            return (auth.StatusCode, auth.Body);
        }

        var token = request?.Token?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return (400, new { error = "tokenRequired" });
        }

        if (token.Length > 4096)
        {
            return (400, new { error = "tokenTooLong" });
        }

        // NotificationsCapable defaults false when omitted by older clients.
        var capable = request?.NotificationsCapable ?? false;

        var device = await _db.Devices.FirstAsync(d => d.Id == deviceId, ct);
        device.PushToken = token;
        device.PushTokenUpdatedAt = DateTimeOffset.UtcNow;
        device.PushNotificationsCapable = capable;
        await _db.SaveChangesAsync(ct);

        // Never echo the token back.
        return (200, new PushTokenPutResponse(device.Id, device.PushTokenUpdatedAt.Value));
    }

    public async Task<(int StatusCode, object? Body)> DeleteAsync(
        Guid deviceId,
        string? credential,
        CancellationToken ct)
    {
        var auth = await AuthenticateAsync(deviceId, credential, ct);
        if (auth.StatusCode != 200)
        {
            return (auth.StatusCode, auth.Body);
        }

        var device = await _db.Devices.FirstAsync(d => d.Id == deviceId, ct);
        device.PushToken = null;
        device.PushTokenUpdatedAt = DateTimeOffset.UtcNow;
        device.PushNotificationsCapable = false;
        await _db.SaveChangesAsync(ct);
        return (204, null);
    }

    private async Task<(int StatusCode, object? Body)> AuthenticateAsync(
        Guid deviceId,
        string? credential,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return (401, new { error = "deviceCredentialRequired" });
        }

        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null)
        {
            return (401, new { error = "unauthorized" });
        }

        if (!CredentialHasher.Matches(credential, device.CredentialHash))
        {
            // Wrong device credential (including another device's secret) → unauthorized.
            return (401, new { error = "unauthorized" });
        }

        if (device.Status == DeviceStatus.Revoked)
        {
            return (410, new { error = "deviceRevoked" });
        }

        // Retiring may still clear/update token while cancel is in flight.
        return (200, null);
    }
}
