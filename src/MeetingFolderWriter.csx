#nullable enable
// MeetingFolderWriter.csx
// Creates one output folder per meeting under OUTPUT_DIR containing:
//   transcript.md   - verbatim copy of the original transcript text (never the original file
//                      itself; the source file dropped by Fireflies is NEVER modified or moved)
//   note.md         - YAML frontmatter (tags only — no duplication with the body) +
//                      a body header with date/participants/links, summary, agreements, open
//                      questions, next actions, diagrams, keywords (camelCase)
//   diagrams/*.md   - one file per Mermaid diagram (fenced ```mermaid block), only created when
//                      the analysis produced at least one diagram
//   keywords.json   - this meeting's keyword vocabulary (canonical casing, not camelCase)
//   metadata.json   - app version that generated this output + MD5 checksums of note.md and
//                      transcript.md
//
// All displayed/derived dates use the Europe/Bratislava local timezone (see DateTimeHelper.csx).

#load "Models.csx"
#load "SafeFileName.csx"
#load "DateTimeHelper.csx"
#load "KeywordFormatter.csx"
#load "Version.csx"
#load "SpeakerTimingAnalyzer.csx"
#load "MeetingIndexStore.csx"

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class MeetingFolderWriter
{
    private const string Placeholder = "not specified";

    /// <summary>Builds the per-meeting output folder path:
    /// OUTPUT_DIR/&lt;YYYY-MM-DD&gt;--&lt;HHmm&gt;--&lt;safe-title&gt;--&lt;id&gt;/
    /// The date/time segment is the meeting's Bratislava-local date and time, so that same-day
    /// meetings sort correctly by time in a plain alphabetical folder listing.</summary>
    public static string BuildMeetingFolderPath(string outputDir, Transcript transcript)
    {
        var localDate = GetLocalDate(transcript);
        var datePrefix = localDate.ToString("yyyy-MM-dd");
        var timePrefix = localDate.ToString("HHmm");
        var slug = SafeFileName.Slugify(transcript.Title);
        var folderName = $"{datePrefix}--{timePrefix}--{slug}--{transcript.Id}";
        return Path.Combine(outputDir, folderName);
    }

    private static DateTimeOffset GetLocalDate(Transcript transcript)
    {
        var utc = transcript.GetDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return DateTimeHelper.ToBratislava(utc);
    }

    /// <summary>Writes the full meeting folder (transcript copy, note, diagrams, keywords,
    /// metadata) and returns the folder path. <paramref name="relatedMeetings"/> and
    /// <paramref name="seriesKey"/> are only non-empty when ENABLE_MEETING_LINKING is on; when
    /// linking is disabled they are simply omitted/blank, never guessed.</summary>
    public static string WriteMeetingFolder(
        string outputDir,
        Transcript transcript,
        TranscriptAnalysis analysis,
        IReadOnlyList<MeetingIndexEntry>? relatedMeetings = null,
        string? seriesKey = null)
    {
        relatedMeetings ??= Array.Empty<MeetingIndexEntry>();
        var folderPath = BuildMeetingFolderPath(outputDir, transcript);
        Directory.CreateDirectory(folderPath);

        // 1. Verbatim transcript copy — plain text/markdown rendering of the source content,
        // never the original file and never altered/paraphrased.
        var transcriptPath = Path.Combine(folderPath, "transcript.md");
        File.WriteAllText(transcriptPath, transcript.RawText);

        // 2. Analysis note with trimmed YAML frontmatter (tags only).
        var notePath = Path.Combine(folderPath, "note.md");
        var noteMarkdown = BuildNoteMarkdown(transcript, analysis, folderPath, relatedMeetings);
        File.WriteAllText(notePath, noteMarkdown);

        // 3. Diagrams (only when present).
        if (analysis.Diagrams.Count > 0)
        {
            var diagramsDir = Path.Combine(folderPath, "diagrams");
            Directory.CreateDirectory(diagramsDir);
            for (var i = 0; i < analysis.Diagrams.Count; i++)
            {
                var diagram = analysis.Diagrams[i];
                var fileName = $"diagram-{i + 1}--{SafeFileName.Slugify(diagram.Title)}.md";
                var content = $"# {diagram.Title}\n\n```mermaid\n{diagram.Mermaid}\n```\n";
                File.WriteAllText(Path.Combine(diagramsDir, fileName), content);
            }
        }

        // 4. Per-meeting keyword vocabulary (canonical casing — not camelCase; that's a note.md
        // presentation-only concern).
        var keywordsJson = JsonSerializer.Serialize(new
        {
            meetingId = transcript.Id,
            title = transcript.Title,
            tags = analysis.Tags,
            keywords = analysis.Keywords,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(folderPath, "keywords.json"), keywordsJson);

        // 5. Metadata: app version + MD5 checksums of note.md/transcript.md, computed from the
        // files actually written above, plus per-speaker statistics (deterministic speaking
        // time/turn count merged with the LLM-assessed quality ratings, by speaker name), plus
        // (when ENABLE_MEETING_LINKING is on) this meeting's series key and related meeting ids.
        var speakerStatistics = BuildSpeakerStatistics(transcript, analysis);
        var metadataJson = JsonSerializer.Serialize(new
        {
            appVersion = AppVersion.Current,
            generatedAt = DateTimeHelper.ToBratislava(DateTimeOffset.UtcNow).ToString("O"),
            files = new Dictionary<string, object>
            {
                ["note.md"] = new { md5 = ComputeMd5(notePath) },
                ["transcript.md"] = new { md5 = ComputeMd5(transcriptPath) },
            },
            speakerStatistics,
            seriesKey = string.IsNullOrWhiteSpace(seriesKey) ? null : seriesKey,
            relatedMeetingIds = relatedMeetings.Select(m => m.MeetingId).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(folderPath, "metadata.json"), metadataJson);

        return folderPath;
    }

    /// <summary>Merges deterministic per-speaker timing (speaking time + turn count) with the
    /// LLM-assessed quality ratings, matched by speaker name (case-insensitive, trimmed).
    /// Speakers present in only one of the two sources still appear, with the fields they have
    /// (never fabricating the missing half).</summary>
    private static List<object> BuildSpeakerStatistics(Transcript transcript, TranscriptAnalysis analysis)
    {
        var timings = transcript.SourceType == TranscriptSourceType.FirefliesApi && transcript.Sentences.Count > 0
            ? SpeakerTimingAnalyzer.AnalyzeSentences(transcript.Sentences)
            : SpeakerTimingAnalyzer.AnalyzeRawText(transcript.RawText);

        var totalSeconds = timings.Sum(t => t.SpeakingTimeSeconds);

        var qualityByName = analysis.SpeakerQuality
            .GroupBy(q => q.Speaker.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in timings)
        {
            if (seen.Add(t.Speaker)) order.Add(t.Speaker);
        }
        foreach (var q in analysis.SpeakerQuality)
        {
            var name = q.Speaker.Trim();
            if (seen.Add(name)) order.Add(name);
        }

        var timingByName = timings.ToDictionary(t => t.Speaker, StringComparer.OrdinalIgnoreCase);

        var result = new List<object>();
        foreach (var name in order)
        {
            timingByName.TryGetValue(name, out var timing);
            qualityByName.TryGetValue(name, out var quality);

            var speakingTimeSeconds = timing?.SpeakingTimeSeconds ?? 0;
            result.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["speakingTimeSeconds"] = speakingTimeSeconds,
                ["speakingTimePercent"] = totalSeconds > 0 ? Math.Round(speakingTimeSeconds / totalSeconds * 100, 1) : 0,
                ["turnCount"] = timing?.TurnCount ?? 0,
                ["clarity"] = quality?.Clarity,
                ["informativeness"] = quality?.Informativeness,
                ["engagement"] = quality?.Engagement,
                ["rationale"] = quality?.Rationale,
            });
        }
        return result;
    }

    private static string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string BuildNoteMarkdown(
        Transcript transcript,
        TranscriptAnalysis analysis,
        string? folderPath = null,
        IReadOnlyList<MeetingIndexEntry>? relatedMeetings = null)
    {
        relatedMeetings ??= Array.Empty<MeetingIndexEntry>();
        var sb = new StringBuilder();
        var localDate = GetLocalDate(transcript);
        var dateDisplay = localDate.ToString("yyyy-MM-dd HH:mm 'Bratislava'");
        var title = string.IsNullOrWhiteSpace(transcript.Title) ? Placeholder : transcript.Title;
        var link = string.IsNullOrWhiteSpace(transcript.TranscriptUrl) ? Placeholder : transcript.TranscriptUrl;

        var participants = transcript.Participants.Any()
            ? string.Join(", ", transcript.Participants)
            : (transcript.Speakers.Any()
                ? string.Join(", ", transcript.Speakers.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
                : Placeholder);
        if (string.IsNullOrWhiteSpace(participants))
        {
            participants = Placeholder;
        }

        // YAML frontmatter (Obsidian-compatible): tags only — title/date/fireflies_id/url are
        // NOT duplicated here; they live solely in the body header below.
        sb.AppendLine("---");
        sb.AppendLine("tags:");
        if (analysis.Tags.Count == 0)
        {
            sb.AppendLine($"  - {Placeholder.Replace(' ', '-')}");
        }
        else
        {
            foreach (var tag in analysis.Tags)
            {
                sb.AppendLine($"  - {KeywordFormatter.ToCamelCase(tag)}");
            }
        }
        sb.AppendLine("---");
        sb.AppendLine();

        // Header
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- **Date:** {dateDisplay}");
        sb.AppendLine($"- **Participants:** {participants}");
        sb.AppendLine($"- **Fireflies link:** {link}");
        sb.AppendLine($"- **Transcript ID:** {transcript.FirefliesId ?? transcript.Id}");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(analysis.Summary) ? Placeholder : analysis.Summary);
        sb.AppendLine();

        // Agreements / decisions
        sb.AppendLine("## Agreements");
        sb.AppendLine();
        AppendBulletList(sb, analysis.Agreements);
        sb.AppendLine();

        // Open questions
        sb.AppendLine("## Open Questions");
        sb.AppendLine();
        AppendBulletList(sb, analysis.OpenQuestions);
        sb.AppendLine();

        // Next actions (checklist)
        sb.AppendLine("## Next Actions");
        sb.AppendLine();
        if (analysis.NextActions == null || analysis.NextActions.Count == 0)
        {
            sb.AppendLine($"- [ ] {Placeholder} — owner: {Placeholder}, due: {Placeholder}");
        }
        else
        {
            foreach (var action in analysis.NextActions)
            {
                var task = string.IsNullOrWhiteSpace(action.Task) ? Placeholder : action.Task;
                var owner = string.IsNullOrWhiteSpace(action.Owner) ? Placeholder : action.Owner;
                var due = string.IsNullOrWhiteSpace(action.Due) ? Placeholder : action.Due;
                sb.AppendLine($"- [ ] {task} — owner: {owner}, due: {due}");
            }
        }
        sb.AppendLine();

        // Diagrams (linked, not inlined, to keep note.md concise)
        if (analysis.Diagrams.Count > 0)
        {
            sb.AppendLine("## Diagrams");
            sb.AppendLine();
            for (var i = 0; i < analysis.Diagrams.Count; i++)
            {
                var diagram = analysis.Diagrams[i];
                var fileName = $"diagram-{i + 1}--{SafeFileName.Slugify(diagram.Title)}.md";
                sb.AppendLine($"- [{diagram.Title}](diagrams/{fileName})");
            }
            sb.AppendLine();
        }

        // Keywords — rendered in camelCase for note.md presentation only; keywords.json and the
        // global vocabulary retain the original/canonical casing.
        sb.AppendLine("## Keywords");
        sb.AppendLine();
        sb.AppendLine(analysis.Keywords.Count == 0 ? Placeholder : string.Join(", ", analysis.Keywords.Select(KeywordFormatter.ToCamelCase)));
        sb.AppendLine();

        // Related meetings (only rendered when ENABLE_MEETING_LINKING is on): purely mechanical
        // links — same recurring series (e.g. standups) or shared tags/keywords with other
        // meetings — never LLM-derived, so no cross-meeting content is invented.
        sb.AppendLine("## Related Meetings");
        sb.AppendLine();
        if (relatedMeetings.Count == 0)
        {
            sb.AppendLine($"- {Placeholder}");
        }
        else
        {
            foreach (var related in relatedMeetings)
            {
                var relatedTitle = string.IsNullOrWhiteSpace(related.Title) ? Placeholder : related.Title;
                var relatedDate = related.DateEpochMs.HasValue
                    ? DateTimeHelper.ToBratislava(DateTimeOffset.FromUnixTimeMilliseconds((long)related.DateEpochMs.Value)).ToString("yyyy-MM-dd")
                    : Placeholder;
                var relativeLink = BuildRelativeNoteLink(folderPath, related.FolderPath);
                sb.AppendLine(relativeLink != null
                    ? $"- [{relatedTitle}]({relativeLink}) — {relatedDate}"
                    : $"- {relatedTitle} — {relatedDate}");
            }
        }
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>Builds a relative markdown link from this meeting's folder to another meeting's
    /// note.md, so Related Meetings links keep working if OUTPUT_DIR is moved/copied elsewhere
    /// (e.g. into an Obsidian vault). Returns null (never guessed) when either folder path is
    /// unknown, falling back to plain text instead of a broken link.</summary>
    private static string? BuildRelativeNoteLink(string? fromFolder, string? toFolder)
    {
        if (string.IsNullOrWhiteSpace(fromFolder) || string.IsNullOrWhiteSpace(toFolder))
        {
            return null;
        }

        try
        {
            var relativeFolder = Path.GetRelativePath(fromFolder, toFolder);
            return Path.Combine(relativeFolder, "note.md").Replace('\\', '/');
        }
        catch
        {
            return null;
        }
    }

    private static void AppendBulletList(StringBuilder sb, List<string>? items)
    {
        if (items == null || items.Count == 0)
        {
            sb.AppendLine($"- {Placeholder}");
            return;
        }

        foreach (var item in items)
        {
            sb.AppendLine($"- {(string.IsNullOrWhiteSpace(item) ? Placeholder : item)}");
        }
    }

}
