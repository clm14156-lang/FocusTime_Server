using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace FocusTime.Api.Tests;

// Discovered only by the test host; never included in the production assembly.
[ApiController]
[Route("api/v1/test-probe")]
public sealed class ProbeController : ControllerBase
{
    [HttpPost("validation")]
    public IActionResult Validate([FromBody] ValidationProbe body) => Ok(body);

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("secret=do-not-expose");
}

public sealed record ValidationProbe([param: Required] string Name, [param: Range(1, 60)] int Minutes);
