namespace PracticeConsoleApp.Tests;

public class FlakyDataRepositoryTests
{
    [Fact]
    public async Task SimulatedDataRepository_ReturnsARecordPerRequestedId()
    {
        var repo = new SimulatedDataRepository();

        var records = await repo.FetchByIdsAsync(new List<int> { 1, 2, 3 });

        Assert.Equal(new List<int> { 1, 2, 3 }, records.Select(r => r.Id).ToList());
        Assert.All(records, r => Assert.Equal($"Value-{r.Id}", r.Value));
    }

    [Fact]
    public async Task FetchByIdsAsync_TransientFailureBatch_ThrowsOnceThenSucceeds()
    {
        var flaky = new FlakyDataRepository(
            new SimulatedDataRepository(),
            transientFailureBatchStarts: new[] { 1 },
            permanentFailureBatchStarts: Array.Empty<int>());

        var ids = new List<int> { 1, 2, 3 };

        await Assert.ThrowsAsync<TimeoutException>(() => flaky.FetchByIdsAsync(ids));

        var records = await flaky.FetchByIdsAsync(ids); // retrying the same batch now succeeds
        Assert.Equal(3, records.Count);
    }

    [Fact]
    public async Task FetchByIdsAsync_PermanentFailureBatch_AlwaysThrows()
    {
        var flaky = new FlakyDataRepository(
            new SimulatedDataRepository(),
            transientFailureBatchStarts: Array.Empty<int>(),
            permanentFailureBatchStarts: new[] { 1 });

        var ids = new List<int> { 1, 2, 3 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => flaky.FetchByIdsAsync(ids));
        await Assert.ThrowsAsync<InvalidOperationException>(() => flaky.FetchByIdsAsync(ids)); // never recovers
    }

    [Fact]
    public async Task FetchByIdsAsync_BatchNotFlagged_DelegatesStraightThrough()
    {
        var flaky = new FlakyDataRepository(
            new SimulatedDataRepository(),
            transientFailureBatchStarts: new[] { 999 },
            permanentFailureBatchStarts: new[] { 888 });

        var records = await flaky.FetchByIdsAsync(new List<int> { 1, 2 });

        Assert.Equal(2, records.Count);
    }
}
