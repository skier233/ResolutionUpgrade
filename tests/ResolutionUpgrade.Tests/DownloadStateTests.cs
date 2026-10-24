using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using JobProgress = Cove.Core.Interfaces.IJobProgress;

namespace ResolutionUpgrade.Tests;

public sealed class DownloadStateTests
{
    [Fact]
    public async Task ARecordLeftDownloadingReadsAsAvailableUnlessItsDownloadIsRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new UpgradeRecordStore(new MemoryExtensionStore());
        await store.SaveAsync(Available(5) with { State = UpgradeStates.Downloading }, ct);
        await store.SaveAsync(Available(6) with { State = UpgradeStates.DownloadQueued }, ct);

        var stale = await store.GetAsync(5, ct);
        Assert.Equal(UpgradeStates.UpgradeAvailable, stale?.State);
        Assert.Contains("stopped before it finished", stale?.Message);
        Assert.Equal(UpgradeStates.UpgradeAvailable, (await store.GetAsync(6, ct))?.State);
        Assert.All(await store.GetAllAsync(ct), record => Assert.Equal(UpgradeStates.UpgradeAvailable, record.State));

        Assert.True(store.TryBeginDownload(5));
        Assert.False(store.TryBeginDownload(5));
        Assert.Equal(UpgradeStates.Downloading, (await store.GetAsync(5, ct))?.State);

        store.EndDownload(5);
        Assert.Equal(UpgradeStates.UpgradeAvailable, (await store.GetAsync(5, ct))?.State);
    }

    [Fact]
    public async Task CancellingABatchOnlyTouchesTheVideoThatStartedAndLeavesItReadyToDownloadAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new UpgradeRecordStore(new MemoryExtensionStore());
        foreach (var videoId in new[] { 1, 2, 3 })
            await store.SaveAsync(Available(videoId), ct);
        var downloader = new BlockingDownloader();
        var workflow = new UpgradeWorkflow(store, null!, downloader, NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();

        var run = workflow.DownloadAsync([1, 2, 3], concurrency: 1, makePrimary: false, deleteOldFile: false, new SilentProgress(), cancellation.Token);
        await downloader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Only the video whose download actually started says so; the rest are untouched.
        Assert.Equal(UpgradeStates.Downloading, (await store.GetAsync(1, ct))?.State);
        Assert.Equal(UpgradeStates.UpgradeAvailable, (await store.GetAsync(2, ct))?.State);
        Assert.Equal(UpgradeStates.UpgradeAvailable, (await store.GetAsync(3, ct))?.State);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var first = await store.GetAsync(1, ct);
        Assert.Equal(UpgradeStates.UpgradeAvailable, first?.State);
        Assert.Contains("cancelled", first?.Message);
        Assert.False(store.IsDownloading(1));
        Assert.Equal(1, downloader.Calls);
        Assert.All(await store.GetAllAsync(ct), record => Assert.True(UpgradeStates.CanDownload(record.State)));
    }

    [Fact]
    public async Task ARecordLeftDownloadingByAnEarlierRunCanBeDownloadedWithoutCheckingAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new UpgradeRecordStore(new MemoryExtensionStore());
        await store.SaveAsync(Available(9) with { State = UpgradeStates.Downloading }, ct);
        var downloader = new FinishingDownloader();
        var workflow = new UpgradeWorkflow(store, null!, downloader, NullLogger.Instance);

        await workflow.DownloadAsync([9], concurrency: 1, makePrimary: false, deleteOldFile: false, new SilentProgress(), ct);

        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task AVideoAlreadyDownloadingInAnotherBatchIsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new UpgradeRecordStore(new MemoryExtensionStore());
        await store.SaveAsync(Available(4), ct);
        Assert.True(store.TryBeginDownload(4));
        var downloader = new FinishingDownloader();
        var workflow = new UpgradeWorkflow(store, null!, downloader, NullLogger.Instance);

        await workflow.DownloadAsync([4], concurrency: 1, makePrimary: false, deleteOldFile: false, new SilentProgress(), ct);

        Assert.Equal(0, downloader.Calls);
        Assert.True(store.IsDownloading(4));
    }

    private static UpgradeRecord Available(int videoId) => new()
    {
        VideoId = videoId,
        State = UpgradeStates.UpgradeAvailable,
        Title = $"Video {videoId}",
        Url = $"https://example.test/{videoId}",
        DownloaderId = "cove.community.downloaders.ytdlp/video",
        QualityId = "2160p",
    };

    private sealed class BlockingDownloader : IDownloaderService
    {
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => _calls;

        public Task<IReadOnlyList<DownloaderMatchDto>> MatchUrlAsync(string url, CancellationToken ct) => throw new NotSupportedException();

        public async Task<DownloaderImportResult?> DownloadAndImportAsync(DownloaderImportRequest request, JobProgress? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    /// <summary>Finishes immediately without a file, which the workflow records as a failed download.</summary>
    private sealed class FinishingDownloader : IDownloaderService
    {
        private int _calls;

        public int Calls => _calls;

        public Task<IReadOnlyList<DownloaderMatchDto>> MatchUrlAsync(string url, CancellationToken ct) => throw new NotSupportedException();

        public Task<DownloaderImportResult?> DownloadAndImportAsync(DownloaderImportRequest request, JobProgress? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<DownloaderImportResult?>(null);
        }
    }

    private sealed class SilentProgress : JobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
    }

    private sealed class MemoryExtensionStore : IExtensionStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            lock (_values)
                return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            lock (_values)
                _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            lock (_values)
                _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        {
            lock (_values)
                return Task.FromResult(new Dictionary<string, string>(_values));
        }
    }
}
