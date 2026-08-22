#nullable enable
// TestKit.csx
// Minimal shared test utilities for the custom .csx test runner: assertions + a fake
// HttpMessageHandler for mocking Fireflies/OpenAI HTTP responses without real network calls.

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public static class TestKit
{
    public static int Passed = 0;
    public static int Failed = 0;

    public static void Assert(bool condition, string message)
    {
        if (condition)
        {
            Passed++;
            Console.WriteLine($"  \u2713 {message}");
        }
        else
        {
            Failed++;
            Console.WriteLine($"  \u2717 FAILED: {message}");
        }
    }

    public static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"== {name} ==");
    }
}

/// <summary>
/// A minimal fake HttpMessageHandler that returns a fixed response (or a sequence of
/// responses) regardless of the request, so HttpClient-based clients can be tested
/// without hitting real endpoints.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public int CallCount { get; private set; } = 0;
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static FakeHttpMessageHandler Returning(HttpStatusCode statusCode, string body)
    {
        return new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body)
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        if (request.Content != null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        return _responder(request);
    }
}
