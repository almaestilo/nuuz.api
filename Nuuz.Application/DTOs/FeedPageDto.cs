namespace Nuuz.Application.DTOs
{
    public sealed class FeedPageDto
    {
        public List<ArticleDto> Items { get; set; } = new();
        public string? NextCursor { get; set; }

        // NEW: mood banner helpers
        public bool Tuned { get; set; } = false;
        public List<string> Explanations { get; set; } = new();
    }
}
