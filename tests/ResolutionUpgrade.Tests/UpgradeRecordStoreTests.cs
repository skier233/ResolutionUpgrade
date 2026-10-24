using Cove.Plugins;

namespace ResolutionUpgrade.Tests;

public sealed class UpgradeRecordStoreTests
{
    [Fact]
    public async Task UpdateAsync_AppliesTheChangeOnlyWhenTheUpdateReturnsARecord()
    {
        var store = new UpgradeRecordStore(new MemoryExtensionStore());
        await store.SaveAsync(new UpgradeRecord { VideoId = 7, State = UpgradeStates.UpgradeAvailable }, CancellationToken.None);

        var skipped = await store.UpdateAsync(7, _ => null, CancellationToken.None);
        var downloaded = await store.UpdateAsync(7, record => record with { State = UpgradeStates.Downloaded }, CancellationToken.None);

        Assert.Null(skipped);
        Assert.Equal(UpgradeStates.Downloaded, downloaded?.State);
        Assert.Equal(UpgradeStates.Downloaded, (await store.GetAsync(7, CancellationToken.None))?.State);
    }

    [Fact]
    public async Task UpdateAsync_DoesNothingForAVideoWithoutARecord()
    {
        var store = new UpgradeRecordStore(new MemoryExtensionStore());

        var updated = await store.UpdateAsync(7, record => record with { State = UpgradeStates.Replaced }, CancellationToken.None);

        Assert.Null(updated);
        Assert.Null(await store.GetAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOnlyUpgradeRecords()
    {
        var backing = new MemoryExtensionStore();
        await backing.SetAsync("unrelated-setting", "{}", CancellationToken.None);
        var store = new UpgradeRecordStore(backing);
        await store.SaveAsync(new UpgradeRecord { VideoId = 1, State = UpgradeStates.UpToDate }, CancellationToken.None);
        await store.SaveAsync(new UpgradeRecord { VideoId = 2, State = UpgradeStates.Downloaded }, CancellationToken.None);
        await store.DeleteAsync(1, CancellationToken.None);

        var record = Assert.Single(await store.GetAllAsync(CancellationToken.None));
        Assert.Equal(2, record.VideoId);
    }

    private sealed class MemoryExtensionStore : IExtensionStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, string>(_values));
    }
}
