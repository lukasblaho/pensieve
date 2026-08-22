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

    /// <summary>Creates a Notion page for this meeting: title, tags (multi-select), and body
    /// blocks for summary/agreements/open questions/next actions/diagrams/keywords.</summary>
    public async Task<string> ExportAsync(Transcript transcript, TranscriptAnalysis analysis)
    {
        var title = string.IsNullOrWhiteSpace(transcript.Title) ? "not specified" : transcript.Title;

        var payload = new Dictionary<string, object?>
        {
            ["parent"] = new Dictionary<string, object?> { ["database_id"] = _databaseId },
            ["properties"] = new Dictionary<string, object?>
            {
                ["Name"] = new
                {
                    title = new object[] { new { text = new { content = title } } }
                },
                ["Tags"] = new
                {
                    multi_select = analysis.Tags.Select(t => new { name = KeywordFormatter.ToCamelCase(t) }).ToArray()
                },
            },
            ["children"] = BuildChildBlocks(transcript, analysis),
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

    private static object[] BuildChildBlocks(Transcript transcript, TranscriptAnalysis analysis)
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
