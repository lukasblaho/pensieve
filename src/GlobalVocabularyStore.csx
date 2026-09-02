#nullable enable
// GlobalVocabularyStore.csx
// Maintains data/vocabulary.json: a pure, mechanical aggregation of the tags/keywords already
// produced per-meeting by CopilotCliClient. This store is NEVER itself re-summarized or
// re-analyzed by the LLM — it only counts/links what each transcript's own (already-validated)
// analysis produced, preserving the no-hallucination guarantee across meetings.
//
// The user can hand-edit an "aliases" map in vocabulary.json (canonical term -> list of
// misspelled/variant terms) to merge AI misspellings into a single canonical entry. On every
// AddMeeting call, incoming tags/keywords are first resolved through this map (case-insensitive)
// before being counted, so corrections normalize future occurrences automatically. The app never
// clears or overwrites the user's aliases — it only ever reads them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class VocabularyEntry
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("meetingIds")]
    public List<string> MeetingIds { get; set; } = new();
}

public sealed class GlobalVocabulary
{
    [JsonPropertyName("keywords")]
    public Dictionary<string, VocabularyEntry> Keywords { get; set; } = new();

    [JsonPropertyName("tags")]
    public Dictionary<string, VocabularyEntry> Tags { get; set; } = new();

    /// <summary>User-editable: canonical term -> list of misspelled/variant terms that should be
    /// merged into it. Never written to by the app except to persist the user's own edits
    /// unchanged; the app only reads this to resolve incoming terms.</summary>
    [JsonPropertyName("aliases")]
    public Dictionary<string, List<string>> Aliases { get; set; } = new();
}

public sealed class GlobalVocabularyStore
{
    private readonly string _path;
    private readonly GlobalVocabulary _vocabulary;
    private readonly Dictionary<string, string> _aliasLookup;
    // Guards all reads/mutations below. `watch` mode can invoke processing for multiple
    // different transcript files concurrently (one FileSystemWatcher debounce Timer per file),
    // and this store's Dictionaries are not otherwise thread-safe.
    private readonly object _lock = new object();

    public GlobalVocabularyStore(string path)
    {
        _path = path;
        _vocabulary = Load(path);
        _aliasLookup = BuildAliasLookup(_vocabulary.Aliases);
    }

    private static GlobalVocabulary Load(string path)
    {
        if (!File.Exists(path))
        {
            return new GlobalVocabulary();
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GlobalVocabulary();
        }

        return JsonSerializer.Deserialize<GlobalVocabulary>(json) ?? new GlobalVocabulary();
    }

    /// <summary>Builds a case-insensitive variant -> canonical lookup from the user-maintained
    /// aliases map.</summary>
    private static Dictionary<string, string> BuildAliasLookup(Dictionary<string, List<string>> aliases)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, variants) in aliases)
        {
            if (string.IsNullOrWhiteSpace(canonical)) continue;
            var canonicalNormalized = canonical.Trim().ToLowerInvariant();
            foreach (var variant in variants)
            {
                if (string.IsNullOrWhiteSpace(variant)) continue;
                lookup[variant.Trim()] = canonicalNormalized;
            }
        }
        return lookup;
    }

    /// <summary>Appends this meeting's already-generated tags/keywords into the global vocabulary.
    /// Idempotent per meetingId: re-running for the same meetingId will not double-count it.
    /// Incoming terms are first resolved through the user-maintained aliases map so known
    /// misspellings/variants are merged into their canonical entry.</summary>
    public void AddMeeting(string meetingId, IEnumerable<string> tags, IEnumerable<string> keywords)
    {
        lock (_lock)
        {
            AddTerms(_vocabulary.Tags, tags, meetingId);
            AddTerms(_vocabulary.Keywords, keywords, meetingId);
            Save();
        }
    }

    private void AddTerms(Dictionary<string, VocabularyEntry> dict, IEnumerable<string> terms, string meetingId)
    {
        foreach (var rawTerm in terms)
        {
            if (string.IsNullOrWhiteSpace(rawTerm)) continue;
            var term = rawTerm.Trim().ToLowerInvariant();

            if (_aliasLookup.TryGetValue(term, out var canonical))
            {
                term = canonical;
            }

            if (!dict.TryGetValue(term, out var entry))
            {
                entry = new VocabularyEntry();
                dict[term] = entry;
            }

            if (!entry.MeetingIds.Contains(meetingId))
            {
                entry.MeetingIds.Add(meetingId);
                entry.Count = entry.MeetingIds.Count;
            }
        }
    }

    public GlobalVocabulary Snapshot()
    {
        lock (_lock) { return _vocabulary; }
    }

    // Callers must already hold `_lock`.
    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_vocabulary, new JsonSerializerOptions { WriteIndented = true });

        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }
}
