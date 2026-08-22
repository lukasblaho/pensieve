#nullable enable
// FirefliesClient.csx
// GraphQL client for the Fireflies.ai API (https://api.fireflies.ai/graphql).
// - Fetches transcripts (secondary/optional source), optionally filtered by fromDate.
// - Resolves a transcript ID by title+date, for folder-sourced transcripts that don't carry an
//   embedded Fireflies ID (best-effort; returns null rather than guessing if no confident match).
// - Deletes a transcript by ID (`deleteTranscript` mutation), used by the optional,
//   opt-in "delete from Fireflies after processing" feature.

#load "Logging.csx"
#load "HttpRetry.csx"
#load "Models.csx"

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public sealed class FirefliesClient
{
    private const string Endpoint = "https://api.fireflies.ai/graphql";
    private const int PageSize = 50; // Fireflies API max limit per query

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Logger _logger;

    public FirefliesClient(HttpClient http, string apiKey, Logger logger)
    {
        _http = http;
        _apiKey = apiKey;
        _logger = logger;
    }

    private const string TranscriptsQuery = @"
        query Transcripts($fromDate: DateTime, $limit: Int, $skip: Int) {
          transcripts(fromDate: $fromDate, limit: $limit, skip: $skip) {
            id
            title
            date
            dateString
            transcript_url
            participants
            speakers { id name }
            sentences { index speaker_name text start_time }
          }
        }";

    private const string DeleteTranscriptMutation = @"
        mutation DeleteTranscript($id: String!) {
          deleteTranscript(id: $id) {
            id
            title
          }
        }";

    /// <summary>
    /// Fetches all transcripts created at/after <paramref name="fromDateUtc"/> (or all available
    /// transcripts if null), paginating through the API, and returns them sorted ascending by date.
    /// </summary>
    public async Task<List<Transcript>> FetchTranscriptsSinceAsync(DateTimeOffset? fromDateUtc)
    {
        var all = new List<Transcript>();
        var skip = 0;

        while (true)
        {
            var variables = new Dictionary<string, object?>
            {
                ["fromDate"] = fromDateUtc?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["limit"] = PageSize,
                ["skip"] = skip,
            };

            var page = await FetchPageAsync(variables).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            all.AddRange(page);
            skip += PageSize;

            if (page.Count < PageSize)
            {
                break;
            }
        }

        return all.OrderBy(t => t.Date ?? 0).ToList();
    }

    /// <summary>
    /// Best-effort resolution of a Fireflies transcript ID for a folder-sourced transcript that
    /// has no embedded ID, by matching title (case-insensitive, exact) and date (within
    /// <paramref name="dateToleranceMinutes"/> minutes). Returns null (never guesses) if zero or
    /// more than one transcript matches.
    /// </summary>
    public async Task<string?> TryResolveTranscriptIdAsync(string? title, DateTimeOffset? date, int dateToleranceMinutes = 30)
    {
        if (string.IsNullOrWhiteSpace(title) || date == null)
        {
            return null;
        }

        var fromDate = date.Value.AddMinutes(-dateToleranceMinutes);
        var candidates = await FetchTranscriptsSinceAsync(fromDate).ConfigureAwait(false);

        var toDate = date.Value.AddMinutes(dateToleranceMinutes);
        var matches = candidates
            .Where(t => string.Equals(t.Title?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(t => t.GetDateTimeOffset() is DateTimeOffset d && d >= fromDate && d <= toDate)
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0].Id;
        }

        if (matches.Count > 1)
        {
            _logger.Warn($"FirefliesClient: {matches.Count} ambiguous title+date matches for '{title}'; refusing to guess an ID.");
        }

        return null;
    }

    /// <summary>Deletes a transcript from Fireflies by ID. Irreversible — callers must gate this
    /// behind an explicit opt-in configuration flag.</summary>
    public async Task DeleteTranscriptAsync(string transcriptId)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            query = DeleteTranscriptMutation,
            variables = new Dictionary<string, object?> { ["id"] = transcriptId },
        });

        HttpRequestMessage MakeRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            return req;
        }

        var response = await HttpRetry.SendWithRetryAsync(_http, MakeRequest, _logger, "Fireflies.deleteTranscript")
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Fireflies deleteTranscript API returned {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errorsEl) && errorsEl.ValueKind == JsonValueKind.Array && errorsEl.GetArrayLength() > 0)
        {
            var messages = string.Join("; ", errorsEl.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString()));
            throw new InvalidOperationException($"Fireflies deleteTranscript API returned errors: {messages}");
        }
    }

    private async Task<List<Transcript>> FetchPageAsync(Dictionary<string, object?> variables)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            query = TranscriptsQuery,
            variables
        });

        HttpRequestMessage MakeRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            return req;
        }

        var response = await HttpRetry.SendWithRetryAsync(_http, MakeRequest, _logger, "Fireflies.transcripts")
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Fireflies API returned {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errorsEl) && errorsEl.GetArrayLength() > 0)
        {
            var messages = string.Join("; ", errorsEl.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString()));
            throw new InvalidOperationException($"Fireflies API returned errors: {messages}");
        }

        var results = new List<Transcript>();
        if (!root.TryGetProperty("data", out var dataEl) ||
            !dataEl.TryGetProperty("transcripts", out var transcriptsEl) ||
            transcriptsEl.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in transcriptsEl.EnumerateArray())
        {
            results.Add(ParseTranscript(item));
        }

        return results;
    }

    private static Transcript ParseTranscript(JsonElement item)
    {
        var transcript = new Transcript
        {
            Id = GetString(item, "id") ?? "",
            FirefliesId = GetString(item, "id"),
            Title = GetString(item, "title"),
            DateString = GetString(item, "dateString"),
            TranscriptUrl = GetString(item, "transcript_url"),
            SourceType = TranscriptSourceType.FirefliesApi,
        };

        if (item.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.Number)
        {
            transcript.Date = dateEl.GetDouble();
        }

        if (item.TryGetProperty("participants", out var participantsEl) && participantsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in participantsEl.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.String)
                {
                    transcript.Participants.Add(p.GetString() ?? "");
                }
            }
        }

        if (item.TryGetProperty("speakers", out var speakersEl) && speakersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in speakersEl.EnumerateArray())
            {
                transcript.Speakers.Add(new Speaker
                {
                    Id = GetString(s, "id"),
                    Name = GetString(s, "name"),
                });
            }
        }

        if (item.TryGetProperty("sentences", out var sentencesEl) && sentencesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sentencesEl.EnumerateArray())
            {
                var sentence = new Sentence
                {
                    SpeakerName = GetString(s, "speaker_name"),
                    Text = GetString(s, "text"),
                };
                if (s.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
                {
                    sentence.Index = idxEl.GetInt32();
                }
                if (s.TryGetProperty("start_time", out var stEl) && stEl.ValueKind == JsonValueKind.Number)
                {
                    sentence.StartTime = stEl.GetDouble();
                }
                transcript.Sentences.Add(sentence);
            }
        }

        return transcript;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }
}
