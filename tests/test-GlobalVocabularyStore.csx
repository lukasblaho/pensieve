#nullable enable
// test-GlobalVocabularyStore.csx
// Verifies pure aggregation semantics: counting appearances across meetings, idempotency (the
// same meetingId doesn't get double-counted on re-run), and separate tag/keyword tracking.

#load "TestKit.csx"
#load "../src/GlobalVocabularyStore.csx"

using System;
using System.IO;

TestKit.Section("GlobalVocabularyStore: aggregates tags/keywords across meetings with counts and meeting links");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-vocab-{Guid.NewGuid()}.json");
    var store = new GlobalVocabularyStore(tempPath);

    store.AddMeeting("meeting-1", new[] { "release", "planning" }, new[] { "sprint", "release date" });
    store.AddMeeting("meeting-2", new[] { "release" }, new[] { "sprint" });

    var snapshot = store.Snapshot();

    TestKit.Assert(snapshot.Tags["release"].Count == 2, "'release' tag should have count 2 across both meetings");
    TestKit.Assert(snapshot.Tags["release"].MeetingIds.Contains("meeting-1") && snapshot.Tags["release"].MeetingIds.Contains("meeting-2"), "'release' tag should link both meeting ids");
    TestKit.Assert(snapshot.Tags["planning"].Count == 1, "'planning' tag should only appear in meeting-1");
    TestKit.Assert(snapshot.Keywords["sprint"].Count == 2, "'sprint' keyword should have count 2");

    File.Delete(tempPath);
}

TestKit.Section("GlobalVocabularyStore: re-adding the same meetingId does not double-count it");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-vocab-{Guid.NewGuid()}.json");
    var store = new GlobalVocabularyStore(tempPath);

    store.AddMeeting("meeting-1", new[] { "release" }, new[] { "sprint" });
    store.AddMeeting("meeting-1", new[] { "release" }, new[] { "sprint" });

    var snapshot = store.Snapshot();
    TestKit.Assert(snapshot.Tags["release"].Count == 1, "re-processing the same meeting should not inflate the count");

    File.Delete(tempPath);
}

TestKit.Section("GlobalVocabularyStore: is case-insensitive and normalizes whitespace for term matching");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-vocab-{Guid.NewGuid()}.json");
    var store = new GlobalVocabularyStore(tempPath);

    store.AddMeeting("meeting-1", new[] { "Release" }, System.Array.Empty<string>());
    store.AddMeeting("meeting-2", new[] { " release " }, System.Array.Empty<string>());

    var snapshot = store.Snapshot();
    TestKit.Assert(snapshot.Tags.ContainsKey("release") && snapshot.Tags["release"].Count == 2, "tags should be normalized (trimmed, lowercased) so 'Release' and ' release ' merge into one entry");

    File.Delete(tempPath);
}

TestKit.Section("GlobalVocabularyStore: persists to disk and reloads correctly");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-vocab-{Guid.NewGuid()}.json");
    var store = new GlobalVocabularyStore(tempPath);
    store.AddMeeting("meeting-1", new[] { "release" }, new[] { "sprint" });

    TestKit.Assert(File.Exists(tempPath), "vocabulary file should be written to disk");

    var reloaded = new GlobalVocabularyStore(tempPath);
    TestKit.Assert(reloaded.Snapshot().Tags["release"].Count == 1, "reloaded store should retain the aggregated vocabulary");

    File.Delete(tempPath);
}

TestKit.Section("GlobalVocabularyStore: resolves user-maintained aliases to merge misspelled variants into the canonical term");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-vocab-{Guid.NewGuid()}.json");
    File.WriteAllText(tempPath, @"{
  ""keywords"": {},
  ""tags"": {},
  ""aliases"": { ""kytica"": [""kzicka"", ""kytycia""] }
}");

    var store = new GlobalVocabularyStore(tempPath);
    store.AddMeeting("meeting-1", System.Array.Empty<string>(), new[] { "kytica" });
    store.AddMeeting("meeting-2", System.Array.Empty<string>(), new[] { "kzicka" });
    store.AddMeeting("meeting-3", System.Array.Empty<string>(), new[] { "Kytycia" });

    var snapshot = store.Snapshot();
    TestKit.Assert(snapshot.Keywords.ContainsKey("kytica") && snapshot.Keywords["kytica"].Count == 3, "all alias variants should merge into the canonical 'kytica' entry");
    TestKit.Assert(!snapshot.Keywords.ContainsKey("kzicka") && !snapshot.Keywords.ContainsKey("kytycia"), "alias variants should not create their own separate entries");

    File.Delete(tempPath);
}

TestKit.Section("GlobalVocabularyStore: preserves user-edited aliases unchanged across reloads");
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"pensieve-vocab-{Guid.NewGuid()}.json");
    File.WriteAllText(tempPath, @"{
  ""keywords"": {},
  ""tags"": {},
  ""aliases"": { ""budget"": [""budjet""] }
}");

    var store = new GlobalVocabularyStore(tempPath);
    store.AddMeeting("meeting-1", System.Array.Empty<string>(), new[] { "budjet" });

    var reloaded = new GlobalVocabularyStore(tempPath);
    TestKit.Assert(reloaded.Snapshot().Aliases.ContainsKey("budget") && reloaded.Snapshot().Aliases["budget"].Contains("budjet"), "the user's alias map should be preserved verbatim across saves/reloads");
    TestKit.Assert(reloaded.Snapshot().Keywords["budget"].Count == 1, "aliased term should already be merged after reload");

    File.Delete(tempPath);
}

