using Nuuz.Application.DTOs;

namespace Nuuz.Application.Services
{
    public interface IFeedService
    {
        Task<IEnumerable<ArticleSummaryDto>> GetPersonalizedFeedAsync(Guid userId);

        Task<FeedPageDto> GetFeedAsync(
            string firebaseUid,
            int limit = 20,
            string? cursor = null,
            string? mood = null,
            double? blend = null,
            bool overrideMood = false);

        Task SaveAsync(string firebaseUid, string articleId);
        Task UnsaveAsync(string firebaseUid, string articleId);

        // UPDATED: Saved view parity (accept mood/blend/override)
        Task<FeedPageDto> GetSavedAsync(
            string firebaseUid,
            int limit = 20,
            string? cursor = null,
            string? mood = null,
            double? blend = null,
            bool overrideMood = false);

        Task UpdateSaveMetaAsync(string firebaseUid, string articleId, List<string>? collections, string? note);
    }
}
