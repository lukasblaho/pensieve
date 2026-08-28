#nullable enable
// NotionExporter.csx
// Optional (config-gated) export: creates one Notion page per meeting under a configured
// Notion database, using the Notion REST API (https://api.notion.com/v1/pages). Mermaid
// diagrams are rendered as fenced ```mermaid code blocks (Notion has no native Mermaid block
// type). Requires NOTION_API_TOKEN (integration token, shared with the target database) and
// NOTION_DATABASE_ID.

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

public sealed class NotionRelatedMeetingRef
{
    public string Title { get; set; } = "";
    public double? DateEpochMs { get; set; }

    /// <summary>The related meeting's own Notion page id, if it was already exported to Notion
    /// previously. Null when unknown — never guessed, and simply skipped for the native relation
    /// property in that case (it still appears in the plain-text list).</summary>
    public string? NotionPageId { get; set; }
}

public sealed class NotionExporter
{
    private const string Endpoint = "https://api.notion.com/v1/pages";
    private const string NotionVersion = "2022-06-28";

    private readonly HttpClient _http;
    private readonly string _apiToken;
    private readonly string _databaseId;
    private readonly Logger _logger;

    public NotionExporter(HttpClient http, string apiToken, string databaseId, Logger logger)
    {
        _http = http;
        _apiToken = apiToken;
        _databaseId = databaseId;
        _logger = logger;
    }

    /// <summary>Creates a Notion page for this meeting: title, tags (multi-select), meeting/import
    /// dates, and body blocks for summary/agreements/open questions/next actions/diagrams/
    /// keywords/related meetings. <paramref name="relatedMeetings"/> is only non-empty when
    /// ENABLE_MEETING_LINKING is on. When <paramref name="relationPropertyName"/> is non-null
    /// (ENABLE_NOTION_RELATION_LINKS), a native Notion relation property is also set using the
    /// related meetings' already-known Notion page ids (requires that relation column to already
    /// exist in the target database).</summary>
    public async Task<string> ExportAsync(
        Transcript transcript,
        TranscriptAnalysis analysis,
        IReadOnlyList<NotionRelatedMeetingRef>? relatedMeetings = null,
        string? relationPropertyName = null)
    {
        relatedMeetings ??= Array.Empty<NotionRelatedMeetingRef>();
        var title = string.IsNullOrWhiteSpace(transcript.Title) ? "not specified" : transcript.Title;
        var meetingDate = transcript.GetDateTimeOffset();
        var importedAt = DateTimeOffset.UtcNow;

        var properties = new Dictionary<string, object?>
        {
            ["Name"] = new
            {
                title = new object[] { new { text = new { content = title } } }
            },
            ["Tags"] = new
            {
                multi_select = analysis.Tags.Select(t => new { name = KeywordFormatter.ToCamelCase(t) }).ToArray()
            },
            ["Imported At"] = new
            {
                date = new { start = importedAt.ToString("yyyy-MM-ddTHH:mm:ssZ") }
            },
        };

        if (meetingDate.HasValue)
        {
            properties["Meeting Date"] = new
            {
                date = new { start = meetingDate.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") }
            };
        }

        if (!string.IsNullOrWhiteSpace(relationPropertyName))
        {
            var relatedPageIds = relatedMeetings
                .Where(r => !string.IsNullOrWhiteSpace(r.NotionPageId))
                .Select(r => new { id = r.NotionPageId })
                .ToArray();

            if (relatedPageIds.Length > 0)
            {
                properties[relationPropertyName] = new { relation = relatedPageIds };
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["parent"] = new Dictionary<string, object?> { ["database_id"] = _databaseId },
            ["properties"] = properties,
            ["children"] = BuildChildBlocks(transcript, analysis, relatedMeetings),
        };

        var requestBody = JsonSerializer.Serialize(payload);

        HttpRequestMessage MakeRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
            req.Headers.Add("Notion-Version", NotionVersion);
            req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            return req;
        }

        var response = await HttpRetry.SendWithRetryAsync(_http, MakeRequest, _logger, "Notion.createPage").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Notion API returned {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var pageId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        _logger.Info($"NotionExporter: created page '{pageId}' for meeting '{transcript.Id}'.");
        return pageId;
    }

    private static object[] BuildChildBlocks(Transcript transcript, TranscriptAnalysis analysis, IReadOnlyList<NotionRelatedMeetingRef> relatedMeetings)
    {
        var blocks = new List<object>();

        blocks.Add(Heading2("Summary"));
        blocks.Add(Paragraph(string.IsNullOrWhiteSpace(analysis.Summary) ? "not specified" : analysis.Summary));

        blocks.Add(Heading2("Agreements"));
        AddBulletedList(blocks, analysis.Agreements);

        blocks.Add(Heading2("Open Questions"));
        AddBulletedList(blocks, analysis.OpenQuestions);

        blocks.Add(Heading2("Next Actions"));
        if (analysis.NextActions.Count == 0)
        {
            blocks.Add(ToDo("not specified — owner: not specified, due: not specified"));
        }
        else
        {
            foreach (var action in analysis.NextActions)
            {
                var task = string.IsNullOrWhiteSpace(action.Task) ? "not specified" : action.Task;
                blocks.Add(ToDo($"{task} — owner: {action.Owner}, due: {action.Due}"));
            }
        }

        if (analysis.Diagrams.Count > 0)
        {
            blocks.Add(Heading2("Diagrams"));
            foreach (var diagram in analysis.Diagrams)
            {
                blocks.Add(Paragraph(diagram.Title));
                blocks.Add(CodeBlock(diagram.Mermaid, "mermaid"));
            }
        }

        blocks.Add(Heading2("Keywords"));
        blocks.Add(Paragraph(analysis.Keywords.Count == 0 ? "not specified" : string.Join(", ", analysis.Keywords)));

        // Related meetings — purely mechanical links (recurring series or shared tags/keywords),
        // never LLM-derived, so no cross-meeting content is invented here.
        blocks.Add(Heading2("Related Meetings"));
        if (relatedMeetings.Count == 0)
        {
            blocks.Add(Paragraph("not specified"));
        }
        else
        {
            foreach (var related in relatedMeetings)
            {
                var relatedTitle = string.IsNullOrWhiteSpace(related.Title) ? "not specified" : related.Title;
                var relatedDate = related.DateEpochMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)related.DateEpochMs.Value).ToString("yyyy-MM-dd")
                    : "not specified";
                blocks.Add(Paragraph($"{relatedTitle} — {relatedDate}"));
            }
        }

        return blocks.ToArray();
    }

