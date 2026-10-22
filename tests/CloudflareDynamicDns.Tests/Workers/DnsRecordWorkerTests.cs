using CloudflareDynamicDns.Core.Fetchers.Interfaces;
using CloudflareDynamicDns.Core.Models;
using CloudflareDynamicDns.Core.Services.Interfaces;
using CloudflareDynamicDns.Workers;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CloudflareDynamicDns.Tests.Workers;

public class DnsRecordWorkerTests
{
    private static IOptions<CloudflareOptions> CreateOptions(int intervalMinutes = 0) =>
        Options.Create(new CloudflareOptions
        {
            ApiToken = "test-token",
            DomainNames = ["sub.example.com"],
            IntervalMinutes = intervalMinutes
        });

    private static CloudflareDnsRecord CreateRecord(string? ip, string name = "sub.example.com", string id = "record-1") =>
        new() { Id = id, Name = name, Content = ip, ZoneId = "zone-1" };

    /// <summary>
    /// Creates a token source that also cancels by itself after a while, so a worker that
    /// fails to stop cannot hang the test run.
    /// </summary>
    private static CancellationTokenSource CreateCancellationTokenSource() =>
        new(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Returns <paramref name="ip"/> from every call to FetchIpAddressAsync and cancels the
    /// token on call number <paramref name="cancelOnCall"/>, so the worker stops after that
    /// iteration (Task.Delay with a cancelled token exits).
    /// </summary>
    private static Mock<IPublicIpAddressFetcher> SetupFetcher(
        CancellationTokenSource cts,
        string ip = "5.6.7.8",
        int cancelOnCall = 1)
    {
        var calls = 0;
        var fetcherMock = new Mock<IPublicIpAddressFetcher>();
        fetcherMock
            .Setup(f => f.FetchIpAddressAsync())
            .ReturnsAsync(() =>
            {
                if (++calls >= cancelOnCall)
                {
                    cts.Cancel();
                }

                return ip;
            });
        return fetcherMock;
    }

    private static Mock<ICloudflareService> SetupService(string ip, params CloudflareDnsRecord[] records)
    {
        var serviceMock = new Mock<ICloudflareService>();
        serviceMock
            .Setup(s => s.SyncDnsRecordsAsync(ip))
            .ReturnsAsync([.. records]);
        return serviceMock;
    }

    /// <summary>
    /// Starts the worker and waits until it stops, ignoring the cancellation that ends the loop.
    /// Any other exception from the worker is propagated to the caller.
    /// </summary>
    private static async Task RunWorkerUntilStoppedAsync(DnsRecordWorker worker, CancellationToken token)
    {
        await worker.StartAsync(token);
        try { await worker.ExecuteTask!; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ExecuteAsync_CallsUpdateIpAddress_ForRecordsWithOutdatedIp()
    {
        const string currentIp = "5.6.7.8";
        const string outdatedIp = "1.2.3.4";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, currentIp);
        var serviceMock = SetupService(currentIp, CreateRecord(outdatedIp));

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.Is<CloudflareDnsRecord>(r => r.Content == outdatedIp), currentIp), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotCallUpdateIpAddress_WhenIpIsUnchanged()
    {
        const string currentIp = "1.2.3.4";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, currentIp);
        var serviceMock = SetupService(currentIp, CreateRecord(currentIp));

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.IsAny<CloudflareDnsRecord>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotCallUpdateIpAddress_WhenNoRecordsReturned()
    {
        const string currentIp = "1.2.3.4";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, currentIp);
        var serviceMock = SetupService(currentIp);

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.IsAny<CloudflareDnsRecord>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_StopsImmediately_WhenAlreadyCancelled()
    {
        var fetcherMock = new Mock<IPublicIpAddressFetcher>();
        var serviceMock = new Mock<ICloudflareService>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        fetcherMock.Verify(f => f.FetchIpAddressAsync(), Times.Never);
        serviceMock.Verify(s => s.SyncDnsRecordsAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesOnlyOutdatedRecords_WhenSomeRecordsAreCurrent()
    {
        const string currentIp = "5.6.7.8";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, currentIp);
        var serviceMock = SetupService(currentIp,
            CreateRecord("1.2.3.4", "outdated-1.example.com", "record-1"),
            CreateRecord(currentIp, "current.example.com", "record-2"),
            CreateRecord("9.9.9.9", "outdated-2.example.com", "record-3"));

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.Is<CloudflareDnsRecord>(r => r.Id == "record-1"), currentIp), Times.Once);
        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.Is<CloudflareDnsRecord>(r => r.Id == "record-3"), currentIp), Times.Once);
        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.IsAny<CloudflareDnsRecord>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_CallsUpdateIpAddress_ForRecordWithNullContent()
    {
        const string currentIp = "5.6.7.8";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, currentIp);
        var serviceMock = SetupService(currentIp, CreateRecord(null));

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.Is<CloudflareDnsRecord>(r => r.Content == null), currentIp), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FetchesIpOncePerIteration_AndSyncsWithThatIp()
    {
        const string currentIp = "5.6.7.8";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, currentIp);
        var serviceMock = SetupService(currentIp, CreateRecord("1.2.3.4"));

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        fetcherMock.Verify(f => f.FetchIpAddressAsync(), Times.Once);
        serviceMock.Verify(s => s.SyncDnsRecordsAsync(currentIp), Times.Once);
        serviceMock.Verify(s => s.SyncDnsRecordsAsync(It.Is<string>(ip => ip != currentIp)), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: Could not resolve public IP.")]
    public async Task ExecuteAsync_PassesFetcherResultToSync_EvenWhenItIsNotAnIpAddress(string fetchedValue)
    {
        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, fetchedValue);
        var serviceMock = SetupService(fetchedValue);

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        serviceMock.Verify(s => s.SyncDnsRecordsAsync(fetchedValue), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Faults_WhenSyncThrows_AndDoesNotUpdateAnyRecord()
    {
        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts);
        var serviceMock = new Mock<ICloudflareService>();
        serviceMock
            .Setup(s => s.SyncDnsRecordsAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("sync failed"));

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunWorkerUntilStoppedAsync(worker, cts.Token));

        Assert.Equal("sync failed", exception.Message);
        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.IsAny<CloudflareDnsRecord>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Faults_WhenUpdateThrows_AndSkipsRemainingRecords()
    {
        const string currentIp = "5.6.7.8";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, currentIp);
        var serviceMock = SetupService(currentIp,
            CreateRecord("1.2.3.4", "first.example.com", "record-1"),
            CreateRecord("1.2.3.4", "second.example.com", "record-2"));
        serviceMock
            .Setup(s => s.UpdateIpAddressAsync(It.Is<CloudflareDnsRecord>(r => r.Id == "record-1"), currentIp))
            .ThrowsAsync(new InvalidOperationException("update failed"));

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunWorkerUntilStoppedAsync(worker, cts.Token));

        Assert.Equal("update failed", exception.Message);
        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.IsAny<CloudflareDnsRecord>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RunsAnotherIteration_UntilTokenIsCancelled()
    {
        const string currentIp = "5.6.7.8";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = SetupFetcher(cts, currentIp, cancelOnCall: 2);
        var serviceMock = SetupService(currentIp, CreateRecord("1.2.3.4"));

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions(intervalMinutes: 0));
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        fetcherMock.Verify(f => f.FetchIpAddressAsync(), Times.Exactly(2));
        serviceMock.Verify(s => s.SyncDnsRecordsAsync(currentIp), Times.Exactly(2));
        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.IsAny<CloudflareDnsRecord>(), currentIp), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_StopsDuringDelay_WithoutFetchingAgain_WhenCancelledAfterUpdate()
    {
        const string currentIp = "5.6.7.8";

        using var cts = CreateCancellationTokenSource();
        var fetcherMock = new Mock<IPublicIpAddressFetcher>();
        fetcherMock
            .Setup(f => f.FetchIpAddressAsync())
            .ReturnsAsync(currentIp);
        var serviceMock = SetupService(currentIp, CreateRecord("1.2.3.4"));
        serviceMock
            .Setup(s => s.UpdateIpAddressAsync(It.IsAny<CloudflareDnsRecord>(), It.IsAny<string>()))
            .Callback(() => cts.Cancel())
            .Returns(Task.CompletedTask);

        var worker = new DnsRecordWorker(fetcherMock.Object, serviceMock.Object, CreateOptions(intervalMinutes: 1));
        await RunWorkerUntilStoppedAsync(worker, cts.Token);

        fetcherMock.Verify(f => f.FetchIpAddressAsync(), Times.Once);
        serviceMock.Verify(s => s.UpdateIpAddressAsync(
            It.IsAny<CloudflareDnsRecord>(), currentIp), Times.Once);
    }
}
