#nullable enable
// test-FirefliesClient.csx
// Verifies GraphQL query building/pagination behavior, JSON parsing, title+date ID resolution
// (never guessing when ambiguous), and the deleteTranscript mutation, all using a mocked
// HttpMessageHandler (no real network calls).

#load "TestKit.csx"
#load "../src/FirefliesClient.csx"
#load "../src/Logging.csx"

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

TestKit.Section("FirefliesClient: parses transcripts from fixture JSON");
{
    var fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "test-fixtures", "sample-transcript.json");
    var fixtureJson = File.ReadAllText(fixturePath);

    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, fixtureJson);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var client = new FirefliesClient(httpClient, "fake-key", logger);

    var transcripts = await client.FetchTranscriptsSinceAsync(null);

    TestKit.Assert(transcripts.Count == 1, "should parse exactly 1 transcript from fixture");
    TestKit.Assert(transcripts[0].Id == "abc123", "transcript id should be parsed correctly");
    TestKit.Assert(transcripts[0].FirefliesId == "abc123", "FirefliesId should mirror the API id for API-sourced transcripts");
    TestKit.Assert(transcripts[0].Title == "Weekly Sync", "transcript title should be parsed correctly");
    TestKit.Assert(transcripts[0].Participants.Contains("alice@example.com"), "participants should include alice@example.com");
    TestKit.Assert(transcripts[0].Sentences.Count == 3, "should parse all 3 sentences");
    TestKit.Assert(transcripts[0].Sentences[0].SpeakerName == "Alice", "first sentence speaker should be Alice");
    TestKit.Assert(transcripts[0].TranscriptUrl == "https://app.fireflies.ai/view/abc123", "transcript_url should be parsed correctly");

    TestKit.Assert(handler.LastRequest!.Headers.Authorization!.Scheme == "Bearer", "request should use ****** scheme");
    TestKit.Assert(handler.LastRequest!.Headers.Authorization!.Parameter == "fake-key", "request should send the configured API key");
    TestKit.Assert(handler.LastRequestBody!.Contains("query Transcripts"), "request body should contain the transcripts GraphQL query");
}

TestKit.Section("FirefliesClient: pagination stops when a page returns fewer than page size");
{
    var emptyResponse = "{\"data\":{\"transcripts\":[]}}";
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, emptyResponse);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var client = new FirefliesClient(httpClient, "fake-key", logger);

    var transcripts = await client.FetchTranscriptsSinceAsync(null);

    TestKit.Assert(transcripts.Count == 0, "should return empty list when API returns no transcripts");
    TestKit.Assert(handler.CallCount == 1, "should only call the API once when first page is empty");
}

TestKit.Section("FirefliesClient: surfaces GraphQL errors as exceptions");
{
    var errorResponse = "{\"errors\":[{\"message\":\"auth_failed\"}]}";
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, errorResponse);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var client = new FirefliesClient(httpClient, "fake-key", logger);

    var threw = false;
    try
    {
        await client.FetchTranscriptsSinceAsync(null);
    }
    catch (InvalidOperationException ex)
    {
        threw = ex.Message.Contains("auth_failed");
    }
    TestKit.Assert(threw, "should throw an InvalidOperationException surfacing the GraphQL error message");
}

TestKit.Section("FirefliesClient: TryResolveTranscriptIdAsync returns the ID on an exact single title+date match");
{
    var fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "test-fixtures", "sample-transcript.json");
    var fixtureJson = File.ReadAllText(fixturePath);
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, fixtureJson);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var client = new FirefliesClient(httpClient, "fake-key", logger);

    var date = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
    var resolvedId = await client.TryResolveTranscriptIdAsync("Weekly Sync", date);

    TestKit.Assert(resolvedId == "abc123", "should resolve the ID when exactly one transcript matches title+date");
}

TestKit.Section("FirefliesClient: TryResolveTranscriptIdAsync refuses to guess when there is no match");
{
    var fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "test-fixtures", "sample-transcript.json");
    var fixtureJson = File.ReadAllText(fixturePath);
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, fixtureJson);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var client = new FirefliesClient(httpClient, "fake-key", logger);

    var date = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
    var resolvedId = await client.TryResolveTranscriptIdAsync("Completely Different Title", date);

    TestKit.Assert(resolvedId == null, "should return null (never guess) when no transcript matches title+date");
}

TestKit.Section("FirefliesClient: DeleteTranscriptAsync sends the deleteTranscript mutation and succeeds on a valid response");
{
    var deleteResponse = "{\"data\":{\"deleteTranscript\":{\"id\":\"abc123\",\"title\":\"Weekly Sync\"}}}";
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, deleteResponse);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var client = new FirefliesClient(httpClient, "fake-key", logger);

    var threw = false;
    try
    {
        await client.DeleteTranscriptAsync("abc123");
    }
    catch
    {
        threw = true;
    }

    TestKit.Assert(!threw, "DeleteTranscriptAsync should not throw on a successful response");
    TestKit.Assert(handler.LastRequestBody!.Contains("deleteTranscript"), "request body should contain the deleteTranscript mutation");
    TestKit.Assert(handler.LastRequestBody!.Contains("abc123"), "request body should contain the transcript id being deleted");
}

TestKit.Section("FirefliesClient: DeleteTranscriptAsync surfaces GraphQL errors as exceptions");
{
    var errorResponse = "{\"errors\":[{\"message\":\"require_elevated_privilege\"}]}";
    var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, errorResponse);
    var httpClient = new HttpClient(handler);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var client = new FirefliesClient(httpClient, "fake-key", logger);

    var threw = false;
    try
    {
        await client.DeleteTranscriptAsync("abc123");
    }
    catch (InvalidOperationException ex)
    {
        threw = ex.Message.Contains("require_elevated_privilege");
    }

    TestKit.Assert(threw, "should surface deleteTranscript GraphQL errors as an exception, never silently ignore");
}
