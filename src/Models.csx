#nullable enable
// Models.csx
// Plain data models used across the agent.

using System;
using System.Collections.Generic;

public sealed class Speaker
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class Sentence
{
    public int Index { get; set; }
    public string? SpeakerName { get; set; }
    public string? Text { get; set; }
    public double? StartTime { get; set; }
}

public enum TranscriptSourceType
{
    /// <summary>Picked up from the locally watched folder where Fireflies auto-saves a .md file.</summary>
    Folder,

    /// <summary>Fetched via the Fireflies GraphQL API.</summary>
    FirefliesApi,
}

/// <summary>
/// A transcript to be analyzed, regardless of where it came from. Exactly one of
/// <see cref="Sentences"/> (Fireflies API source) or <see cref="RawText"/> (folder source)
/// carries the actual spoken content; <see cref="RawText"/> is always populated before analysis
/// (built from <see cref="Sentences"/> for API-sourced transcripts).
/// </summary>
public sealed class Transcript
{
    /// <summary>Stable identity for this transcript: the real Fireflies ID when known, otherwise
    /// a locally generated "local-&lt;hash&gt;" id for folder-sourced transcripts with no resolvable ID.</summary>
    public string Id { get; set; } = "";

    /// <summary>The resolved Fireflies transcript ID, if known (embedded in the source file, or
    /// resolved via the API by title+date). Null if it could not be resolved.</summary>
    public string? FirefliesId { get; set; }

    public string? Title { get; set; }
    public double? Date { get; set; } // epoch milliseconds
    public string? DateString { get; set; }
    public string? TranscriptUrl { get; set; }
    public List<string> Participants { get; set; } = new();
    public List<Speaker> Speakers { get; set; } = new();
    public List<Sentence> Sentences { get; set; } = new();

    /// <summary>Full transcript text used for analysis and for the verbatim copy written to the
    /// meeting folder (for API-sourced transcripts, rendered deterministically from
    /// <see cref="Sentences"/>; for folder-sourced transcripts, the original file's body text).</summary>
    public string RawText { get; set; } = "";

    public TranscriptSourceType SourceType { get; set; } = TranscriptSourceType.FirefliesApi;

    /// <summary>Original file path for folder-sourced transcripts. Never written to.</summary>
    public string? SourceFilePath { get; set; }

    /// <summary>Content hash used for change detection / idempotency in the state store.</summary>
    public string ContentHash { get; set; } = "";

    public DateTimeOffset? GetDateTimeOffset()
    {
        if (Date.HasValue)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Date.Value);
        }
        return null;
    }
}

/// <summary>
/// A single next-action/task item. Owner/Due are literal "not specified" (never invented)
/// when not explicitly stated in the transcript.
/// </summary>
public sealed class ActionItem
{
    public string Task { get; set; } = "";
    public string Owner { get; set; } = "not specified";
    public string Due { get; set; } = "not specified";
}

/// <summary>A Mermaid diagram generated only when the transcript explicitly discusses a
/// flow/process/architecture/component relationship.</summary>
public sealed class DiagramItem
{
    public string Title { get; set; } = "";
    public string Mermaid { get; set; } = "";
}

/// <summary>
/// LLM-assessed communication/information quality for one speaker, grounded strictly in what
/// they actually said in this transcript. Clarity/Informativeness/Engagement are always an
/// integer 1-5 (a judgment call, unlike factual fields such as agreements/owners which fall
/// back to "not specified" when absent) — the model is instructed to give its best-effort
/// rating even from limited evidence, while the rationale must stay honest about that.
/// </summary>
public sealed class SpeakerQualityRating
{
    public string Speaker { get; set; } = "";
    public int Clarity { get; set; }
    public int Informativeness { get; set; }
    public int Engagement { get; set; }
    public string Rationale { get; set; } = "";
}

/// <summary>
/// Structured analysis output expected from Copilot CLI for a single transcript. Always
/// produced in English, regardless of the source transcript's language (English, Slovak, or
/// Czech). Scope is strictly limited to the current transcript's content — no cross-meeting
/// knowledge, nothing invented.
/// </summary>
public sealed class TranscriptAnalysis
{
    public string Summary { get; set; } = "";
    public List<string> Agreements { get; set; } = new();
    public List<string> OpenQuestions { get; set; } = new();
    public List<ActionItem> NextActions { get; set; } = new();

    /// <summary>Flattened, display/storage-facing tag list (max 5): built from
    /// MeetingType + Category + Topics, in that order. This is what note.md frontmatter,
    /// keywords.json, Notion, GlobalVocabularyStore, and MeetingLinker all consume.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>The single tag describing what kind of meeting this was (e.g. "standup",
    /// "planning", "one-to-one"). Freeform — not validated against a fixed enum.</summary>
    public string MeetingType { get; set; } = "";

    /// <summary>The single tag describing the general nature/domain of the meeting (e.g.
    /// "technology", "design", "team", "one-to-one", "business"). Freeform.</summary>
    public string Category { get; set; } = "";

    /// <summary>1-3 short tags naming the key subject(s) actually discussed in this
    /// transcript. Freeform, never invented beyond what the transcript supports.</summary>
    public List<string> Topics { get; set; } = new();

    public List<string> Keywords { get; set; } = new();
    public List<DiagramItem> Diagrams { get; set; } = new();

    /// <summary>LLM-assessed per-speaker communication/information quality ratings. Only
    /// speakers who actually appear in the transcript are rated — never invented.</summary>
    public List<SpeakerQualityRating> SpeakerQuality { get; set; } = new();
}

/// <summary>
/// Deterministic (non-LLM), code-computed per-speaker speaking time and turn count, derived
/// purely from transcript timestamps: for each turn, duration = next turn's start time minus
/// this turn's start time; the final turn in the transcript contributes 0 duration (no "next"
/// timestamp to bound it).
/// </summary>
public sealed class SpeakerTiming
{
    public string Speaker { get; set; } = "";
    public double SpeakingTimeSeconds { get; set; }
    public int TurnCount { get; set; }
}
