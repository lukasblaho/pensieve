#nullable enable
// test-MeetingFolderWriter.csx
// Verifies per-meeting folder creation: verbatim transcript copy, note.md with trimmed YAML
// frontmatter (tags only, no duplication with the body header), Bratislava-local date display,
// camelCase keyword rendering, diagram files (only when diagrams are present), keywords.json,
// and metadata.json (app version + MD5 checksums) — plus that "not specified" fallbacks are
// used, never invented content.

#load "TestKit.csx"
#load "../src/MeetingFolderWriter.csx"
#load "../src/SafeFileName.csx"
#load "../src/Models.csx"
#load "../src/DateTimeHelper.csx"
#load "../src/Version.csx"
#load "../src/MeetingIndexStore.csx"

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

TestKit.Section("SafeFileName: slugifies titles for safe filenames");
{
    TestKit.Assert(SafeFileName.Slugify("Weekly Sync: Q3 Planning!") == "weekly-sync-q3-planning", "should lowercase, strip punctuation, and dash-join words");
    TestKit.Assert(SafeFileName.Slugify(null) == "untitled-meeting", "null title should fall back to 'untitled-meeting'");
}

TestKit.Section("MeetingFolderWriter: builds correct meeting folder path (YYYY-MM-DD--HHmm--slug--id, Bratislava-local)");
{
    var transcript = new Transcript { Id = "xyz789", Title = "Sprint Review", Date = 1700000000000 };
    var path = MeetingFolderWriter.BuildMeetingFolderPath("/tmp/notes", transcript);
    var expectedLocal = DateTimeHelper.ToBratislava(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));

    TestKit.Assert(Path.GetFileName(path).StartsWith($"{expectedLocal:yyyy-MM-dd}--{expectedLocal:HHmm}--sprint-review--xyz789"), "folder name should follow YYYY-MM-DD--HHmm--slug--id pattern using Bratislava-local time");
}

TestKit.Section("MeetingFolderWriter: writes transcript.md verbatim, note.md with trimmed frontmatter, and keywords.json");
{
    var outputDir = Path.Combine(Path.GetTempPath(), $"pensieve-notes-{Guid.NewGuid()}");
    var transcript = new Transcript
    {
        Id = "abc123",
        FirefliesId = "abc123",
        Title = "Weekly Sync",
        Date = 1700000000000,
        TranscriptUrl = "https://app.fireflies.ai/view/abc123",
        Participants = new List<string> { "alice@example.com", "bob@example.com" },
        RawText = "Alice: Let's start.\nBob: Sounds good.",
    };

    var analysis = new TranscriptAnalysis
    {
        Summary = "This is the summary.",
        Agreements = new List<string> { "We will ship on Friday." },
        OpenQuestions = new List<string>(),
        NextActions = new List<ActionItem> { new ActionItem { Task = "Write release notes", Owner = "Bob", Due = "Friday" } },
        Tags = new List<string> { "release planning", "QA" },
        Keywords = new List<string> { "weekly sync", "release" },
    };

    var folder = MeetingFolderWriter.WriteMeetingFolder(outputDir, transcript, analysis);

    TestKit.Assert(Directory.Exists(folder), "meeting folder should be created");

    var transcriptCopy = File.ReadAllText(Path.Combine(folder, "transcript.md"));
    TestKit.Assert(transcriptCopy == transcript.RawText, "transcript.md should be a verbatim copy of the raw transcript text");

    var note = File.ReadAllText(Path.Combine(folder, "note.md"));
    TestKit.Assert(note.StartsWith("---"), "note.md should start with YAML frontmatter");
    TestKit.Assert(note.Contains("tags:") && note.Contains("- releasePlanning") && note.Contains("- qa"), "note.md frontmatter should list tags rendered in camelCase (e.g. 'release planning' -> 'releasePlanning')");

    var frontmatterEnd = note.IndexOf("---", 3, StringComparison.Ordinal);
    var frontmatter = note.Substring(0, frontmatterEnd);
    TestKit.Assert(!frontmatter.Contains("title:") && !frontmatter.Contains("date:") && !frontmatter.Contains("fireflies_id:") && !frontmatter.Contains("fireflies_url:"), "frontmatter should contain ONLY tags — no duplication with the body header");

    TestKit.Assert(note.Contains("# Weekly Sync"), "should include meeting title as H1");
    TestKit.Assert(note.Contains("- **Date:**") && note.Contains("Bratislava"), "body header should include the Bratislava-local date");
    TestKit.Assert(note.Contains("## Summary") && note.Contains("This is the summary."), "should include summary section");
    TestKit.Assert(note.Contains("## Agreements") && note.Contains("We will ship on Friday."), "should include agreements section");
    TestKit.Assert(note.Contains("## Open Questions") && note.Contains("not specified"), "should include open questions section with fallback when empty");
    TestKit.Assert(note.Contains("## Next Actions") && note.Contains("- [ ] Write release notes — owner: Bob, due: Friday"), "should include next actions checklist");
    TestKit.Assert(note.Contains("## Keywords") && note.Contains("weeklySync") && note.Contains("release"), "keywords section should render camelCase (e.g. 'weekly sync' -> 'weeklySync')");

    var keywordsJson = File.ReadAllText(Path.Combine(folder, "keywords.json"));
    using var doc = JsonDocument.Parse(keywordsJson);
    TestKit.Assert(doc.RootElement.GetProperty("tags").GetArrayLength() == 2, "keywords.json should record this meeting's tags");
    TestKit.Assert(doc.RootElement.GetProperty("tags")[0].GetString() == "release planning", "keywords.json should retain the original/canonical (non-camelCase) casing for tags");
    TestKit.Assert(doc.RootElement.GetProperty("keywords").GetArrayLength() == 2, "keywords.json should record this meeting's keywords");
    TestKit.Assert(doc.RootElement.GetProperty("keywords")[0].GetString() == "weekly sync", "keywords.json should retain the original/canonical (non-camelCase) casing");

    TestKit.Assert(!Directory.Exists(Path.Combine(folder, "diagrams")), "diagrams folder should NOT be created when there are no diagrams");

    Directory.Delete(outputDir, recursive: true);
}

