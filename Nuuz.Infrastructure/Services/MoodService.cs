using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;

namespace Nuuz.Infrastructure.Services;

public sealed class MoodService : IMoodService
{
    private readonly IUserMoodRepository _repo;

    public MoodService(IUserMoodRepository repo) => _repo = repo;

    public async Task<MoodDto?> GetAsync(string firebaseUid)
    {
        var m = await _repo.GetAsync(firebaseUid);
        if (m is null) return null;

        return new MoodDto
        {
            Mood = m.Mood,
            Blend = m.Blend,
            SetAt = m.SetAt.ToDateTimeOffset(),
            Source = m.Source
        };
    }

    public async Task<MoodDto> SetAsync(string firebaseUid, SetMoodRequest request)
    {
        // Normalize + clamp
        var mood = string.IsNullOrWhiteSpace(request.Mood) ? "Curious" : request.Mood.Trim();
        var blend = Math.Clamp(request.Blend, 0.0, 1.0);

        var entity = new UserMood
        {
            Id = firebaseUid,
            Mood = mood,
            Blend = blend,
            SetAt = Timestamp.FromDateTime(DateTime.UtcNow.ToUniversalTime()),
            Source = string.IsNullOrWhiteSpace(request.Source) ? null : request.Source!.Trim()
        };

        var saved = await _repo.UpsertAsync(entity);

        return new MoodDto
        {
            Mood = saved.Mood,
            Blend = saved.Blend,
            SetAt = saved.SetAt.ToDateTimeOffset(),
            Source = saved.Source
        };
    }
}
