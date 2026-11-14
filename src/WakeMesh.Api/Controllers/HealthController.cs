using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WakeMesh.Api.Controllers;

[ApiController]
public sealed class HealthController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/health")]
    public IActionResult Get() => Ok(new { status = "ok" });
}
