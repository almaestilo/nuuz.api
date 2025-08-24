using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;

namespace Nuuz.Api.Controllers;
[ApiController]
[Route("api/interests")]
public class InterestsController : ControllerBase
{
    private readonly IInterestService _interestService;

    public InterestsController(IInterestService interestService)
    {
        _interestService = interestService;
    }
    public record CreateInterestDto(string Name);
    [HttpGet]
    public async Task<ActionResult<IEnumerable<InterestDto>>> GetAll()
    {
        var interests = await _interestService.GetAllAsync();
        return Ok(interests);
    }

    [HttpPost]
    public async Task<ActionResult<InterestDto>> Create([FromBody] CreateInterestDto dto)
    {
        var created = await _interestService.CreateAsync(dto.Name); // implement in service/repo
        return CreatedAtAction(nameof(GetAll), null, created);
    }
}
