using System.Collections.Concurrent;
using System.Text.Json;
using Cove.Plugins;

namespace ResolutionUpgrade;

/// <summary>Persists one <see cref="UpgradeRecord"/> per video in the extension's key-value store.</summary>
internal sealed class UpgradeRecordStore(IExtensionStore store)
{
    private const string KeyPrefix = "result:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Check and download jobs can touch the same video concurrently; updates are read-modify-write.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    // Videos whose download is running in this process right now.
    private readonly ConcurrentDictionary<int, byte> _activeDownloads = new();

    /// <summary>Claims the video for a download; false when another batch is already downloading it.</summary>
    public bool TryBeginDownload(int videoId) => _activeDownloads.TryAdd(videoId, 0);

    public void EndDownload(int videoId) => _activeDownloads.TryRemove(videoId, out _);

    public bool IsDownloading(int videoId) => _activeDownloads.ContainsKey(videoId);

    public async Task<UpgradeRecord?> GetAsync(int videoId, CancellationToken ct)
    {
        var raw = await store.GetAsync(Key(videoId), ct);
        return raw is null ? null : Effective(JsonSerializer.Deserialize<UpgradeRecord>(raw, Json));
    }

    public async Task<IReadOnlyList<UpgradeRecord>> GetAllAsync(CancellationToken ct)
    {
        var entries = await store.GetAllAsync(ct);
        return entries
            .Where(entry => entry.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .Select(entry => Effective(JsonSerializer.Deserialize<UpgradeRecord>(entry.Value, Json)))
            .OfType<UpgradeRecord>()
            .ToList();
    }

    /// <summary>
    /// A record that says it is queued or downloading while no download for it is running was left behind by a
    /// cancelled batch or a restart. Everything reads it as available again, so it can be downloaded without
    /// checking the video a second time.
    /// </summary>
    private UpgradeRecord? Effective(UpgradeRecord? record)
        => record is { State: UpgradeStates.DownloadQueued or UpgradeStates.Downloading } && !IsDownloading(record.VideoId)
            ? record with
            {
                State = UpgradeStates.UpgradeAvailable,
                Message = "The last download stopped before it finished, so this upgrade can be downloaded again.",
            }
            : record;

    public async Task SaveAsync(UpgradeRecord record, CancellationToken ct)
    {
        await WithLockAsync(record.VideoId, () => WriteAsync(record with { UpdatedAt = DateTime.UtcNow }, ct), ct);
    }

    /// <summary>Apply <paramref name="update"/> to the stored record; a null result leaves it unchanged.</summary>
    public async Task<UpgradeRecord?> UpdateAsync(int videoId, Func<UpgradeRecord, UpgradeRecord?> update, CancellationToken ct)
    {
        UpgradeRecord? updated = null;
        await WithLockAsync(videoId, async () =>
        {
            var current = await GetAsync(videoId, ct);
            if (current is null)
                return;

            updated = update(current);
            if (updated is not null)
            {
                updated = updated with { UpdatedAt = DateTime.UtcNow };
                await WriteAsync(updated, ct);
            }
        }, ct);
        return updated;
    }

    public async Task DeleteAsync(int videoId, CancellationToken ct)
    {
        await WithLockAsync(videoId, () => store.DeleteAsync(Key(videoId), ct), ct);
    }

    private Task WriteAsync(UpgradeRecord record, CancellationToken ct)
        => store.SetAsync(Key(record.VideoId), JsonSerializer.Serialize(record, Json), ct);

    private async Task WithLockAsync(int videoId, Func<Task> action, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(videoId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Key(int videoId) => $"{KeyPrefix}{videoId}";
}
