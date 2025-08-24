using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.Abstraction;
using Nuuz.Application.DTOs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/embed")]
    public sealed class EmbedController : ControllerBase
    {
        private readonly IEmbedProbeService _probe;

        public EmbedController(IEmbedProbeService probe)
        {
            _probe = probe;
        }

        [HttpGet("check")]
        public async Task<ActionResult<EmbedCheckResult>> Check([FromQuery] string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url)) return BadRequest("url is required");
            try
            {
                var result = await _probe.CheckAsync(url, ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return Ok(new EmbedCheckResult
                {
                    Embeddable = false,
                    Reason = $"Probe error: {ex.GetType().Name}",
                    FinalUrl = url,
                    StatusCode = 0
                });
            }
        }
    }
}
