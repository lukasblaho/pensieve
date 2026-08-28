#nullable enable
// test-MeetingIndexStore.csx
// Verifies persistence/idempotency of the mechanical per-meeting index used for related-meeting
// linking (mirrors test-GlobalVocabularyStore.csx style).

#load "TestKit.csx"
#load "../src/MeetingIndexStore.csx"

using System;
using System.IO;
using System.Linq;

TestKit.Section("MeetingIndexStore: adds an entry and retrieves it via All()");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-meetings-index-{Guid.NewGuid()}.json");
    var store = new MeetingIndexStore(tempPath);

    store.AddOrUpdate(new MeetingIndexEntry
    {
        MeetingId = "m1",
        Title = "Daily Standup - Aug 28",
        DateEpochMs = 1000,
        FolderPath = "/notes/m1",
        Tags = new() { "standup" },
        Keywords = new() { "blockers" },
        SeriesKey = "daily standup",
    });

    var all = store.All();
    TestKit.Assert(all.Count == 1, "should contain exactly one entry after one AddOrUpdate call");
    TestKit.Assert(all[0].MeetingId == "m1", "the entry should have the expected meetingId");
    TestKit.Assert(all[0].SeriesKey == "daily standup", "the entry should retain its series key");

    File.Delete(tempPath);
}

TestKit.Section("MeetingIndexStore: re-adding the same meetingId updates in place rather than duplicating");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-meetings-index-{Guid.NewGuid()}.json");
    var store = new MeetingIndexStore(tempPath);

    store.AddOrUpdate(new MeetingIndexEntry { MeetingId = "m1", Title = "Old Title", SeriesKey = "old" });
    store.AddOrUpdate(new MeetingIndexEntry { MeetingId = "m1", Title = "New Title", SeriesKey = "new" });

    var all = store.All();
    TestKit.Assert(all.Count == 1, "re-adding the same meetingId should not create a duplicate entry");
    TestKit.Assert(all[0].Title == "New Title", "the second AddOrUpdate call should overwrite the first");

    File.Delete(tempPath);
}

TestKit.Section("MeetingIndexStore: persists to disk and reloads correctly");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-meetings-index-{Guid.NewGuid()}.json");
    var store = new MeetingIndexStore(tempPath);
    store.AddOrUpdate(new MeetingIndexEntry { MeetingId = "m1", Title = "Weekly Sync", SeriesKey = "weekly sync" });

    TestKit.Assert(File.Exists(tempPath), "index file should be written to disk");

    var reloaded = new MeetingIndexStore(tempPath);
    var all = reloaded.All();
    TestKit.Assert(all.Count == 1 && all[0].MeetingId == "m1", "reloaded store should retain the indexed meeting");

    File.Delete(tempPath);
}

TestKit.Section("MeetingIndexStore: returns an empty list when no index file exists yet");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-meetings-index-missing-{Guid.NewGuid()}.json");
    var store = new MeetingIndexStore(tempPath);

    TestKit.Assert(store.All().Count == 0, "a fresh store with no prior file should start empty");

    if (File.Exists(tempPath)) File.Delete(tempPath);
}
