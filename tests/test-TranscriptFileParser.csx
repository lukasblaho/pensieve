#nullable enable
// test-TranscriptFileParser.csx
// Verifies parsing of dropped .md transcript files: frontmatter-embedded Fireflies ID/URL
// extraction, fallback URL-in-body extraction, fallback local ID generation (hash-based, never
// guessed), title extraction from frontmatter or first heading, and content-hash stability.

#load "TestKit.csx"
#load "../src/TranscriptFileParser.csx"
#load "../src/Models.csx"

using System;
using System.IO;

TestKit.Section("TranscriptFileParser: extracts Fireflies ID and title from YAML frontmatter");
{
    var path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid()}.md");
    File.WriteAllText(path, "---\nfireflies_id: abc123\ntitle: \"Weekly Sync\"\ndate: 2023-11-14\n---\n# Weekly Sync\n\nAlice: hello\nBob: hi\n");

    var transcript = TranscriptFileParser.Parse(path);

    TestKit.Assert(transcript.FirefliesId == "abc123", "should extract fireflies_id from frontmatter");
    TestKit.Assert(transcript.Id == "abc123", "transcript Id should use the resolved Fireflies ID when present");
    TestKit.Assert(transcript.Title == "Weekly Sync", "should extract title from frontmatter");
    TestKit.Assert(transcript.RawText.Contains("Alice: hello"), "raw text should contain the transcript body, excluding frontmatter");
    TestKit.Assert(transcript.SourceFilePath == path, "source file path should be recorded");

    File.Delete(path);
}

TestKit.Section("TranscriptFileParser: falls back to extracting a Fireflies URL/ID from plain body text");
{
    var path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid()}.md");
    File.WriteAllText(path, "# Standup\n\nFireflies link: https://app.fireflies.ai/view/xyz789\n\nAlice: hi\n");

    var transcript = TranscriptFileParser.Parse(path);

    TestKit.Assert(transcript.FirefliesId == "xyz789", "should extract the ID from a Fireflies view URL in the body");
    TestKit.Assert(transcript.TranscriptUrl != null && transcript.TranscriptUrl.Contains("xyz789"), "should capture the matched URL");

    File.Delete(path);
}

TestKit.Section("TranscriptFileParser: generates a stable local ID (never guessed) when no Fireflies ID is present");
{
    var path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid()}.md");
    File.WriteAllText(path, "# No ID Meeting\n\nAlice: hello\nBob: hi\n");

    var transcript1 = TranscriptFileParser.Parse(path);
    var transcript2 = TranscriptFileParser.Parse(path);

    TestKit.Assert(transcript1.FirefliesId == null, "no Fireflies ID should be resolved when absent from the file");
    TestKit.Assert(transcript1.Id.StartsWith("local-"), "should fall back to a locally generated 'local-<hash>' id");
    TestKit.Assert(transcript1.Id == transcript2.Id, "the local id should be stable/deterministic for identical content");

    File.Delete(path);
}

TestKit.Section("TranscriptFileParser: content hash changes when file content changes (change detection)");
{
    var path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid()}.md");
    File.WriteAllText(path, "# Meeting\n\nAlice: first version\n");
    var v1 = TranscriptFileParser.Parse(path);

    File.WriteAllText(path, "# Meeting\n\nAlice: edited version\n");
    var v2 = TranscriptFileParser.Parse(path);

    TestKit.Assert(v1.ContentHash != v2.ContentHash, "content hash should change when the underlying file content changes");

    File.Delete(path);
}

TestKit.Section("TranscriptFileParser: never modifies the original file on disk");
{
    var path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid()}.md");
    var original = "# Meeting\n\nAlice: hello\n";
    File.WriteAllText(path, original);

    TranscriptFileParser.Parse(path);

    TestKit.Assert(File.ReadAllText(path) == original, "parsing must never modify the original transcript file");

    File.Delete(path);
}

