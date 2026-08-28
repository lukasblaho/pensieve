#nullable enable
// test-MeetingLinker.csx
// Verifies purely mechanical related-meeting matching: same-series grouping, shared-tag/keyword
// threshold matching, self-exclusion, date-descending sort, and cap enforcement.

#load "TestKit.csx"
#load "../src/MeetingIndexStore.csx"
#load "../src/MeetingLinker.csx"

using System;
using System.Collections.Generic;
using System.Linq;

static MeetingIndexEntry Entry(string id, string title, double? date, string seriesKey, List<string>? tags = null, List<string>? keywords = null) => new MeetingIndexEntry
{
    MeetingId = id,
    Title = title,
    DateEpochMs = date,
    SeriesKey = seriesKey,
    Tags = tags ?? new List<string>(),
    Keywords = keywords ?? new List<string>(),
};

TestKit.Section("MeetingLinker: groups meetings by matching series key (recurring standups)");
{
    var current = Entry("m3", "Daily Standup - Aug 30", 3000, "daily standup");
    var candidates = new List<MeetingIndexEntry>
    {
        Entry("m1", "Daily Standup - Aug 28", 1000, "daily standup"),
        Entry("m2", "Daily Standup - Aug 29", 2000, "daily standup"),
        Entry("m4", "Sprint Retro", 2500, "sprint retro"),
    };

    var related = MeetingLinker.FindRelated(current, candidates, minSharedTags: 3, maxRelated: 15);

    TestKit.Assert(related.Count == 2, "should find the two prior standups as related");
    TestKit.Assert(related.Any(r => r.MeetingId == "m1") && related.Any(r => r.MeetingId == "m2"), "both same-series meetings should be included");
    TestKit.Assert(!related.Any(r => r.MeetingId == "m4"), "a differently-series meeting with no tag overlap should not be included");
}

TestKit.Section("MeetingLinker: links differently-titled meetings that share enough tags/keywords");
{
    var current = Entry("m1", "Project Atlas Kickoff", 1000, "project atlas kickoff",
        tags: new() { "atlas", "migration", "database" }, keywords: new() { "postgres" });
    var candidates = new List<MeetingIndexEntry>
    {
        Entry("m2", "Cross-team Sync", 2000, "cross team sync",
            tags: new() { "atlas", "migration" }, keywords: new() { "postgres" }),
        Entry("m3", "Unrelated Chat", 1500, "unrelated chat",
            tags: new() { "atlas" }, keywords: new()),
    };

    var related = MeetingLinker.FindRelated(current, candidates, minSharedTags: 3, maxRelated: 15);

    TestKit.Assert(related.Count == 1 && related[0].MeetingId == "m2", "only the meeting meeting the 3-shared-term threshold should be linked");
}

TestKit.Section("MeetingLinker: never includes the current meeting itself");
{
    var current = Entry("m1", "Daily Standup", 1000, "daily standup");
    var candidates = new List<MeetingIndexEntry> { current, Entry("m2", "Daily Standup", 2000, "daily standup") };

    var related = MeetingLinker.FindRelated(current, candidates, minSharedTags: 3, maxRelated: 15);

    TestKit.Assert(related.Count == 1 && related[0].MeetingId == "m2", "the current meeting must never appear in its own related list");
}

TestKit.Section("MeetingLinker: sorts results by date descending (most recent first)");
{
    var current = Entry("m0", "Daily Standup", 5000, "daily standup");
    var candidates = new List<MeetingIndexEntry>
    {
        Entry("old", "Daily Standup", 1000, "daily standup"),
        Entry("newest", "Daily Standup", 4000, "daily standup"),
        Entry("middle", "Daily Standup", 2000, "daily standup"),
    };

    var related = MeetingLinker.FindRelated(current, candidates, minSharedTags: 3, maxRelated: 15);

    TestKit.Assert(related.Select(r => r.MeetingId).SequenceEqual(new[] { "newest", "middle", "old" }), "related meetings should be ordered most-recent-first");
}

TestKit.Section("MeetingLinker: caps the number of related meetings returned");
{
    var current = Entry("m0", "Daily Standup", 100, "daily standup");
    var candidates = Enumerable.Range(1, 20).Select(i => Entry($"m{i}", "Daily Standup", i, "daily standup")).ToList();

    var related = MeetingLinker.FindRelated(current, candidates, minSharedTags: 3, maxRelated: 5);

    TestKit.Assert(related.Count == 5, $"result should be capped at maxRelated=5, was {related.Count}");
}

TestKit.Section("MeetingLinker: returns an empty list when nothing matches");
{
    var current = Entry("m1", "One-off Meeting", 1000, "one off meeting", tags: new() { "x" });
    var candidates = new List<MeetingIndexEntry> { Entry("m2", "Totally Different", 2000, "totally different", tags: new() { "y" }) };

    var related = MeetingLinker.FindRelated(current, candidates, minSharedTags: 3, maxRelated: 15);

    TestKit.Assert(related.Count == 0, "unrelated meetings should never be linked");
}
