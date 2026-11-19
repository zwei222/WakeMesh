using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WakeMesh.Api.Auth;
using WakeMesh.Api.Models;
using WakeMesh.Api.Services;

namespace WakeMesh.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthPolicies.OwnerOnly)]
[Route("api/v1/devices")]
public sealed class DevicesController : ControllerBase
{
    private readonly DeviceService _devices;
    private readonly DesiredAckService _desired;
    private readonly PushTokenService _pushTokens;

    public DevicesController(DeviceService devices, DesiredAckService desired, PushTokenService pushTokens)
    {
        _devices = devices;
        _desired = desired;
        _pushTokens = pushTokens;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _devices.ListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (status, body) = await _devices.GetAsync(id, ct);
        return StatusCode(status, body);
    }

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] DeviceRegisterRequest request, CancellationToken ct)
    {
        var created = await _devices.RegisterAsync(request ?? new DeviceRegisterRequest(null), ct);
        return Created($"/api/v1/devices/{created.Id}", created);
    }

    /// <summary>Begin graceful retire: empty desired snapshot + tombstones; cancel success ACK → Revoked.</summary>
    [HttpPost("{id:guid}/retire")]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct)
    {
        var (status, body) = await _devices.RetireAsync(id, ct);
        return StatusCode(status, body);
    }

    /// <summary>Immediate revoke. Local cancel remains unconfirmed in owner DTO.</summary>
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var (status, body) = await _devices.RevokeImmediateAsync(id, ct);
        return StatusCode(status, body);
    }

    /// <summary>Register/replace FCM push token. Requires owner JWT + this device's credential.</summary>
    [HttpPut("{id:guid}/push-token")]
    public async Task<IActionResult> PutPushToken(Guid id, [FromBody] PushTokenPutRequest request, CancellationToken ct)
    {
        var credential = Request.Headers["X-Device-Credential"].FirstOrDefault();
        var (status, body) = await _pushTokens.PutAsync(id, credential, request, ct);
        return StatusCode(status, body);
    }

    /// <summary>Clear FCM push token for this device.</summary>
    [HttpDelete("{id:guid}/push-token")]
    public async Task<IActionResult> DeletePushToken(Guid id, CancellationToken ct)
    {
        var credential = Request.Headers["X-Device-Credential"].FirstOrDefault();
        var (status, body) = await _pushTokens.DeleteAsync(id, credential, ct);
        return StatusCode(status, body);
    }

    [HttpGet("{id:guid}/desired")]
    public async Task<IActionResult> Desired(Guid id, CancellationToken ct)
    {
        var credential = Request.Headers["X-Device-Credential"].FirstOrDefault();
        var (status, body) = await _desired.GetDesiredAsync(id, credential, ct);
        return StatusCode(status, body);
    }

    [HttpPost("{id:guid}/acks")]
    public async Task<IActionResult> Ack(Guid id, [FromBody] AckRequest request, CancellationToken ct)
    {
        var credential = Request.Headers["X-Device-Credential"].FirstOrDefault();
        var (status, body) = await _desired.PostAckAsync(id, credential, request, ct);
        return StatusCode(status, body);
    }
}
