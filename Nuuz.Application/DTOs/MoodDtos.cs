namespace Nuuz.Application.DTOs;

public sealed class MoodDto
{
    public string Mood { get; set; } = "Curious";
    public double Blend { get; set; } = 0.3; // 0..1
    public DateTimeOffset SetAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Source { get; set; }
}

public sealed class SetMoodRequest
{
    public string Mood { get; set; } = "Curious";
    public double Blend { get; set; } = 0.3; // 0..1
    public string? Source { get; set; }
}
