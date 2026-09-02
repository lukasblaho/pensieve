#nullable enable
// StateStore.csx
// Persists per-meeting processing state to data/state.json: which source (file path or
// Fireflies ID) has been analyzed, its content hash (for change detection), the generated
// meeting folder, the resolved Fireflies ID, independent delete/export status flags, and a
// cached copy of the analysis JSON — so each step (analysis, deletion, Obsidian export, Notion
// export) is idempotent and independently retryable: IsFullyProcessed() reports whether a
// meeting still has any enabled-but-incomplete optional step, letting a later sync pass retry
// just that missing step (from the cached AnalysisJson) without ever re-invoking the Copilot
// CLI or repeating a step that already succeeded (e.g. never creating a second Notion page for
// the same meeting). All mutation/read methods are lock-guarded because `watch` mode can invoke
// processing for multiple different files concurrently. Writes are atomic (temp file + rename)
// to avoid corruption on crash.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class MeetingRecord
{
    /// <summary>Stable identity: the source file path (folder-sourced) or the Fireflies transcript
    /// ID (API-sourced).</summary>
    [JsonPropertyName("sourceKey")]
    public string SourceKey { get; set; } = "";

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = "";

    [JsonPropertyName("meetingFolder")]
    public string? MeetingFolder { get; set; }

    [JsonPropertyName("firefliesId")]
    public string? FirefliesId { get; set; }

    /// <summary>The transcript's stable identity (Transcript.Id), used to look up a meeting's
    /// record by meetingId (e.g. to resolve a related meeting's already-created Notion page id)
    /// independently of its sourceKey.</summary>
    [JsonPropertyName("meetingId")]
    public string? MeetingId { get; set; }

    [JsonPropertyName("analyzed")]
    public bool Analyzed { get; set; }

    [JsonPropertyName("deletedFromFireflies")]
    public bool DeletedFromFireflies { get; set; }

    [JsonPropertyName("obsidianExported")]
    public bool ObsidianExported { get; set; }

    [JsonPropertyName("notionExported")]
    public bool NotionExported { get; set; }

    /// <summary>The Notion page id created for this meeting, once exported. Used so later
    /// meetings can link back to this one via a Notion relation property. Null when not yet
    /// (or never) exported to Notion.</summary>
    [JsonPropertyName("notionPageId")]
    public string? NotionPageId { get; set; }

    /// <summary>Serialized <c>TranscriptAnalysis</c> JSON captured at analysis time. Lets a
    /// later sync pass retry only a failed optional step (Fireflies deletion, Obsidian export,
    /// Notion export) for an already-analyzed meeting without re-invoking the Copilot CLI. Null
    /// for records written before this field existed — such records fall back to a one-time
    /// full reprocessing pass to catch up on any pending step.</summary>
    [JsonPropertyName("analysisJson")]
    public string? AnalysisJson { get; set; }
}

public sealed class AgentState
{
    [JsonPropertyName("meetings")]
    public Dictionary<string, MeetingRecord> Meetings { get; set; } = new();

    /// <summary>Last processed transcript date (epoch ms), used to resume the Fireflies API
    /// source pagination.</summary>
    [JsonPropertyName("lastProcessedDate")]
    public double? LastProcessedDate { get; set; }
}

public sealed class StateStore
{
    private readonly string _path;
    private readonly AgentState _state;
    // Guards all reads/mutations below. Needed because `watch` mode can invoke processing for
    // multiple different transcript files concurrently (one FileSystemWatcher debounce Timer per
    // file), and this store's Dictionary is not otherwise thread-safe.
    private readonly object _lock = new object();

    public StateStore(string path)
    {
        _path = path;
        _state = Load(path);
    }

    private static AgentState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AgentState();
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AgentState();
        }

        return JsonSerializer.Deserialize<AgentState>(json) ?? new AgentState();
    }

    public double? LastProcessedDate
    {
        get { lock (_lock) { return _state.LastProcessedDate; } }
        set { lock (_lock) { _state.LastProcessedDate = value; Save(); } }
    }

    public MeetingRecord? GetRecord(string sourceKey)
    {
        lock (_lock)
        {
            return _state.Meetings.TryGetValue(sourceKey, out var record) ? record : null;
        }
    }

    /// <summary>Looks up a meeting's record by its stable meetingId (Transcript.Id) rather than
    /// its sourceKey — used to resolve a related meeting's already-created Notion page id.
    /// Returns null if no record with that meetingId has been persisted (e.g. from before this
    /// field existed, or the meeting hasn't been processed).</summary>
    public MeetingRecord? GetRecordByMeetingId(string meetingId)
    {
        lock (_lock)
        {
            return _state.Meetings.Values.FirstOrDefault(r => r.MeetingId == meetingId);
        }
    }

    /// <summary>True if this source key was already analyzed with the exact same content hash
    /// (i.e. nothing changed since last time it was processed).</summary>
    public bool IsUpToDate(string sourceKey, string contentHash)
    {
        lock (_lock)
        {
            var record = _state.Meetings.TryGetValue(sourceKey, out var r) ? r : null;
            return record != null && record.Analyzed && record.ContentHash == contentHash;
        }
    }

    /// <summary>True if this source key is analyzed, unchanged, AND every optional step that is
    /// actually enabled has already completed successfully — i.e. there is nothing left to do
    /// for this meeting. False whenever analysis itself is stale/missing OR at least one enabled
    /// optional step (Fireflies deletion, Obsidian export, Notion export) is still pending, so
    /// the caller knows to retry just that pending work instead of treating the meeting as
    /// fully done.</summary>
    public bool IsFullyProcessed(
        string sourceKey,
        string contentHash,
        bool needsDeletion,
        bool needsObsidianExport,
        bool needsNotionExport)
    {
        lock (_lock)
        {
            var record = _state.Meetings.TryGetValue(sourceKey, out var r) ? r : null;
            if (record == null || !record.Analyzed || record.ContentHash != contentHash)
            {
                return false;
            }

            if (needsDeletion && !record.DeletedFromFireflies) return false;
            if (needsObsidianExport && !record.ObsidianExported) return false;
            if (needsNotionExport && !record.NotionExported) return false;

            return true;
        }
    }

    public void UpsertRecord(MeetingRecord record)
    {
        lock (_lock)
        {
            _state.Meetings[record.SourceKey] = record;
            Save();
        }
    }

    public void MarkDeleted(string sourceKey)
    {
        lock (_lock)
        {
            if (_state.Meetings.TryGetValue(sourceKey, out var record))
            {
                record.DeletedFromFireflies = true;
                Save();
            }
        }
    }

    public void MarkObsidianExported(string sourceKey)
    {
        lock (_lock)
        {
            if (_state.Meetings.TryGetValue(sourceKey, out var record))
            {
                record.ObsidianExported = true;
                Save();
            }
        }
    }

    public void MarkNotionExported(string sourceKey, string? notionPageId = null)
    {
        lock (_lock)
        {
            if (_state.Meetings.TryGetValue(sourceKey, out var record))
            {
                record.NotionExported = true;
                if (!string.IsNullOrWhiteSpace(notionPageId))
                {
                    record.NotionPageId = notionPageId;
                }
                Save();
            }
        }
    }

    // Callers must already hold `_lock`.
    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });

        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }
}
