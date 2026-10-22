using System.Net;
using CloudflareDynamicDns.Core.Fetchers;
using CloudflareDynamicDns.Core.Tests.Helpers;
using Moq;
using Xunit;
using static CloudflareDynamicDns.Core.Tests.Helpers.TestHttpResponses;

namespace CloudflareDynamicDns.Core.Tests.Fetchers;

public class CloudflareDiagnosticsFetcherTests
{
    private static async Task<string> FetchAsync(string traceResponse)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => TextResponse(traceResponse));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new CloudflareDiagnosticsFetcher(httpClient);

        return await fetcher.FetchIpAddressAsync();
    }

    [Fact]
    public async Task FetchIpAddressAsync_RequestsTraceEndpointWithGet_OnSuccess()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => TextResponse("ip=1.2.3.4\n"));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new CloudflareDiagnosticsFetcher(httpClient);

        await fetcher.FetchIpAddressAsync();

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://1.1.1.1/cdn-cgi/trace", request.Url);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsIpAddress_OnSuccess()
    {
        var result = await FetchAsync("fl=1abc\nip=1.2.3.4\nts=1234567890\n");

        Assert.Equal("1.2.3.4", result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsIpAddress_WhenIpLineIsFirst()
    {
        var result = await FetchAsync("ip=1.2.3.4\nfl=1abc\nts=1234567890\n");

        Assert.Equal("1.2.3.4", result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsIpAddress_WhenIpLineIsLastWithoutTrailingNewline()
    {
        var result = await FetchAsync("fl=1abc\nts=1234567890\nip=1.2.3.4");

        Assert.Equal("1.2.3.4", result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsTrimmedIpAddress_WhenLinesEndWithCarriageReturn()
    {
        var result = await FetchAsync("fl=1abc\r\nip=1.2.3.4\r\nts=1234567890\r\n");

        Assert.Equal("1.2.3.4", result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsIpv6Address_OnSuccess()
    {
        var result = await FetchAsync("fl=1abc\nip=2001:db8::1\nts=1234567890\n");

        Assert.Equal("2001:db8::1", result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsEmpty_OnHttpFailure()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupThrows(new HttpRequestException("Network error"));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new CloudflareDiagnosticsFetcher(httpClient);

        var result = await fetcher.FetchIpAddressAsync();

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FetchIpAddressAsync_ReturnsEmpty_OnNonSuccessStatusCode(HttpStatusCode statusCode)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => TextResponse("ip=1.2.3.4\n", statusCode));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new CloudflareDiagnosticsFetcher(httpClient);

        var result = await fetcher.FetchIpAddressAsync();

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsEmpty_WhenIpLineIsMissing()
    {
        var result = await FetchAsync("fl=1abc\nts=1234567890\n");

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   ")]
    public async Task FetchIpAddressAsync_ReturnsEmpty_WhenBodyIsEmptyOrWhitespace(string body)
    {
        var result = await FetchAsync(body);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_IgnoresLineThatContainsButDoesNotStartWithIpKey()
    {
        var result = await FetchAsync("fl=1abc\nxip=9.9.9.9\nts=1234567890\n");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsIpFromIpLine_WhenEarlierLineOnlyContainsIpKey()
    {
        var result = await FetchAsync("xip=9.9.9.9\nip=1.2.3.4\n");

        Assert.Equal("1.2.3.4", result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_Throws_WhenRequestTimesOut()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupThrows(new TaskCanceledException("The request timed out."));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new CloudflareDiagnosticsFetcher(httpClient);

        await Assert.ThrowsAsync<TaskCanceledException>(() => fetcher.FetchIpAddressAsync());
    }
}
