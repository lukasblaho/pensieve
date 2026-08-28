#nullable enable
// SeriesKeyGenerator.csx
// Purely mechanical (regex-based, no LLM) normalization of a meeting title into a stable
// "series key" so that recurring meetings (e.g. daily standups, weekly syncs) are recognized as
// the same series even though their titles embed a different date each time. Two titles that
// differ only by date/weekday/trailing numbers normalize to the same key.
//
// Examples:
//   "Daily Standup - Aug 28"      -> "daily standup"
//   "Daily Standup 8/29"          -> "daily standup"
//   "Weekly Sync (Monday)"        -> "weekly sync"
//   "Team Sync 2026-08-28 10:00"  -> "team sync"

using System;
using System.Text.RegularExpressions;

public static class SeriesKeyGenerator
{
    private static readonly string[] Weekdays =
    {
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "mon", "tue", "tues", "wed", "thu", "thur", "thurs", "fri", "sat", "sun",
    };

    private static readonly string[] Months =
    {
        "january", "february", "march", "april", "may", "june", "july", "august", "september",
        "october", "november", "december",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    // ISO date: 2026-08-28 or 2026/08/28
    private static readonly Regex IsoDate = new(@"\b\d{4}[-/]\d{1,2}[-/]\d{1,2}\b", RegexOptions.Compiled);

    // Slash/dotted date: 8/29, 08/29/2026, 8.29
    private static readonly Regex SlashDate = new(@"\b\d{1,2}[/.]\d{1,2}([/.]\d{2,4})?\b", RegexOptions.Compiled);

    // 24h or 12h time: 10:00, 10:00am, 10:00 AM
    private static readonly Regex TimeOfDay = new(@"\b\d{1,2}:\d{2}(\s?[ap]m)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Ordinal day suffixes: 28th, 1st, 2nd, 3rd (kept attached so the following digit-strip step
    // removes the whole token).
    private static readonly Regex OrdinalSuffix = new(@"\b(\d{1,2})(st|nd|rd|th)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Any leftover standalone run of digits (day numbers, years, etc).
    private static readonly Regex StandaloneDigits = new(@"\b\d+\b", RegexOptions.Compiled);

    // Punctuation/symbols collapsed to a single space.
    private static readonly Regex Punctuation = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex WeekdayPattern = new(
        @"\b(" + string.Join("|", Weekdays) + @")\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonthPattern = new(
        @"\b(" + string.Join("|", Months) + @")\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Normalizes a meeting title into a stable, lowercase series key with dates/times/weekdays/
    /// month names/standalone numbers/punctuation stripped, so recurring meetings with the same
    /// underlying name group together regardless of the date embedded in each occurrence's title.
    /// Returns an empty string for a null/blank/fully-numeric-or-date title (never guessed).
    /// </summary>
    public static string Generate(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        var s = title.Trim().ToLowerInvariant();

        s = IsoDate.Replace(s, " ");
        s = TimeOfDay.Replace(s, " ");
        s = SlashDate.Replace(s, " ");
        s = OrdinalSuffix.Replace(s, " ");
        s = WeekdayPattern.Replace(s, " ");
        s = MonthPattern.Replace(s, " ");
        s = StandaloneDigits.Replace(s, " ");
        s = Punctuation.Replace(s, " ");
        s = MultiSpace.Replace(s, " ").Trim();

        return s;
    }
}
