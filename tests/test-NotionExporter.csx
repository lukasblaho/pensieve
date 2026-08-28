#nullable enable
// test-NotionExporter.csx
// Verifies the Notion export builds a valid create-page request (title, tags as multi-select,
// summary/agreements/open-questions/next-actions/diagrams-as-mermaid-code-blocks/keywords as
// child blocks) using a mocked HttpMessageHandler (no real network calls).

#load "TestKit.csx"
#load "../src/NotionExporter.csx"
#load "../src/Logging.csx"
#load "../src/Models.csx"

using System;
using System.IO;
using System.Net;
using System.Net.Http;

TestKit.Section("NotionExporter: creates a page with title, tags, and analysis content as child blocks");
{
    var successResponse = "{\"id\":\"page-123\"}";
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, successResponse);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var exporter = new NotionExporter(httpClient, "fake-token", "fake-db-id", logger);

    var transcript = new Transcript { Id = "t1", Title = "Weekly Sync", Date = 1700000000000 };
    var analysis = new TranscriptAnalysis
    {
        Summary = "This is the summary.",
        Agreements = new System.Collections.Generic.List<string> { "Ship on Friday." },
        Tags = new System.Collections.Generic.List<string> { "release" },
        Keywords = new System.Collections.Generic.List<string> { "sprint" },
        Diagrams = new System.Collections.Generic.List<DiagramItem> { new DiagramItem { Title = "Flow", Mermaid = "graph TD; A-->B;" } },
    };

    var pageId = await exporter.ExportAsync(transcript, analysis);

    TestKit.Assert(pageId == "page-123", "should return the created page id from the Notion API response");
    TestKit.Assert(handler.LastRequest!.Headers.Authorization!.Parameter == "fake-token", "request should use the configured API token");
    TestKit.Assert(handler.LastRequest!.Headers.Contains("Notion-Version"), "request should include the Notion-Version header");
    TestKit.Assert(handler.LastRequestBody!.Contains("fake-db-id"), "request body should target the configured database id");
    TestKit.Assert(handler.LastRequestBody!.Contains("Weekly Sync"), "request body should include the meeting title");
    TestKit.Assert(handler.LastRequestBody!.Contains("release"), "request body should include tags");
    TestKit.Assert(handler.LastRequestBody!.Contains("Ship on Friday."), "request body should include agreements content");
    TestKit.Assert(handler.LastRequestBody!.Contains("mermaid"), "request body should render diagrams as mermaid code blocks");
    TestKit.Assert(handler.LastRequestBody!.Contains("\"Meeting Date\""), "request body should include the meeting date property");
    TestKit.Assert(handler.LastRequestBody!.Contains("\"Imported At\""), "request body should include the imported-at date property");
    TestKit.Assert(handler.LastRequestBody!.Contains("Related Meetings"), "request body should always include a Related Meetings block, even when empty");
}

TestKit.Section("NotionExporter: surfaces related meetings as a text block and (when a relation property name is given) a native relation property");
{
    var successResponse = "{\"id\":\"page-456\"}";
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, successResponse);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var exporter = new NotionExporter(httpClient, "fake-token", "fake-db-id", logger);

    var transcript = new Transcript { Id = "t2", Title = "Daily Standup - Aug 30", Date = 1700000000000 };
    var analysis = new TranscriptAnalysis { Summary = "x" };
    var relatedMeetings = new System.Collections.Generic.List<NotionRelatedMeetingRef>
    {
        new NotionRelatedMeetingRef { Title = "Daily Standup - Aug 29", DateEpochMs = 1699900000000, NotionPageId = "notion-page-old" },
        new NotionRelatedMeetingRef { Title = "Daily Standup - Aug 28", DateEpochMs = 1699800000000, NotionPageId = null },
    };

    var pageId = await exporter.ExportAsync(transcript, analysis, relatedMeetings, relationPropertyName: "Related Meetings");

    TestKit.Assert(pageId == "page-456", "should still return the created page id");
    TestKit.Assert(handler.LastRequestBody!.Contains("Daily Standup - Aug 29"), "request body should include the related meeting's title in the text block");
    TestKit.Assert(handler.LastRequestBody!.Contains("\"relation\""), "request body should include a native Notion relation property");
    TestKit.Assert(handler.LastRequestBody!.Contains("notion-page-old"), "the relation property should reference the related meeting's known Notion page id");
}

TestKit.Section("NotionExporter: omits the relation property entirely when no related meeting has a known Notion page id");
{
    var successResponse = "{\"id\":\"page-789\"}";
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, successResponse);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var exporter = new NotionExporter(httpClient, "fake-token", "fake-db-id", logger);

    var transcript = new Transcript { Id = "t3", Title = "Daily Standup - Aug 30" };
    var analysis = new TranscriptAnalysis { Summary = "x" };
    var relatedMeetings = new System.Collections.Generic.List<NotionRelatedMeetingRef>
    {
        new NotionRelatedMeetingRef { Title = "Daily Standup - Aug 29", DateEpochMs = 1699900000000, NotionPageId = null },
    };

    await exporter.ExportAsync(transcript, analysis, relatedMeetings, relationPropertyName: "Related Meetings");

    TestKit.Assert(!handler.LastRequestBody!.Contains("\"relation\""), "relation property should be omitted entirely when no related meeting has a known Notion page id yet");
}

TestKit.Section("NotionExporter: surfaces API errors as exceptions instead of silently failing");
{
    var errorResponse = "{\"message\":\"invalid database id\"}";
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.BadRequest, errorResponse);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var exporter = new NotionExporter(httpClient, "fake-token", "bad-db-id", logger);

    var transcript = new Transcript { Id = "t1", Title = "Weekly Sync" };
    var analysis = new TranscriptAnalysis { Summary = "x" };

    var threw = false;
    try
    {
        await exporter.ExportAsync(transcript, analysis);
    }
    catch (InvalidOperationException ex)
    {
        threw = ex.Message.Contains("invalid database id") || ex.Message.Contains("400");
    }

    TestKit.Assert(threw, "should throw on a non-success Notion API response");
}
