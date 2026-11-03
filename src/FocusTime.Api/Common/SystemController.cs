using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace FocusTime.Api.Common;

[ApiController]
[AllowAnonymous]
[Route("api/v1/system")]
public sealed class SystemController : ControllerBase
{
    [HttpGet("info")]
    [ProducesResponseType<ServiceInfo>(StatusCodes.Status200OK)]
    public ServiceInfo Info() => new("FocusTime_Server", "v1");
}

public sealed record ServiceInfo(string Service, string ApiVersion);
