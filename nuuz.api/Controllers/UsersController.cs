using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Nuuz.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/users/me")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    public UsersController(IUserService userService) => _userService = userService;

    private string GetFirebaseUid() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new UnauthorizedAccessException("Invalid user ID in token.");

    [HttpGet]
    public async Task<ActionResult<UserDto>> GetMe()
    {
        var uid = GetFirebaseUid();
        var userDto = await _userService.GetByFirebaseUidAsync(uid);
        return Ok(userDto);
    }

    public record RegisterDto(string Name);

    // Optional: keep your existing POST to create explicitly.
    [HttpPost]
    public async Task<ActionResult<UserDto>> Register(RegisterDto dto)
    {
        var uid = GetFirebaseUid();
        var created = await _userService.CreateUserAsync(uid, dto.Name);
        return CreatedAtAction(nameof(GetMe), null, created);
    }

    // NEW: Idempotent ensure – OK whether the user existed or not.
    [HttpPost("ensure")]
    public async Task<ActionResult<UserDto>> Ensure(RegisterDto dto)
    {
        var uid = GetFirebaseUid();
        var ensured = await _userService.EnsureUserAsync(uid, dto.Name);
        return Ok(ensured);
    }

    [HttpPost("interests")]
    public async Task<IActionResult> SetSystemInterests([FromBody] IEnumerable<string> interestIds)
    {
        var uid = GetFirebaseUid();
        await _userService.SetUserInterestsAsync(uid, interestIds);
        return NoContent();
    }

    public record CreateCustomInterestDto(string Name);

    [HttpPost("custom-interests")]
    public async Task<ActionResult<CustomInterestDto>> AddCustomInterest([FromBody] CreateCustomInterestDto dto)
    {
        var uid = GetFirebaseUid();
        var created = await _userService.AddCustomInterestAsync(uid, dto.Name);
        return Ok(created);
    }

    [HttpDelete("custom-interests/{id}")]
    public async Task<IActionResult> RemoveCustomInterest(string id)
    {
        var uid = GetFirebaseUid();
        await _userService.RemoveCustomInterestAsync(uid, id);
        return NoContent();
    }
}
