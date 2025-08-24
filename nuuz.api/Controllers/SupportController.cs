using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using System.Security.Claims;

namespace Nuuz.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/support")]
public class SupportController : ControllerBase
{
    private readonly IFeedbackService _feedbackService;

    public SupportController(IFeedbackService feedbackService)
    {
        _feedbackService = feedbackService;
    }

    private string GetFirebaseUid() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new UnauthorizedAccessException("Invalid user ID in token.");

    // Allow reasonably large JSON payloads (data URLs with screenshots)
    [HttpPost("feedback")]
    [RequestSizeLimit(20_000_000)] // ~20MB
    public async Task<IActionResult> PostFeedback([FromBody] CreateFeedbackRequest dto)
    {
        if (dto is null)
            return BadRequest(new { error = "Body is required." });

        try
        {
            var uid = GetFirebaseUid();

            // Map API model -> Application DTO
            var create = dto.ToCreateDto();

            // Optional: tiny guard if screenshot strings get absurdly large
            if (!string.IsNullOrWhiteSpace(create.ScreenshotDataUrl) && create.ScreenshotDataUrl!.Length > 18_000_000)
            {
                return BadRequest(new { error = "Screenshot too large." });
            }

            await _feedbackService.CreateAsync(uid, create);
            return Ok(new { ok = true });
        }
        catch (ArgumentException ex)
        {
            // invalid category/severity or subject/message empty checks
            return BadRequest(new { error = ex.Message });
        }
    }
}
