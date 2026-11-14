using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WakeMesh.Api.Auth;
using WakeMesh.Api.Models;
using WakeMesh.Api.Services;

namespace WakeMesh.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthPolicies.OwnerOnly)]
[Route("api/v1/alarm-groups")]
public sealed class AlarmGroupsController : ControllerBase
{
    private readonly AlarmGroupService _groups;

    public AlarmGroupsController(AlarmGroupService groups)
    {
        _groups = groups;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _groups.ListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AlarmGroupWriteRequest request, CancellationToken ct)
    {
        var (status, body) = await _groups.CreateAsync(request, ct);
        return StatusCode(status, body);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] AlarmGroupWriteRequest request, CancellationToken ct)
    {
        long? ifMatch = null;
        if (Request.Headers.TryGetValue("If-Match", out var values))
        {
            var raw = values.ToString().Trim().Trim('"');
            if (long.TryParse(raw, out var v))
            {
                ifMatch = v;
            }
            else
            {
                return StatusCode(428, new { error = "ifMatchRequired" });
            }
        }

        var (status, body) = await _groups.ReplaceAsync(id, request, ifMatch, ct);
        return StatusCode(status, body);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        long? ifMatch = null;
        if (Request.Headers.TryGetValue("If-Match", out var values))
        {
            var raw = values.ToString().Trim().Trim('"');
            if (long.TryParse(raw, out var v))
            {
                ifMatch = v;
            }
        }

        var (status, body) = await _groups.DeleteAsync(id, ifMatch, ct);
        if (status == 204)
        {
            return NoContent();
        }

        return StatusCode(status, body);
    }
}
