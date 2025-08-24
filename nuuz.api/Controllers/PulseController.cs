using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Infrastructure.Services;
using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/pulse")]
    public sealed class PulseController : ControllerBase
    {
        private readonly IPulseService _pulse;
        private readonly string _timezone;

        public PulseController(IPulseService pulse, IConfiguration cfg)
        {
            _pulse = pulse;
            _timezone = cfg["Pulse:Timezone"] ?? "America/New_York";
        }

        private string Uid() =>
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException();

        // GET /api/pulse/today?override=true&mood=Calm&blend=0.5
        [HttpGet("today")]
        public async Task<ActionResult<PulseTodayDto>> Get(
            [FromQuery] string? mood,
            [FromQuery(Name = "override")] bool overrideMood,
            [FromQuery] double? blend,
            CancellationToken ct)
        {
            var dto = await _pulse.GetTodayAsync(
                timezone: _timezone,
                userFirebaseUid: Uid(),
                mood: overrideMood ? mood : null,
                blendOverride: overrideMood ? blend : null,
                ct: ct);

            return Ok(dto);
        }

        [HttpPost("generate")]
        public async Task<IActionResult> Generate(CancellationToken ct)
        {
            await _pulse.GenerateHourAsync(_timezone, ct: ct);
            return NoContent();
        }
    }
}
