#nullable enable
// MeetingLinker.csx
// Purely mechanical (no LLM) matching over already-indexed meeting entries: computes which past
// meetings are "related" to a given meeting, either because they belong to the same recurring
// series (same normalized title, e.g. daily standups) or because they share enough tags/keywords
// to plausibly be about the same topic/initiative. This never looks at transcript content itself
// — only at each meeting's own already-produced, already-validated title/tags/keywords — so it
// preserves the app's no-cross-meeting-hallucination guarantee.

using System;
using System.Collections.Generic;
using System.Linq;

public static class MeetingLinker
{
    /// <summary>
    /// Finds meetings related to <paramref name="current"/> among <paramref name="candidates"/>
    /// (which may or may not include <paramref name="current"/> itself — it is always excluded
    /// from the result). A candidate is related if it shares the same non-empty series key, or if
    /// its tags+keywords overlap (case-insensitive) with the current meeting's tags+keywords by
    /// at least <paramref name="minSharedTags"/> distinct terms. Results are sorted by date
    /// descending (most recent first; entries with no date sort last) and capped at
    /// <paramref name="maxRelated"/>.
    /// </summary>
    public static List<MeetingIndexEntry> FindRelated(
        MeetingIndexEntry current,
        IEnumerable<MeetingIndexEntry> candidates,
        int minSharedTags,
        int maxRelated)
    {
        var currentTerms = new HashSet<string>(
            (current.Tags ?? new List<string>()).Concat(current.Keywords ?? new List<string>())
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0));

        var hasSeriesKey = !string.IsNullOrWhiteSpace(current.SeriesKey);

        var related = candidates
            .Where(c => c.MeetingId != current.MeetingId)
            .Where(c =>
            {
                var sameSeries = hasSeriesKey && string.Equals(c.SeriesKey, current.SeriesKey, StringComparison.OrdinalIgnoreCase);
                if (sameSeries) return true;

                var candidateTerms = (c.Tags ?? new List<string>()).Concat(c.Keywords ?? new List<string>())
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t.Length > 0)
                    .Distinct();
                var sharedCount = candidateTerms.Count(t => currentTerms.Contains(t));
                return sharedCount >= minSharedTags;
            })
            .OrderByDescending(c => c.DateEpochMs ?? double.MinValue)
            .Take(Math.Max(0, maxRelated))
            .ToList();

        return related;
    }
}