TestKit.Section("MeetingFolderWriter: writes metadata.json with app version and correct MD5 checksums");
{
    var outputDir = Path.Combine(Path.GetTempPath(), $"pensieve-notes-{Guid.NewGuid()}");
    var transcript = new Transcript { Id = "meta1", Title = "Metadata Check", RawText = "Alice: hello." };
    var analysis = new TranscriptAnalysis { Summary = "Summary text." };

    var folder = MeetingFolderWriter.WriteMeetingFolder(outputDir, transcript, analysis);
    var metadataJson = File.ReadAllText(Path.Combine(folder, "metadata.json"));
    using var doc = JsonDocument.Parse(metadataJson);

    TestKit.Assert(doc.RootElement.GetProperty("appVersion").GetString() == AppVersion.Current, "metadata.json should record the current app version");
    TestKit.Assert(doc.RootElement.TryGetProperty("generatedAt", out _), "metadata.json should record a generatedAt timestamp");

    string Md5Of(string path)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }

    var expectedNoteMd5 = Md5Of(Path.Combine(folder, "note.md"));
    var expectedTranscriptMd5 = Md5Of(Path.Combine(folder, "transcript.md"));

    TestKit.Assert(doc.RootElement.GetProperty("files").GetProperty("note.md").GetProperty("md5").GetString() == expectedNoteMd5, "metadata.json note.md MD5 should match the actual written file's checksum");
    TestKit.Assert(doc.RootElement.GetProperty("files").GetProperty("transcript.md").GetProperty("md5").GetString() == expectedTranscriptMd5, "metadata.json transcript.md MD5 should match the actual written file's checksum");

    Directory.Delete(outputDir, recursive: true);
}

TestKit.Section("MeetingFolderWriter: writes one file per diagram under diagrams/, only when diagrams are present");
{
    var outputDir = Path.Combine(Path.GetTempPath(), $"pensieve-notes-{Guid.NewGuid()}");
    var transcript = new Transcript { Id = "d1", Title = "Architecture Review", RawText = "Alice: here's the flow." };
    var analysis = new TranscriptAnalysis
    {
        Summary = "Discussed the ingestion pipeline.",
        Diagrams = new List<DiagramItem> { new DiagramItem { Title = "Ingestion Flow", Mermaid = "graph TD; A-->B;" } },
    };

    var folder = MeetingFolderWriter.WriteMeetingFolder(outputDir, transcript, analysis);
    var diagramsDir = Path.Combine(folder, "diagrams");

    TestKit.Assert(Directory.Exists(diagramsDir), "diagrams folder should be created when diagrams are present");
    var diagramFiles = Directory.GetFiles(diagramsDir);
    TestKit.Assert(diagramFiles.Length == 1, "should write exactly one file per diagram");
    var diagramContent = File.ReadAllText(diagramFiles[0]);
    TestKit.Assert(diagramContent.Contains("```mermaid") && diagramContent.Contains("graph TD; A-->B;"), "diagram file should contain a fenced mermaid code block");

    var note = File.ReadAllText(Path.Combine(folder, "note.md"));
    TestKit.Assert(note.Contains("## Diagrams") && note.Contains("Ingestion Flow"), "note.md should link to the generated diagram");

    Directory.Delete(outputDir, recursive: true);
}

