using System.Net;
using System.Net.Http.Json;
using System.Text;
using Moq;
using Moq.Protected;

namespace CloudflareDynamicDns.Core.Tests.Helpers;

/// <summary>
/// Snapshot of an outgoing request. The body is read eagerly so it can be asserted after the
/// request and its content have been disposed by the HttpClient.
/// </summary>
public sealed record CapturedRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body)
{
    public string Url => Uri?.AbsoluteUri ?? string.Empty;
}

/// <summary>
/// Shared helpers for faking the HttpMessageHandler behind an HttpClient. Every setup returns the
/// list of requests that the handler received, in order.
/// </summary>
public static class HttpMessageHandlerMockExtensions
{
    /// <summary>
    /// Answers every request with a response built by <paramref name="responseFactory"/>. The factory is
    /// invoked once per request so a response (and its content) is never shared between calls.
    /// </summary>
    public static IReadOnlyList<CapturedRequest> SetupSendAsync(
        this Mock<HttpMessageHandler> handlerMock,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var captured = new List<CapturedRequest>();

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage request, CancellationToken _) =>
            {
                var snapshot = await CaptureAsync(request);
                lock (captured)
                {
                    captured.Add(snapshot);
                }

                return responseFactory(request);
            });

        return captured;
    }

    /// <summary>
    /// Answers the first request with the first factory, the second request with the second factory, and so on.
    /// A request beyond the configured responses fails the call with an InvalidOperationException.
    /// </summary>
    public static IReadOnlyList<CapturedRequest> SetupSequence(
        this Mock<HttpMessageHandler> handlerMock,
        params Func<HttpResponseMessage>[] responseFactories)
    {
        var calls = 0;

        return handlerMock.SetupSendAsync(_ =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            if (index >= responseFactories.Length)
            {
                throw new InvalidOperationException(
                    $"Unexpected request #{index + 1}; only {responseFactories.Length} response(s) were configured.");
            }

            return responseFactories[index]();
        });
    }

    /// <summary>
    /// Makes every request fail with <paramref name="exception"/>.
    /// </summary>
    public static IReadOnlyList<CapturedRequest> SetupThrows(
        this Mock<HttpMessageHandler> handlerMock,
        Exception exception)
    {
        var captured = new List<CapturedRequest>();

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage request, CancellationToken _) =>
            {
                var snapshot = await CaptureAsync(request);
                lock (captured)
                {
                    captured.Add(snapshot);
                }

                throw exception;
            });

        return captured;
    }

    private static async Task<CapturedRequest> CaptureAsync(HttpRequestMessage request)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();

        return new CapturedRequest(request.Method, request.RequestUri, headers, body);
    }
}

/// <summary>
/// Builders for the responses returned by the faked HttpMessageHandler.
/// </summary>
public static class TestHttpResponses
{
    public static HttpResponseMessage JsonResponse(object? content, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = JsonContent.Create(content)
        };

    public static HttpResponseMessage RawJsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage TextResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(content)
        };

    public static HttpResponseMessage StatusResponse(HttpStatusCode statusCode) =>
        new(statusCode);
}
