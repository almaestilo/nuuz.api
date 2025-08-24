// Nuuz.Infrastructure.Services/FeedbackService.cs
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nuuz.Application.Abstraction;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;

namespace Nuuz.Infrastructure.Services
{
    public class FeedbackService : IFeedbackService
    {
        private readonly IFeedbackRepository _repo;
        private readonly IScreenshotStorage? _storage; // optional

        private static readonly Regex DataUrlRegex =
            new(@"^data:(?<mt>[^;]+);base64,(?<b64>.+)$", RegexOptions.Singleline | RegexOptions.Compiled);

        public FeedbackService(IFeedbackRepository repo, IScreenshotStorage? storage = null)
        {
            _repo = repo;
            _storage = storage;
        }

        public async Task<FeedbackDto> CreateAsync(string firebaseUid, CreateFeedbackDto dto, FeedbackContext? context = null)
        {
            if (dto is null) throw new ArgumentNullException(nameof(dto));
            if (string.IsNullOrWhiteSpace(firebaseUid))
                throw new ArgumentException("Invalid user ID.", nameof(firebaseUid));

            // Normalize & validate
            var feedbackId = Guid.NewGuid().ToString("n");
            var subject = (dto.Subject ?? string.Empty).Trim();
            var message = (dto.Message ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Subject is required.", nameof(dto.Subject));
            if (message.Length < 10)
                throw new ArgumentException("Message must be at least 10 characters.", nameof(dto.Message));

            int? rating = dto.Rating;
            if (rating is < 1 or > 5) rating = null; // clamp to null if out-of-range

            string? mediaType = null;
            long? bytes = null;
            string? screenshotUri = null;

            // Parse optional screenshot data URL
            var dataUrl = dto.ScreenshotDataUrl; // <<— updated to align with CreateFeedbackDto
            if (!string.IsNullOrWhiteSpace(dataUrl))
            {
                var m = DataUrlRegex.Match(dataUrl);
                if (m.Success)
                {
                    mediaType = m.Groups["mt"].Value;
                    try
                    {
                        var raw = Convert.FromBase64String(m.Groups["b64"].Value);
                        bytes = raw.LongLength;

                        if (_storage != null)
                        {
                            screenshotUri = await _storage.SaveAsync(feedbackId, raw, mediaType);
                        }
                        else
                        {
                            // No storage configured → skip storing base64 in Firestore
                            screenshotUri = null;
                        }
                    }
                    catch
                    {
                        // Ignore invalid base64
                        mediaType = null; bytes = null; screenshotUri = null;
                    }
                }
            }

            var entity = new Feedback
            {
                Id = feedbackId,
                UserFirebaseUid = firebaseUid,
                Category = dto.Category.ToString(),
                Severity = dto.Severity.ToString(),
                Subject = subject,
                Message = message,
                Rating = rating,

                ScreenshotMediaType = mediaType,
                ScreenshotBytes = bytes,
                ScreenshotUri = screenshotUri,

                CreatedAtUtc = DateTime.UtcNow,
                UserAgent = context?.UserAgent,
                ClientIp = context?.ClientIp,
                AppVersion = context?.AppVersion,
                Device = context?.Device
            };

            var saved = await _repo.AddAsync(entity);
            return Map(saved);
        }

        public async Task<FeedbackDto?> GetAsync(string id)
        {
            var item = await _repo.GetAsync(id);
            return item is null ? null : Map(item);
        }

        private static FeedbackDto Map(Feedback f) => new()
        {
            Id = f.Id,
            UserFirebaseUid = f.UserFirebaseUid,
            Category = Enum.TryParse<FeedbackCategory>(f.Category, true, out var cat) ? cat : FeedbackCategory.Other,
            Severity = Enum.TryParse<FeedbackSeverity>(f.Severity, true, out var sev) ? sev : FeedbackSeverity.Normal,
            Subject = f.Subject,
            Message = f.Message,
            Rating = f.Rating,
            ScreenshotMediaType = f.ScreenshotMediaType,
            ScreenshotBytes = f.ScreenshotBytes,
            ScreenshotUri = f.ScreenshotUri,
            CreatedAtUtc = f.CreatedAtUtc,
            UserAgent = f.UserAgent,
            ClientIp = f.ClientIp,
            AppVersion = f.AppVersion,
            Device = f.Device
        };

      
    }
}
