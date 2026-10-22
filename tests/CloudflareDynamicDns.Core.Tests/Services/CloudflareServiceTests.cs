using System.Net;
using System.Text.Json;
using CloudflareDynamicDns.Core.Models;
using CloudflareDynamicDns.Core.Services;
using CloudflareDynamicDns.Core.Tests.Helpers;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using static CloudflareDynamicDns.Core.Tests.Helpers.TestHttpResponses;

namespace CloudflareDynamicDns.Core.Tests.Services;

public class CloudflareServiceTests
{
    private const string ApiBaseUrl = "https://api.cloudflare.com/client/v4/";

    private static CloudflareOptions CreateOptions(params string[] domainNames) => new()
    {
        ApiToken = "test-token",
        DomainNames = domainNames.Length == 0 ? ["sub.example.com"] : [.. domainNames],
        IntervalMinutes = 5
    };

    private static CloudflareService CreateService(HttpClient httpClient, CloudflareOptions? options = null) =>
        new(httpClient, Options.Create(options ?? CreateOptions()));

    private static CloudflareResponse ZoneResponse(string zoneId = "zone-1", string zoneName = "example.com") => new()
    {
        Success = true,
        Result =
        [
            new CloudflareDnsRecord { Id = zoneId, Name = zoneName, Content = "1.2.3.4" }
        ]
    };

    private static CloudflareResponse DnsRecordsResponse(
        string id = "record-1",
        string ip = "1.2.3.4",
        string name = "sub.example.com",
        int ttl = 1,
        bool proxied = false) => new()
    {
        Success = true,
        Result =
        [
            new CloudflareDnsRecord { Id = id, Name = name, Content = ip, ZoneId = "zone-1", Ttl = ttl, Proxied = proxied }
        ]
    };

    private static CloudflareResponse EmptyResponse() => new() { Success = true, Result = [] };

    private static CloudflareDnsRecord CreateDnsRecord(string name = "sub.example.com") =>
        new() { Id = "record-1", Name = name, ZoneId = "zone-1" };

    // Constructor

    [Fact]
    public void Constructor_ConfiguresBaseAddressAndBearerToken()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        using var httpClient = new HttpClient(handlerMock.Object);

        CreateService(httpClient);

