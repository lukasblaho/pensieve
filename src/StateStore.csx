#nullable enable
// StateStore.csx
// Persists per-meeting processing state to data/state.json: which source (file path or
// Fireflies ID) has been analyzed, its content hash (for change detection), the generated
// meeting folder, the resolved Fireflies ID, and independent delete/export status flags so
// each step (analysis, deletion, Obsidian export, Notion export) is idempotent and retryable.
// Writes are atomic (temp file + rename) to avoid corruption on crash.

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
        get => _state.LastProcessedDate;
        set { _state.LastProcessedDate = value; Save(); }
    }

    public MeetingRecord? GetRecord(string sourceKey)
    {
        return _state.Meetings.TryGetValue(sourceKey, out var record) ? record : null;
    }

    /// <summary>Looks up a meeting's record by its stable meetingId (Transcript.Id) rather than
    /// its sourceKey — used to resolve a related meeting's already-created Notion page id.
    /// Returns null if no record with that meetingId has been persisted (e.g. from before this
    /// field existed, or the meeting hasn't been processed).</summary>
    public MeetingRecord? GetRecordByMeetingId(string meetingId)
    {
        return _state.Meetings.Values.FirstOrDefault(r => r.MeetingId == meetingId);
    }

    /// <summary>True if this source key was already analyzed with the exact same content hash
    /// (i.e. nothing changed since last time it was processed).</summary>
    public bool IsUpToDate(string sourceKey, string contentHash)
    {
        var record = GetRecord(sourceKey);
        return record != null && record.Analyzed && record.ContentHash == contentHash;
    }

    public void UpsertRecord(MeetingRecord record)
    {
        _state.Meetings[record.SourceKey] = record;
        Save();
    }

    public void MarkDeleted(string sourceKey)
    {
        if (_state.Meetings.TryGetValue(sourceKey, out var record))
        {
            record.DeletedFromFireflies = true;
            Save();
        }
    }

    public void MarkObsidianExported(string sourceKey)
    {
        if (_state.Meetings.TryGetValue(sourceKey, out var record))
        {
            record.ObsidianExported = true;
            Save();
        }
    }

    public void MarkNotionExported(string sourceKey, string? notionPageId = null)
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
