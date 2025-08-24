namespace Nuuz.Application.DTOs
{
    public enum MoodFeedbackAction
    {
        MoreLikeThis,
        GreatExplainer,
        MoreLaunches,
        TooIntense,
        TooFluffy,
        TooShort,
        NotRelevant
    }

    public sealed class RecordMoodFeedbackDto
    {
        public string ArticleId { get; set; } = string.Empty;
        public string Mood { get; set; } = "Curious"; // Calm|Focused|...
        public MoodFeedbackAction Action { get; set; } = MoodFeedbackAction.MoreLikeThis;
    }
}
