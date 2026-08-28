#nullable enable
// test-StateStore.csx
// Verifies per-meeting idempotency tracking: change detection via content hash, independent
// delete/export status flags, and atomic persistence of data/state.json.

#load "TestKit.csx"
#load "../src/StateStore.csx"

using System;
using System.IO;

TestKit.Section("StateStore: first run has nothing processed");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-state-{Guid.NewGuid()}.json");
    var store = new StateStore(tempPath);

    TestKit.Assert(store.GetRecord("some-key") == null, "no record should exist on first run");
    TestKit.Assert(!store.IsUpToDate("some-key", "hash1"), "nothing should be considered up to date on first run");

    File.Delete(tempPath);
}

TestKit.Section("StateStore: upserting a record persists it and marks it up to date for the same hash");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-state-{Guid.NewGuid()}.json");
    var store = new StateStore(tempPath);

    store.UpsertRecord(new MeetingRecord { SourceKey = "file1.md", ContentHash = "hash-a", Analyzed = true, MeetingFolder = "/out/meeting1" });

    TestKit.Assert(store.IsUpToDate("file1.md", "hash-a"), "should be up to date when the content hash matches the last analyzed hash");
    TestKit.Assert(!store.IsUpToDate("file1.md", "hash-b"), "should NOT be up to date when the content hash has changed (file was edited)");
    TestKit.Assert(File.Exists(tempPath), "state file should have been written to disk");

    var reloaded = new StateStore(tempPath);
    TestKit.Assert(reloaded.IsUpToDate("file1.md", "hash-a"), "reloaded store should retain the up-to-date status across restarts");

    File.Delete(tempPath);
}

TestKit.Section("StateStore: delete/export status flags are tracked independently per meeting");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-state-{Guid.NewGuid()}.json");
    var store = new StateStore(tempPath);

    store.UpsertRecord(new MeetingRecord { SourceKey = "fireflies:abc", ContentHash = "abc", Analyzed = true, FirefliesId = "abc" });

    var before = store.GetRecord("fireflies:abc")!;
    TestKit.Assert(!before.DeletedFromFireflies && !before.ObsidianExported && !before.NotionExported, "all optional-step flags should start false");

    store.MarkDeleted("fireflies:abc");
    store.MarkObsidianExported("fireflies:abc");

    var after = store.GetRecord("fireflies:abc")!;
    TestKit.Assert(after.DeletedFromFireflies, "DeletedFromFireflies should be true after MarkDeleted");
    TestKit.Assert(after.ObsidianExported, "ObsidianExported should be true after MarkObsidianExported");
    TestKit.Assert(!after.NotionExported, "NotionExported should remain false (was never marked)");

    File.Delete(tempPath);
}

TestKit.Section("StateStore: LastProcessedDate tracks the Fireflies API resume point");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-state-{Guid.NewGuid()}.json");
    var store = new StateStore(tempPath);

    TestKit.Assert(store.LastProcessedDate == null, "should start null");
    store.LastProcessedDate = 5000;
    TestKit.Assert(store.LastProcessedDate == 5000, "should persist the last processed date");

    var reloaded = new StateStore(tempPath);
    TestKit.Assert(reloaded.LastProcessedDate == 5000, "reloaded store should retain last processed date");

    File.Delete(tempPath);
}

TestKit.Section("StateStore: GetRecordByMeetingId looks up a record by its stable meetingId, independent of sourceKey");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-state-{Guid.NewGuid()}.json");
    var store = new StateStore(tempPath);

    store.UpsertRecord(new MeetingRecord { SourceKey = "fireflies:abc", ContentHash = "abc", Analyzed = true, MeetingId = "meeting-123" });

    var found = store.GetRecordByMeetingId("meeting-123");
    TestKit.Assert(found != null && found.SourceKey == "fireflies:abc", "should find the record by meetingId regardless of its sourceKey");
    TestKit.Assert(store.GetRecordByMeetingId("no-such-id") == null, "should return null for an unknown meetingId");

    File.Delete(tempPath);
}

TestKit.Section("StateStore: MarkNotionExported persists the returned Notion page id");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-state-{Guid.NewGuid()}.json");
    var store = new StateStore(tempPath);

    store.UpsertRecord(new MeetingRecord { SourceKey = "file1.md", ContentHash = "hash-a", Analyzed = true, MeetingId = "meeting-1" });
    store.MarkNotionExported("file1.md", "notion-page-abc");

    var record = store.GetRecord("file1.md")!;
    TestKit.Assert(record.NotionExported, "NotionExported should be true");
    TestKit.Assert(record.NotionPageId == "notion-page-abc", "the returned Notion page id should be persisted");

    var byMeetingId = store.GetRecordByMeetingId("meeting-1")!;
    TestKit.Assert(byMeetingId.NotionPageId == "notion-page-abc", "the page id should also be retrievable via GetRecordByMeetingId");

    File.Delete(tempPath);
}