    private static object Heading2(string text) => new
    {
        @object = "block",
        type = "heading_2",
        heading_2 = new { rich_text = new object[] { RichText(text) } }
    };

    private static object Paragraph(string text) => new
    {
        @object = "block",
        type = "paragraph",
        paragraph = new { rich_text = new object[] { RichText(text) } }
    };

    private static object ToDo(string text) => new
    {
        @object = "block",
        type = "to_do",
        to_do = new { rich_text = new object[] { RichText(text) }, @checked = false }
    };

    private static object CodeBlock(string code, string language) => new
    {
        @object = "block",
        type = "code",
        code = new { rich_text = new object[] { RichText(code) }, language }
    };

    private static void AddBulletedList(List<object> blocks, List<string> items)
    {
        if (items.Count == 0)
        {
            blocks.Add(BulletedListItem("not specified"));
            return;
        }
        foreach (var item in items)
        {
            blocks.Add(BulletedListItem(string.IsNullOrWhiteSpace(item) ? "not specified" : item));
        }
    }

    private static object BulletedListItem(string text) => new
    {
        @object = "block",
        type = "bulleted_list_item",
        bulleted_list_item = new { rich_text = new object[] { RichText(text) } }
    };

    private static object RichText(string text) => new { type = "text", text = new { content = Truncate(text) } };

    // Notion enforces a 2000-character limit per rich_text content segment.
    private static string Truncate(string text) => text.Length > 2000 ? text.Substring(0, 2000) : text;
}
