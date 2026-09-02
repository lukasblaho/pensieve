#nullable enable
// MeetingIndexStore.csx
// Maintains data/meetings-index.json: a pure, mechanical per-meeting index (title, date, output
// folder, tags, keywords, series key) used solely to compute related-meeting links (see
// MeetingLinker.csx). Like GlobalVocabularyStore, this store is NEVER re-summarized or
// re-analyzed by the LLM — it only records what each transcript's own (already-validated)
// analysis produced, preserving the no-hallucination guarantee across meetings. Only populated
// when ENABLE_MEETING_LINKING is turned on.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class MeetingIndexEntry
{
    [JsonPropertyName("meetingId")]
    public string MeetingId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Meeting date as epoch milliseconds, matching Transcript.Date. Null when unknown.</summary>
    [JsonPropertyName("dateEpochMs")]
    public double? DateEpochMs { get; set; }

    [JsonPropertyName("folderPath")]
    public string FolderPath { get; set; } = "";

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    [JsonPropertyName("seriesKey")]
    public string SeriesKey { get; set; } = "";
}

public sealed class MeetingIndex
{
    [JsonPropertyName("meetings")]
    public Dictionary<string, MeetingIndexEntry> Meetings { get; set; } = new();
}

public sealed class MeetingIndexStore
{
    private readonly string _path;
    private readonly MeetingIndex _index;
    // Guards all reads/mutations below; see GlobalVocabularyStore for why this is needed.
    private readonly object _lock = new object();

    public MeetingIndexStore(string path)
    {
        _path = path;
        _index = Load(path);
    }

    private static MeetingIndex Load(string path)
    {
        if (!File.Exists(path))
        {
            return new MeetingIndex();
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MeetingIndex();
        }

        return JsonSerializer.Deserialize<MeetingIndex>(json) ?? new MeetingIndex();
    }

    /// <summary>All indexed meeting entries (used as candidates for related-meeting matching).
    /// Not guaranteed to be in any particular order.</summary>
    public IReadOnlyList<MeetingIndexEntry> All()
    {
        lock (_lock) { return _index.Meetings.Values.ToList(); }
    }

    /// <summary>Adds or replaces this meeting's index entry (idempotent by meetingId) and
    /// persists immediately.</summary>
    public void AddOrUpdate(MeetingIndexEntry entry)
    {
        lock (_lock)
        {
            _index.Meetings[entry.MeetingId] = entry;
            Save();
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

        var json = JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = true });

        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }
}
