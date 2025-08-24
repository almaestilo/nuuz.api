using Nuuz.Application.DTOs;
using System.Threading.Tasks;

namespace Nuuz.Application.Services;

public interface IMoodService
{
    Task<MoodDto?> GetAsync(string firebaseUid);
    Task<MoodDto> SetAsync(string firebaseUid, SetMoodRequest request);
}
