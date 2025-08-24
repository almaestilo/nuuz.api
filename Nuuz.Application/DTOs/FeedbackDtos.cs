// Nuuz.Application/DTOs/FeedbackDtos.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nuuz.Application.DTOs
{
    // ----- Enums used internally -----
    public enum FeedbackCategory
    {
        Bug,
        FeatureRequest,
        ContentIssue,
        AccountAndBilling,
        SocialConnections,
        Other
    }

    public enum FeedbackSeverity
    {
        Low,
        Normal,
        High,
        Critical
    }

    // ----- What the frontend sends (API-facing request model) -----
    // Accepts human-friendly strings for category/severity and maps server-side.
    // Also accepts EITHER `screenshot` OR `screenshotDataUrl` (legacy + current).
    public class CreateFeedbackRequest
    {
        [JsonPropertyName("category")]
        public string Category { get; set; } = "";   // e.g., "Feature request" or "FeatureRequest"

        [JsonPropertyName("subject")]
        public string Subject { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("rating")]
        public int? Rating { get; set; } = null; // 1..5 optional

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "Normal";

        /// <summary>
        /// Preferred key the current UI should send (camelCase JSON → ScreenshotDataUrl).
        /// e.g., "data:image/png;base64,AAAA..."
        /// </summary>
        [JsonPropertyName("screenshotDataUrl")]
        public string? ScreenshotDataUrl { get; set; } = null;

        /// <summary>
        /// Legacy alias support: if clients post "screenshot", we still accept it.
        /// Setter-only so it won't serialize back out; it just populates ScreenshotDataUrl.
        /// </summary>
        [JsonPropertyName("screenshot")]
        public string? ScreenshotAlias
        {
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                    ScreenshotDataUrl = value;
            }
        }
    }

    // ----- Normalized DTO the service uses -----
    public class CreateFeedbackDto
    {
        [Required]
        public FeedbackCategory Category { get; set; } = FeedbackCategory.Other;

        [Required, StringLength(160, MinimumLength = 1)]
        public string Subject { get; set; } = string.Empty;

        [Required, StringLength(20000, MinimumLength = 10)]
        public string Message { get; set; } = string.Empty;

        [Range(1, 5)]
        public int? Rating { get; set; }

        [Required]
        public FeedbackSeverity Severity { get; set; } = FeedbackSeverity.Normal;

        /// <summary>
        /// Optional screenshot as a data URL (e.g., "data:image/png;base64,AAAA...").
        /// Your service can strip the prefix and decode to bytes if desired.
        /// </summary>
        [StringLength(25_000_000)] // text cap for incoming payload (~25MB raw text)
        public string? ScreenshotDataUrl { get; set; } // normalized name
    }

    // ----- Mapping helpers (tolerant of spaces/case/ampersand) -----
    public static class FeedbackMapping
    {
        public static bool TryParseCategory(string? input, out FeedbackCategory cat)
        {
            cat = FeedbackCategory.Other;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var s = input.Trim().ToLowerInvariant();
            switch (s)
            {
                case "bug":
                    cat = FeedbackCategory.Bug; return true;

                case "feature":
                case "feature request":
                case "featurerequest":
                    cat = FeedbackCategory.FeatureRequest; return true;

                case "content":
                case "content issue":
                case "contentissue":
                    cat = FeedbackCategory.ContentIssue; return true;

                case "account":
                case "billing":
                case "account & billing":
                case "account and billing":
                case "accountandbilling":
                    cat = FeedbackCategory.AccountAndBilling; return true;

                case "social":
                case "social connections":
                case "socialconnections":
                    cat = FeedbackCategory.SocialConnections; return true;

                case "other":
                    cat = FeedbackCategory.Other; return true;

                default:
                    return false;
            }
        }

        public static bool TryParseSeverity(string? input, out FeedbackSeverity sev)
        {
            sev = FeedbackSeverity.Normal;
            if (string.IsNullOrWhiteSpace(input)) return true;

            var s = input.Trim().ToLowerInvariant();
            switch (s)
            {
                case "low": sev = FeedbackSeverity.Low; return true;
                case "normal": sev = FeedbackSeverity.Normal; return true;
                case "high": sev = FeedbackSeverity.High; return true;
                case "critical": sev = FeedbackSeverity.Critical; return true;
                default: return false;
            }
        }

        public static CreateFeedbackDto ToCreateDto(this CreateFeedbackRequest req)
        {
            if (!TryParseCategory(req.Category, out var cat))
                throw new ArgumentException($"Invalid category: '{req.Category}'", nameof(req.Category));

            if (!TryParseSeverity(req.Severity, out var sev))
                throw new ArgumentException($"Invalid severity: '{req.Severity}'", nameof(req.Severity));

            var subject = req.Subject?.Trim() ?? "";
            var message = req.Message?.Trim() ?? "";

            if (subject.Length < 1)
                throw new ArgumentException("Subject is required.", nameof(req.Subject));

            if (message.Length < 10)
                throw new ArgumentException("Message must be at least 10 characters.", nameof(req.Message));

            return new CreateFeedbackDto
            {
                Category = cat,
                Subject = subject,
                Message = message,
                Rating = req.Rating,
                Severity = sev,
                // Already normalized via ScreenshotDataUrl or the alias setter.
                ScreenshotDataUrl = req.ScreenshotDataUrl
            };
        }
    }

    // ----- What we return (and store) -----
    public class FeedbackDto
    {
        public string Id { get; set; } = string.Empty;
        public string UserFirebaseUid { get; set; } = string.Empty;

        public FeedbackCategory Category { get; set; } = FeedbackCategory.Other;
        public FeedbackSeverity Severity { get; set; } = FeedbackSeverity.Normal;

        public string Subject { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int? Rating { get; set; }

        public string? ScreenshotMediaType { get; set; }
        public long? ScreenshotBytes { get; set; }
        public string? ScreenshotUri { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public string? UserAgent { get; set; }
        public string? ClientIp { get; set; }
        public string? AppVersion { get; set; }
        public string? Device { get; set; }
    }
}