TestKit.Section("TranscriptFileParser: resolves ID/title/date from a paired native Fireflies summary file");
{
    var dir = Path.Combine(Path.GetTempPath(), $"ff-parser-{Guid.NewGuid()}");
    var transcriptsDir = Path.Combine(dir, "Transcripts");
    var summariesDir = Path.Combine(dir, "Summaries");
    Directory.CreateDirectory(transcriptsDir);
    Directory.CreateDirectory(summariesDir);

    var transcriptPath = Path.Combine(transcriptsDir, "Weekly Sync-transcript-2026-08-18T10-36-32.000Z.md");
    var summaryPath = Path.Combine(summariesDir, "Weekly Sync-summary-2026-08-18T10-36-32.000Z.md");

    File.WriteAllText(transcriptPath, "# Weekly Sync\n\n**Speaker 1** *[00:05]*: hello\n");
    File.WriteAllText(summaryPath,
        "# Weekly Sync \n\n> August 18, 2026 \u25e6 12:36 PM CEST \u25e6 20 mins\n\n> someone@example.com\n\n[View Meeting Recording](https://app.fireflies.ai/view/01M0A73EFGM1JRH9PSBE021909)\n\n---\n\n- bullet\n");

    var transcript = TranscriptFileParser.Parse(transcriptPath, summariesDir);

    TestKit.Assert(transcript.FirefliesId == "01M0A73EFGM1JRH9PSBE021909", "should resolve the Fireflies ID from the paired summary file's link");
    TestKit.Assert(transcript.Id == "01M0A73EFGM1JRH9PSBE021909", "transcript Id should use the summary-resolved Fireflies ID");
    TestKit.Assert(transcript.Title == "Weekly Sync", "should resolve title from the summary file's heading");
    TestKit.Assert(transcript.TranscriptUrl != null && transcript.TranscriptUrl.Contains("01M0A73EFGM1JRH9PSBE021909"), "should capture the URL from the summary file");
    var date = transcript.GetDateTimeOffset();
    TestKit.Assert(date.HasValue && date.Value.Year == 2026 && date.Value.Month == 8 && date.Value.Day == 18, "should resolve the meeting date from the summary file's date line");
    TestKit.Assert(transcript.RawText.Contains("hello") && !transcript.RawText.Contains("bullet"), "summary content must never be merged into the transcript body");

    Directory.Delete(dir, recursive: true);
}

TestKit.Section("TranscriptFileParser: falls back gracefully when no matching summary file exists");
{
    var dir = Path.Combine(Path.GetTempPath(), $"ff-parser-{Guid.NewGuid()}");
    var transcriptsDir = Path.Combine(dir, "Transcripts");
    var summariesDir = Path.Combine(dir, "Summaries");
    Directory.CreateDirectory(transcriptsDir);
    Directory.CreateDirectory(summariesDir);

    var transcriptPath = Path.Combine(transcriptsDir, "Orphan Meeting-transcript-2026-08-18T10-36-32.000Z.md");
    File.WriteAllText(transcriptPath, "# Orphan Meeting\n\nAlice: hi\n");

    var transcript = TranscriptFileParser.Parse(transcriptPath, summariesDir);

    TestKit.Assert(transcript.FirefliesId == null, "should not guess an ID when no matching summary file exists");
    TestKit.Assert(transcript.Id.StartsWith("local-"), "should fall back to a local hash-based id");
    TestKit.Assert(transcript.Title == "Orphan Meeting", "should still resolve title from the transcript's own heading");

    Directory.Delete(dir, recursive: true);
}

TestKit.Section("TranscriptFileParser: ReadFileWithRetry survives a transient exclusive file lock (e.g. cloud-sync deadlock)");
{
    var path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid()}.md");
    File.WriteAllText(path, "# Locked Briefly\n\nAlice: hi\n");

    // Hold an exclusive (FileShare.None) lock briefly on a background thread, simulating a
    // transient cloud-sync lock ("Resource deadlock avoided"). ReadFileWithRetry should retry
    // past this instead of failing immediately.
    using var gate = new System.Threading.ManualResetEventSlim(false);
    var lockTask = System.Threading.Tasks.Task.Run(() =>
    {
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        gate.Set();
        System.Threading.Thread.Sleep(400);
    });

    gate.Wait();
    var content = TranscriptFileParser.ReadFileWithRetry(path);
    lockTask.Wait();

    TestKit.Assert(content.Contains("Locked Briefly"), "should eventually read the file's content once the transient lock is released");

    File.Delete(path);
}

TestKit.Section("TranscriptFileParser: ReadFileWithRetry surfaces the exception when the file is never unlocked");
{
    var path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid()}.md");
    File.WriteAllText(path, "# Always Locked\n\nAlice: hi\n");

    using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var threw = false;
    try
    {
        TranscriptFileParser.ReadFileWithRetry(path);
    }
    catch (IOException)
    {
        threw = true;
    }

    TestKit.Assert(threw, "should surface an IOException after exhausting all retries if the lock never clears");

    File.Delete(path);
}
