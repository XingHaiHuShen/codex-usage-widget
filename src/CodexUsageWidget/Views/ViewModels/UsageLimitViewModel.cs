using System.Windows.Media;
using CodexUsageWidget.Application;
using CodexUsageWidget.Domain;
using CodexUsageWidget.Localization;

namespace CodexUsageWidget.Views.ViewModels;

public sealed class UsageLimitViewModel
{
    public UsageLimitViewModel(
        string label,
        UsageWindow window,
        TimeFormatPreference timeFormatPreference = TimeFormatPreference.Automatic,
        bool includeWeeklyReference = false,
        DateTimeOffset? now = null)
    {
        Label = label;
        UsedPercent = window.UsedPercent;
        IsNormal = window.RemainingPercent > 25;
        UsedText = Strings.Format("Usage_UsedPercent", Math.Round(window.UsedPercent));
        RemainingText = Strings.Format(
            "Usage_RemainingPercent",
            Math.Round(window.RemainingPercent));
        ResetText = window.ResetsAt is null
            ? Strings.Get("Usage_ResetUnavailable")
            : UsageTextFormatter.FormatReset(
                window.ResetsAt.Value,
                timeFormatPreference: timeFormatPreference);
        ProgressBrush = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                UsageTextFormatter.ColorForRemaining(window.RemainingPercent)));

        var currentTime = now ?? DateTimeOffset.Now;
        if (includeWeeklyReference && window.WindowDurationMinutes == 10_080 &&
            window.ResetsAt is { } reset && reset >= currentTime &&
            double.IsFinite(window.UsedPercent))
        {
            ReferencePercent = Math.Clamp(
                100d * (1d - (reset - currentTime).TotalMinutes / 10_080d), 0d, 100d);
            ReferenceText = Strings.Format("Usage_ReferencePercent", Math.Round(ReferencePercent.Value));
            var used = Math.Clamp(window.UsedPercent, 0d, 100d);
            GapLeading = new System.Windows.GridLength(
                Math.Min(used, ReferencePercent.Value), System.Windows.GridUnitType.Star);
            GapWidth = new System.Windows.GridLength(
                Math.Abs(used - ReferencePercent.Value), System.Windows.GridUnitType.Star);
            GapTrailing = new System.Windows.GridLength(
                100d - Math.Max(used, ReferencePercent.Value), System.Windows.GridUnitType.Star);
        }
    }

    public string Label { get; }

    public double UsedPercent { get; }

    public bool IsNormal { get; }

    public string UsedText { get; }

    public string RemainingText { get; }

    public string ResetText { get; }

    public double? ReferencePercent { get; }

    public bool HasReference => ReferencePercent.HasValue;

    public string? ReferenceText { get; }

    public System.Windows.GridLength GapLeading { get; }

    public System.Windows.GridLength GapWidth { get; }

    public System.Windows.GridLength GapTrailing { get; }

    public System.Windows.Media.Brush ProgressBrush { get; }
}
