namespace VocabCollector.Core.Library;

public sealed record RoamingDayCount(DateOnly Day, int Count, bool IsFuture = false, int UniqueWords = 0);

public sealed record RoamingStatistics(int WeekReviewed, int MonthReviewed, int TotalWords,
    DateOnly Today, DateOnly WeekStart, DateOnly MonthStart, DateOnly GridStart,
    IReadOnlyList<RoamingDayCount> DayCounts, bool HasLegacyHistory = false, DateOnly? TrackingStartedOn = null)
{
    public double WeekCoverage => TotalWords <= 0 ? 0 : Math.Clamp((double)WeekReviewed / TotalWords, 0, 1);
    public double MonthCoverage => TotalWords <= 0 ? 0 : Math.Clamp((double)MonthReviewed / TotalWords, 0, 1);
}
