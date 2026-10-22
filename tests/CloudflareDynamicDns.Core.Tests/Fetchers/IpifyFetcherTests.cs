using System.Net;
using CloudflareDynamicDns.Core.Fetchers;
using CloudflareDynamicDns.Core.Tests.Helpers;
using Moq;
using Xunit;
using static CloudflareDynamicDns.Core.Tests.Helpers.TestHttpResponses;

namespace CloudflareDynamicDns.Core.Tests.Fetchers;

public class IpifyFetcherTests
{
    [Fact]
    public async Task FetchIpAddressAsync_RequestsIpifyEndpointWithGet_OnSuccess()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => TextResponse("1.2.3.4"));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new IpifyFetcher(httpClient);

        await fetcher.FetchIpAddressAsync();

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.ipify.org/", request.Url);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsTrimmedIpAddress_OnSuccess()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => TextResponse("  1.2.3.4  "));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new IpifyFetcher(httpClient);

        var result = await fetcher.FetchIpAddressAsync();

        Assert.Equal("1.2.3.4", result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsTrimmedIpv6Address_OnSuccess()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => TextResponse("2001:db8::1\n"));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new IpifyFetcher(httpClient);

        var result = await fetcher.FetchIpAddressAsync();

        Assert.Equal("2001:db8::1", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public async Task FetchIpAddressAsync_ReturnsEmptyString_WhenBodyIsEmptyOrWhitespace(string body)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => TextResponse(body));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new IpifyFetcher(httpClient);

        var result = await fetcher.FetchIpAddressAsync();

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_ReturnsErrorMessage_OnHttpFailure()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupThrows(new HttpRequestException("Network error"));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new IpifyFetcher(httpClient);

        var result = await fetcher.FetchIpAddressAsync();

        Assert.StartsWith("Error:", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task FetchIpAddressAsync_ReturnsErrorMessage_OnNonSuccessStatusCode(HttpStatusCode statusCode)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => TextResponse("upstream failure", statusCode));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new IpifyFetcher(httpClient);

        var result = await fetcher.FetchIpAddressAsync();

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task FetchIpAddressAsync_Throws_WhenRequestTimesOut()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupThrows(new TaskCanceledException("The request timed out."));
        using var httpClient = new HttpClient(handlerMock.Object);
        var fetcher = new IpifyFetcher(httpClient);

        await Assert.ThrowsAsync<TaskCanceledException>(() => fetcher.FetchIpAddressAsync());
    }
}
