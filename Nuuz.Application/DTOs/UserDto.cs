using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.DTOs;
public class UserDto
{
    public string Id { get; set; }
    public string Email { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string FirebaseUid { get; set; } = null!;
    public IEnumerable<InterestDto> Interests { get; set; } = Array.Empty<InterestDto>();
    public IEnumerable<CustomInterestDto> CustomInterests { get; set; } = Array.Empty<CustomInterestDto>();
}
