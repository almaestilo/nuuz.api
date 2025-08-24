using System;
using System.Collections.Generic;

namespace Nuuz.Application.DTOs
{
    public sealed class PulseTodayDto
    {
        public string Date { get; set; } = "";          // YYYY-MM-DD (app timezone)
        public int CurrentHour { get; set; }            // 0..23 (app timezone)
        public DateTimeOffset UpdatedAt { get; set; }   // last write (UTC)

        public List<PulseItemDto> Global { get; set; } = new();
        public List<PulseItemDto> Personal { get; set; } = new(); // NEW

        public List<PulseTimelineHourDto> Timeline { get; set; } = new();
    }

    public sealed class PulseItemDto
    {
        public string ArticleId { get; set; } = "";
        public string Title { get; set; } = "";
        public string SourceId { get; set; } = "";
        public DateTimeOffset PublishedAt { get; set; }
        public string? Summary { get; set; }
        public string? ImageUrl { get; set; }

        public double Heat { get; set; }                 // 0..1
        public string Trend { get; set; } = "STEADY";    // NEW|UP|DOWN|STEADY|FADE
        public string[] Reasons { get; set; } = Array.Empty<string>();
        public string[] Topics { get; set; } = Array.Empty<string>();

        // Optional passthroughs (safe to ignore on UI)
        public double? ScoreGlobal { get; set; }
        public double? ScorePersonal { get; set; }

        public bool Saved { get; set; }

    }

    public sealed class PulseTimelineHourDto
    {
        public int Hour { get; set; }
        public int Count { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } // UTC
    }
}