TestKit.Section("MeetingFolderWriter: merges speaker timing + LLM quality ratings into metadata.json speakerStatistics");
{
    var outputDir = Path.Combine(Path.GetTempPath(), $"pensieve-notes-{Guid.NewGuid()}");
    var transcript = new Transcript
    {
        Id = "spk1",
        Title = "Speaker Stats Check",
        RawText = "**Speaker 1** *[00:00]*: Hello everyone.\n**Speaker 2** *[00:10]*: Hi there.\n**Speaker 1** *[00:20]*: Let's begin.",
    };
    var analysis = new TranscriptAnalysis
    {
        Summary = "Discussed the plan.",
        SpeakerQuality = new List<SpeakerQualityRating>
        {
            new SpeakerQualityRating { Speaker = "Speaker 1", Clarity = 4, Informativeness = 5, Engagement = 3, Rationale = "Led the discussion clearly." },
            new SpeakerQualityRating { Speaker = "Speaker 2", Clarity = 3, Informativeness = 3, Engagement = 2, Rationale = "Brief responses only." },
        },
    };

    var folder = MeetingFolderWriter.WriteMeetingFolder(outputDir, transcript, analysis);
    var metadataJson = File.ReadAllText(Path.Combine(folder, "metadata.json"));
    using var doc = JsonDocument.Parse(metadataJson);

    var stats = doc.RootElement.GetProperty("speakerStatistics");
    TestKit.Assert(stats.GetArrayLength() == 2, "should include statistics for both speakers");

    var speaker1 = stats.EnumerateArray().First(e => e.GetProperty("name").GetString() == "Speaker 1");
    TestKit.Assert(speaker1.GetProperty("speakingTimeSeconds").GetDouble() == 10, "Speaker 1's speaking time: turn1 (0->10)=10s + turn3 (last turn)=0s = 10s");
    TestKit.Assert(speaker1.GetProperty("turnCount").GetInt32() == 2, "Speaker 1 spoke in 2 turns");
    TestKit.Assert(speaker1.GetProperty("clarity").GetInt32() == 4, "Speaker 1's clarity rating should be merged from analysis.SpeakerQuality");
    TestKit.Assert(speaker1.GetProperty("rationale").GetString() == "Led the discussion clearly.", "Speaker 1's rationale should be merged correctly");

    var speaker2 = stats.EnumerateArray().First(e => e.GetProperty("name").GetString() == "Speaker 2");
    TestKit.Assert(speaker2.GetProperty("speakingTimeSeconds").GetDouble() == 10, "Speaker 2's speaking time should be (20-10)=10 seconds");
    TestKit.Assert(speaker2.GetProperty("engagement").GetInt32() == 2, "Speaker 2's engagement rating should be merged correctly");

    var totalPercent = speaker1.GetProperty("speakingTimePercent").GetDouble() + speaker2.GetProperty("speakingTimePercent").GetDouble();
    TestKit.Assert(Math.Abs(totalPercent - 100) < 0.01, "speaking time percentages should sum to 100%");

    Directory.Delete(outputDir, recursive: true);
}

TestKit.Section("MeetingFolderWriter: speaker present in only timing or only quality still appears (no fabricated half)");
{
    var outputDir = Path.Combine(Path.GetTempPath(), $"pensieve-notes-{Guid.NewGuid()}");
    var transcript = new Transcript
    {
        Id = "spk2",
        Title = "Partial Stats Check",
        RawText = "**Speaker 1** *[00:00]*: Hello.\n**Speaker 2** *[00:05]*: Hi.",
    };
    var analysis = new TranscriptAnalysis
    {
        Summary = "Short call.",
        SpeakerQuality = new List<SpeakerQualityRating>
        {
            new SpeakerQualityRating { Speaker = "Speaker 1", Clarity = 4, Informativeness = 4, Engagement = 4, Rationale = "Clear." },
        },
    };

    var folder = MeetingFolderWriter.WriteMeetingFolder(outputDir, transcript, analysis);
    var metadataJson = File.ReadAllText(Path.Combine(folder, "metadata.json"));
    using var doc = JsonDocument.Parse(metadataJson);
    var stats = doc.RootElement.GetProperty("speakerStatistics");

    var speaker2 = stats.EnumerateArray().First(e => e.GetProperty("name").GetString() == "Speaker 2");
    TestKit.Assert(speaker2.GetProperty("clarity").ValueKind == JsonValueKind.Null, "Speaker 2 has no quality rating — clarity should be null, never fabricated");
    TestKit.Assert(speaker2.GetProperty("turnCount").GetInt32() == 1, "Speaker 2's timing should still be present");

    Directory.Delete(outputDir, recursive: true);
}


