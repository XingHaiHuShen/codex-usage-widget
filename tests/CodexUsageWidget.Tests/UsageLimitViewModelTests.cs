using CodexUsageWidget.Domain;
using CodexUsageWidget.Views.ViewModels;

namespace CodexUsageWidget.Tests;

public sealed class UsageLimitViewModelTests
{
    private static readonly DateTimeOffset Reset = new(2030, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10_080, 0)]
    [InlineData(5_040, 50)]
    [InlineData(2_520, 75)]
    [InlineData(0, 100)]
    [InlineData(20_160, 0)]
    public void ReferenceUsesElapsedQuotaCycle(double minutesUntilReset, double expected)
    {
        var model = Create(45, Reset.AddMinutes(-minutesUntilReset));

        Assert.True(model.HasReference);
        Assert.Equal(expected, model.ReferencePercent);
        Assert.Equal($"Reference {expected}%", model.ReferenceText);
    }

    [Fact]
    public void ReferenceUsesAbsoluteTimeAcrossOffsets()
    {
        var model = Create(45, Reset.AddDays(-3.5).ToOffset(TimeSpan.FromHours(8)));

        Assert.Equal(50, model.ReferencePercent);
    }

    [Theory]
    [InlineData(25, 25, 25, 50)]
    [InlineData(75, 50, 25, 25)]
    [InlineData(50, 50, 0, 50)]
    [InlineData(-10, 0, 50, 50)]
    [InlineData(110, 50, 50, 0)]
    public void GapSpansOnlyTheIntervalBetweenReferenceAndActual(
        double used, double leading, double width, double trailing)
    {
        var model = Create(used, Reset.AddDays(-3.5));

        Assert.Equal(leading, model.GapLeading.Value);
        Assert.Equal(width, model.GapWidth.Value);
        Assert.Equal(trailing, model.GapTrailing.Value);
        Assert.True(model.GapWidth.IsStar);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(300L)]
    public void OtherOrMissingDurationsHaveNoReference(long? duration)
    {
        var model = new UsageLimitViewModel("Limit", new UsageWindow("Limit", 45, duration, Reset),
            includeWeeklyReference: true, now: Reset.AddDays(-3));

        Assert.False(model.HasReference);
    }

    [Fact]
    public void MissingOrExpiredResetAndDisabledReferenceAreHidden()
    {
        Assert.False(Create(45, Reset.AddSeconds(1)).HasReference);
        Assert.False(new UsageLimitViewModel("Weekly", new UsageWindow("Weekly", 45, 10_080, null),
            includeWeeklyReference: true).HasReference);
        Assert.False(new UsageLimitViewModel("Weekly", new UsageWindow("Weekly", 45, 10_080, Reset),
            now: Reset.AddDays(-3)).HasReference);
        Assert.False(Create(double.NaN, Reset.AddDays(-3)).HasReference);
    }

    private static UsageLimitViewModel Create(double used, DateTimeOffset now) => new(
        "Weekly", new UsageWindow("Weekly", used, 10_080, Reset),
        includeWeeklyReference: true, now: now);
}