        Assert.Equal(ApiBaseUrl, httpClient.BaseAddress?.AbsoluteUri);
        Assert.Equal("Bearer test-token", httpClient.DefaultRequestHeaders.Authorization?.ToString());
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenHttpClientIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => CreateService(null!));

        Assert.Equal("httpClient", exception.ParamName);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_SendsBearerTokenWithEveryRequest()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse()),
            () => JsonResponse(DnsRecordsResponse()));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal("Bearer test-token", request.Headers["Authorization"]));
    }

    // SyncDnsRecordsAsync: records

    [Fact]
    public async Task SyncDnsRecordsAsync_ReturnsExistingRecords_WhenRecordsExist()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse()),
            () => JsonResponse(DnsRecordsResponse()));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        var record = Assert.Single(result);
        Assert.Equal("sub.example.com", record.Name);
        Assert.Equal("zone-1", record.ZoneId);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_SetsZoneIdOnReturnedRecords()
    {
        var recordsWithoutZone = new CloudflareResponse
        {
            Success = true,
            Result = [new CloudflareDnsRecord { Id = "record-1", Name = "sub.example.com", Content = "1.2.3.4" }]
        };
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse("zone-42")),
            () => JsonResponse(recordsWithoutZone));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Equal("zone-42", Assert.Single(result).ZoneId);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_RequestsRecordsForDomainInResolvedZone()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse("zone-7")),
            () => JsonResponse(DnsRecordsResponse()));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Equal(HttpMethod.Get, requests[1].Method);
        Assert.Equal($"{ApiBaseUrl}zones/zone-7/dns_records?name=sub.example.com", requests[1].Url);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_CreatesRecordWithExpectedPayload_WhenNoRecordsExist()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse()),
            () => JsonResponse(EmptyResponse()),
            () => StatusResponse(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await service.SyncDnsRecordsAsync("9.8.7.6");

        Assert.Equal(3, requests.Count);
        var createRequest = requests[2];
        Assert.Equal(HttpMethod.Post, createRequest.Method);
        Assert.Equal($"{ApiBaseUrl}zones/zone-1/dns_records", createRequest.Url);

        Assert.NotNull(createRequest.Body);
        using var payload = JsonDocument.Parse(createRequest.Body);
        var root = payload.RootElement;
        Assert.Equal("sub.example.com", root.GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("ttl").GetInt32());
        Assert.Equal("A", root.GetProperty("type").GetString());
        Assert.Equal("9.8.7.6", root.GetProperty("content").GetString());
        Assert.False(root.GetProperty("proxied").GetBoolean());
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_ReturnsEmptyList_AfterSuccessfulCreate_BecauseCreatedRecordIsNotReturned()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse()),
            () => JsonResponse(EmptyResponse()),
            () => StatusResponse(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Empty(result);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_SkipsDomain_WhenCreateRecordFails()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse()),
            () => JsonResponse(EmptyResponse()),
            () => StatusResponse(HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Empty(result);
        Assert.Equal(HttpMethod.Post, requests[^1].Method);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_DoesNotCreateRecord_WhenResponseContainsErrors()
    {
        var responseWithErrors = new CloudflareResponse
        {
            Success = false,
            Errors = ["Authentication error"],
            Result = []
        };
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse()),
            () => JsonResponse(responseWithErrors));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Empty(result);
        Assert.DoesNotContain(requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_SkipsDomain_WhenRecordsBodyIsJsonNull()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse()),
            () => RawJsonResponse("null"));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Empty(result);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_ThrowsHttpRequestException_WhenRecordsRequestFails()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse()),
            () => StatusResponse(HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncDnsRecordsAsync("1.2.3.4"));
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_ReturnsEmptyListWithoutHttpCalls_WhenNoDomainsConfigured()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => StatusResponse(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handlerMock.Object);
        var options = CreateOptions();
        options.DomainNames = [];
        var service = CreateService(httpClient, options);

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Empty(result);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_AggregatesRecordsFromAllDomainsUsingTheirOwnZones()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(request => request.RequestUri?.PathAndQuery switch
        {
            "/client/v4/zones?name=example.com" => JsonResponse(ZoneResponse("zone-1", "example.com")),
            "/client/v4/zones?name=other.org" => JsonResponse(ZoneResponse("zone-2", "other.org")),
            "/client/v4/zones/zone-1/dns_records?name=a.example.com" =>
                JsonResponse(DnsRecordsResponse("record-a", name: "a.example.com")),
            "/client/v4/zones/zone-2/dns_records?name=b.other.org" =>
                JsonResponse(DnsRecordsResponse("record-b", name: "b.other.org")),
            _ => StatusResponse(HttpStatusCode.NotFound)
        });
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient, CreateOptions("a.example.com", "b.other.org"));

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Equal(4, requests.Count);
        Assert.Collection(result,
            first =>
            {
                Assert.Equal("record-a", first.Id);
                Assert.Equal("zone-1", first.ZoneId);
            },
            second =>
            {
                Assert.Equal("record-b", second.Id);
                Assert.Equal("zone-2", second.ZoneId);
            });
    }

    // SyncDnsRecordsAsync: zone lookup

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("sub.example.com", "example.com")]
    [InlineData("a.b.example.com", "example.com")]
    [InlineData("https://sub.example.com", "example.com")]
    [InlineData("https://sub.example.com/some/path", "example.com")]
    [InlineData("localhost", "localhost")]
    public async Task SyncDnsRecordsAsync_QueriesZoneForBaseDomain(string domain, string expectedZoneName)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse(zoneName: expectedZoneName)),
            () => JsonResponse(DnsRecordsResponse()));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient, CreateOptions(domain));

        await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal($"{ApiBaseUrl}zones?name={expectedZoneName}", requests[0].Url);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_MatchesZoneNameCaseInsensitively()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSequence(
            () => JsonResponse(ZoneResponse("zone-9", "EXAMPLE.COM")),
            () => JsonResponse(DnsRecordsResponse()));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var result = await service.SyncDnsRecordsAsync("1.2.3.4");

        Assert.Equal("zone-9", Assert.Single(result).ZoneId);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_ThrowsZoneNotFound_WhenNoZonesAreReturned()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => JsonResponse(EmptyResponse()));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var exception = await Assert.ThrowsAsync<Exception>(() => service.SyncDnsRecordsAsync("1.2.3.4"));

        Assert.Equal("Zone ID not found!", exception.Message);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_ThrowsZoneNotFound_WhenZoneHasDifferentName()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => JsonResponse(ZoneResponse(zoneName: "other.com")));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var exception = await Assert.ThrowsAsync<Exception>(() => service.SyncDnsRecordsAsync("1.2.3.4"));

        Assert.Equal("Zone ID not found!", exception.Message);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_ThrowsZoneNotFound_WhenZoneBodyIsJsonNull()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => RawJsonResponse("null"));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var exception = await Assert.ThrowsAsync<Exception>(() => service.SyncDnsRecordsAsync("1.2.3.4"));

        Assert.Equal("Zone ID not found!", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SyncDnsRecordsAsync_ThrowsHttpRequestException_WhenZoneRequestFails(HttpStatusCode statusCode)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => StatusResponse(statusCode));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncDnsRecordsAsync("1.2.3.4"));

        Assert.Single(requests);
    }

    [Fact]
    public async Task SyncDnsRecordsAsync_ThrowsInvalidDomainName_WhenDomainCannotBeParsed()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => StatusResponse(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient, CreateOptions(""));

        var exception = await Assert.ThrowsAsync<Exception>(() => service.SyncDnsRecordsAsync("1.2.3.4"));

        Assert.Equal("Invalid domain name!", exception.Message);
        Assert.Empty(requests);
    }

    // UpdateIpAddressAsync

    [Fact]
    public async Task UpdateIpAddressAsync_DoesNotUpdate_WhenIpIsUnchanged()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => JsonResponse(DnsRecordsResponse(ip: "1.2.3.4")));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await service.UpdateIpAddressAsync(CreateDnsRecord(), "1.2.3.4");

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task UpdateIpAddressAsync_UpdatesRecord_WhenIpHasChanged()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(DnsRecordsResponse(ip: "1.2.3.4")),
            () => StatusResponse(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await service.UpdateIpAddressAsync(CreateDnsRecord(), "5.6.7.8");

        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal(HttpMethod.Patch, requests[1].Method);
    }

    [Fact]
    public async Task UpdateIpAddressAsync_ListsARecordsByUrlEncodedName()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => JsonResponse(DnsRecordsResponse(ip: "5.6.7.8")));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await service.UpdateIpAddressAsync(CreateDnsRecord("a b.example.com"), "5.6.7.8");

        var request = Assert.Single(requests);
        Assert.Equal($"{ApiBaseUrl}zones/zone-1/dns_records?name=a+b.example.com&type=A", request.Url);
    }

    [Fact]
    public async Task UpdateIpAddressAsync_PatchesExistingRecordKeepingTtlAndProxied()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSequence(
            () => JsonResponse(DnsRecordsResponse(id: "record-9", ip: "1.2.3.4", ttl: 120, proxied: true)),
            () => StatusResponse(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await service.UpdateIpAddressAsync(CreateDnsRecord(), "5.6.7.8");

        var patchRequest = requests[1];
        Assert.Equal(HttpMethod.Patch, patchRequest.Method);
        Assert.Equal($"{ApiBaseUrl}zones/zone-1/dns_records/record-9", patchRequest.Url);

        Assert.NotNull(patchRequest.Body);
        using var payload = JsonDocument.Parse(patchRequest.Body);
        var root = payload.RootElement;
        Assert.Equal("A", root.GetProperty("type").GetString());
        Assert.Equal("sub.example.com", root.GetProperty("name").GetString());
        Assert.Equal("5.6.7.8", root.GetProperty("content").GetString());
        Assert.Equal(120, root.GetProperty("ttl").GetInt32());
        Assert.True(root.GetProperty("proxied").GetBoolean());
    }

    [Fact]
    public async Task UpdateIpAddressAsync_ThrowsHttpRequestException_WhenPatchFails()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSequence(
            () => JsonResponse(DnsRecordsResponse(ip: "1.2.3.4")),
            () => StatusResponse(HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.UpdateIpAddressAsync(CreateDnsRecord(), "5.6.7.8"));
    }

    [Fact]
    public async Task UpdateIpAddressAsync_ThrowsHttpRequestException_WhenListRequestFails()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => StatusResponse(HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.UpdateIpAddressAsync(CreateDnsRecord(), "5.6.7.8"));

        Assert.Single(requests);
    }

    [Fact]
    public async Task UpdateIpAddressAsync_ThrowsNoRecordFound_WhenNoRecordExists()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var requests = handlerMock.SetupSendAsync(_ => JsonResponse(EmptyResponse()));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var exception = await Assert.ThrowsAsync<Exception>(
            () => service.UpdateIpAddressAsync(CreateDnsRecord(), "5.6.7.8"));

        Assert.Contains("No existing A record found", exception.Message);
        Assert.Single(requests);
    }

    [Fact]
    public async Task UpdateIpAddressAsync_ThrowsNoRecordFound_WhenListBodyIsJsonNull()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.SetupSendAsync(_ => RawJsonResponse("null"));
        using var httpClient = new HttpClient(handlerMock.Object);
        var service = CreateService(httpClient);

        var exception = await Assert.ThrowsAsync<Exception>(
            () => service.UpdateIpAddressAsync(CreateDnsRecord(), "5.6.7.8"));

        Assert.Contains("No existing A record found", exception.Message);
    }
}