TestKit.Section("MeetingFolderWriter: renders 'not specified' for Related Meetings when linking is disabled/no related meetings");
{
    var outputDir = Path.Combine(Path.GetTempPath(), $"pensieve-notes-{Guid.NewGuid()}");
    var transcript = new Transcript { Id = "rel-none", Title = "Solo Meeting", RawText = "Alice: hi." };
    var analysis = new TranscriptAnalysis { Summary = "x" };

    var folder = MeetingFolderWriter.WriteMeetingFolder(outputDir, transcript, analysis);
    var note = File.ReadAllText(Path.Combine(folder, "note.md"));
    TestKit.Assert(note.Contains("## Related Meetings"), "note.md should always include a Related Meetings section header");

    var metadataJson = File.ReadAllText(Path.Combine(folder, "metadata.json"));
    using var doc = JsonDocument.Parse(metadataJson);
    TestKit.Assert(doc.RootElement.GetProperty("relatedMeetingIds").GetArrayLength() == 0, "relatedMeetingIds should be empty when no related meetings were passed");
    TestKit.Assert(doc.RootElement.GetProperty("seriesKey").ValueKind == JsonValueKind.Null, "seriesKey should be null when not provided");

    Directory.Delete(outputDir, recursive: true);
}

TestKit.Section("MeetingFolderWriter: renders linked Related Meetings with relative note.md links and dates");
{
    var outputDir = Path.Combine(Path.GetTempPath(), $"pensieve-notes-{Guid.NewGuid()}");
    var transcript = new Transcript { Id = "rel-cur", Title = "Daily Standup - Aug 30", Date = 1700000000000, RawText = "Alice: status." };
    var analysis = new TranscriptAnalysis { Summary = "x" };

    var otherFolder = Path.Combine(outputDir, "2026-08-28--0900--daily-standup--rel-old");
    Directory.CreateDirectory(otherFolder);
    var related = new List<MeetingIndexEntry>
    {
        new MeetingIndexEntry { MeetingId = "rel-old", Title = "Daily Standup - Aug 28", DateEpochMs = 1699900000000, FolderPath = otherFolder, SeriesKey = "daily standup" },
    };

    var folder = MeetingFolderWriter.WriteMeetingFolder(outputDir, transcript, analysis, related, "daily standup");
    var note = File.ReadAllText(Path.Combine(folder, "note.md"));

    TestKit.Assert(note.Contains("Daily Standup - Aug 28"), "note.md should list the related meeting's title");
    TestKit.Assert(note.Contains("../2026-08-28--0900--daily-standup--rel-old/note.md") || note.Contains("2026-08-28--0900--daily-standup--rel-old/note.md"), "note.md should link to the related meeting's note.md via a relative path");

    var metadataJson = File.ReadAllText(Path.Combine(folder, "metadata.json"));
    using var doc = JsonDocument.Parse(metadataJson);
    TestKit.Assert(doc.RootElement.GetProperty("seriesKey").GetString() == "daily standup", "metadata.json should record the series key");
    var relatedIds = doc.RootElement.GetProperty("relatedMeetingIds");
    TestKit.Assert(relatedIds.GetArrayLength() == 1 && relatedIds[0].GetString() == "rel-old", "metadata.json should record the related meeting id");

    Directory.Delete(outputDir, recursive: true);
}

{
    var outputDir = Path.Combine(Path.GetTempPath(), $"pensieve-notes-{Guid.NewGuid()}");
    var transcript = new Transcript { Id = "empty1", Title = "Untitled Call", RawText = "..." };
    var analysis = new TranscriptAnalysis { Summary = "" };

    var folder = MeetingFolderWriter.WriteMeetingFolder(outputDir, transcript, analysis);
    var note = File.ReadAllText(Path.Combine(folder, "note.md"));

    var occurrences = note.Split("not specified").Length - 1;
    TestKit.Assert(occurrences >= 4, $"should use 'not specified' as placeholder for every missing field; found {occurrences} occurrences");
    TestKit.Assert(!note.Contains("ship on Friday"), "should not contain any invented decision/date content");

    Directory.Delete(outputDir, recursive: true);
}

