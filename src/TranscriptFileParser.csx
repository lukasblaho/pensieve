#nullable enable
// TranscriptFileParser.csx
// Parses a .md transcript file dropped by Fireflies into the watched folder into a Transcript
// model. Never modifies the original file. Best-effort extracts an embedded Fireflies
// transcript ID/URL (from YAML frontmatter or a plain "Fireflies link: ..." style line) so
// deletion can later target the right transcript; falls back to a locally generated stable ID
// (a content hash) when none is found — never guessed.
//
// Fireflies' own local export layout drops two sibling folders: "Transcripts" (raw transcript
// per meeting) and "Summaries" (Fireflies' own AI summary per meeting, which reliably embeds the
// Fireflies meeting ID/link). Filenames share the pattern "<Title>-transcript-<timestamp>.md" and
// "<Title>-summary-<timestamp>.md" with an identical <timestamp>. When a summary folder is
// configured (SUMMARY_FOLDER), the parser locates the matching summary file by filename and uses
// it purely as a metadata source (Fireflies ID, canonical title, meeting date) — never as
// additional analysis input, so Copilot CLI analysis remains scoped strictly to the raw
// transcript text.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public static class TranscriptFileParser
{
    private static readonly Regex FrontmatterRegex = new Regex(@"\A---\s*\r?\n(.*?)\r?\n---\s*\r?\n?", RegexOptions.Singleline);
    private static readonly Regex FirefliesUrlRegex = new Regex(@"https?://app\.fireflies\.ai/view/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
    private static readonly Regex HeadingRegex = new Regex(@"^#\s+(.+?)\s*$", RegexOptions.Multiline);
    private const string TranscriptMarker = "-transcript-";
    private const string SummaryMarker = "-summary-";

    /// <summary>
    /// Parses a dropped transcript file. If <paramref name="summaryFolder"/> is provided and a
    /// matching "<Title>-summary-<timestamp>.md" file exists there (Fireflies' native export
    /// layout), its embedded Fireflies ID/title/date take priority over other fallbacks — but its
    /// text content is never read into the transcript body or passed to analysis.
    /// </summary>
    public static Transcript Parse(string filePath, string? summaryFolder = null)
    {
        var raw = File.ReadAllText(filePath);

        var frontmatter = "";
        var body = raw;
        var fmMatch = FrontmatterRegex.Match(raw);
        if (fmMatch.Success)
        {
            frontmatter = fmMatch.Groups[1].Value;
            body = raw.Substring(fmMatch.Length);
        }

        var firefliesId = ExtractFrontmatterValue(frontmatter, "fireflies_id")
            ?? ExtractFrontmatterValue(frontmatter, "id");

        var url = ExtractFrontmatterValue(frontmatter, "url")
            ?? ExtractFrontmatterValue(frontmatter, "transcript_url");

        if (firefliesId == null)
        {
            var urlMatch = FirefliesUrlRegex.Match(raw);
            if (urlMatch.Success)
            {
                firefliesId = urlMatch.Groups[1].Value;
                url ??= urlMatch.Value;
            }
        }

        // Metadata-only lookup of Fireflies' own paired summary file (native export layout),
        // used purely to reliably resolve ID/title/date — its text is never fed into RawText or
        // passed to Copilot analysis.
        SummaryMetadata? summaryMeta = null;
        if (!string.IsNullOrWhiteSpace(summaryFolder))
        {
            summaryMeta = TryReadSummaryMetadata(filePath, summaryFolder!);
        }

        if (firefliesId == null && summaryMeta?.FirefliesId != null)
        {
            firefliesId = summaryMeta.FirefliesId;
            url ??= summaryMeta.Url;
        }

        var title = ExtractFrontmatterValue(frontmatter, "title");
        if (title == null)
        {
            var headingMatch = HeadingRegex.Match(body);
            title = headingMatch.Success ? headingMatch.Groups[1].Value.Trim() : null;
        }
        title ??= summaryMeta?.Title ?? Path.GetFileNameWithoutExtension(filePath);

        DateTimeOffset date;
        var dateStr = ExtractFrontmatterValue(frontmatter, "date");
        if (dateStr != null && DateTimeOffset.TryParse(dateStr, out var parsedDate))
        {
            date = parsedDate;
        }
        else if (summaryMeta?.Date != null)
        {
            date = summaryMeta.Date.Value;
        }
        else
        {
            date = new DateTimeOffset(SafeGetCreationTimeUtc(filePath), TimeSpan.Zero);
        }

        var hash = ComputeContentHash(raw);
        var id = firefliesId ?? $"local-{hash.Substring(0, 12)}";

        return new Transcript
        {
            Id = id,
            FirefliesId = firefliesId,
            Title = title,
            Date = date.ToUnixTimeMilliseconds(),
            TranscriptUrl = url,
            RawText = body.Trim(),
            SourceType = TranscriptSourceType.Folder,
            SourceFilePath = filePath,
            ContentHash = hash,
        };
    }

    /// <summary>Computes a stable content hash for change detection, independent of frontmatter parsing.</summary>
    public static string ComputeContentHash(string rawFileContent)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(rawFileContent));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class SummaryMetadata
    {
        public string? FirefliesId { get; set; }
        public string? Url { get; set; }
        public string? Title { get; set; }
        public DateTimeOffset? Date { get; set; }
    }

    // Fireflies' native export filenames look like "<Title>-transcript-<timestamp>.md" in
    // Transcripts/ and "<Title>-summary-<timestamp>.md" in Summaries/, sharing an identical
    // <Title> and <timestamp>. This locates the summary file by swapping the marker.
    private static SummaryMetadata? TryReadSummaryMetadata(string transcriptFilePath, string summaryFolder)
    {
        try
        {
            var transcriptFileName = Path.GetFileName(transcriptFilePath);
            if (!transcriptFileName.Contains(TranscriptMarker))
            {
                return null;
            }

            var summaryFileName = transcriptFileName.Replace(TranscriptMarker, SummaryMarker);
            var summaryPath = Path.Combine(summaryFolder, summaryFileName);
            if (!File.Exists(summaryPath))
            {
                return null;
            }

            var summaryText = File.ReadAllText(summaryPath);

            var meta = new SummaryMetadata();

            var urlMatch = FirefliesUrlRegex.Match(summaryText);
            if (urlMatch.Success)
            {
                meta.FirefliesId = urlMatch.Groups[1].Value;
                meta.Url = urlMatch.Value;
            }

            var headingMatch = HeadingRegex.Match(summaryText);
            if (headingMatch.Success)
            {
                meta.Title = headingMatch.Groups[1].Value.Trim();
            }

            // e.g. "> August 18, 2026 ◦ 12:36 PM CEST ◦ 20 mins"
            var dateLineMatch = Regex.Match(summaryText, @">\s*([A-Za-z]+ \d{1,2},\s*\d{4})\s*◦\s*([\d:]+\s*[AP]M)", RegexOptions.IgnoreCase);
            if (dateLineMatch.Success)
            {
                var dateTimeStr = $"{dateLineMatch.Groups[1].Value} {dateLineMatch.Groups[2].Value}";
                if (DateTimeOffset.TryParse(dateTimeStr, out var parsed))
                {
                    meta.Date = parsed;
                }
            }

            return meta;
        }
        catch
        {
            // Metadata lookup is best-effort only; any failure falls back to the normal chain.
            return null;
        }
    }

    private static string? ExtractFrontmatterValue(string frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter)) return null;
        var match = Regex.Match(frontmatter, $@"^{Regex.Escape(key)}:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = match.Groups[1].Value.Trim().Trim('"', '\'');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DateTime SafeGetCreationTimeUtc(string path)
    {
        try { return File.GetCreationTimeUtc(path); }
        catch { return DateTime.UtcNow; }
    }
}
