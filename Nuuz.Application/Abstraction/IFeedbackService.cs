// Nuuz.Application.Services/IFeedbackService.cs
using Nuuz.Application.DTOs;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    public class FeedbackContext
    {
        public string? UserAgent { get; set; }
        public string? ClientIp { get; set; }
        public string? AppVersion { get; set; }
        public string? Device { get; set; }
    }

    public interface IFeedbackService
    {
        Task<FeedbackDto> CreateAsync(string firebaseUid, CreateFeedbackDto dto, FeedbackContext? context = null);
        Task<FeedbackDto?> GetAsync(string id);
    }
}
