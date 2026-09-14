using PracticeConsoleApp.Tests.TestDoubles;

namespace PracticeConsoleApp.Tests;

public class BatchDataFetcherTests
{
    private static List<DataRecord> ToRecords(IReadOnlyList<int> ids) =>
        ids.Select(id => new DataRecord(id, $"Value-{id}")).ToList();

    [Fact]
    public async Task FetchAllAsync_NoIds_ReturnsEmptyWithoutCallingRepository()
    {
        var repo = new FakeDataRepository(_ => throw new InvalidOperationException("should not be called"));
        var fetcher = new BatchDataFetcher(repo);

        var result = await fetcher.FetchAllAsync(new List<int>());

        Assert.Empty(result.Records);
        Assert.Empty(result.Failures);
        Assert.Empty(repo.Calls);
    }

    [Fact]
    public async Task FetchAllAsync_ChunksIdsIntoBatchesOfAtMostBatchSize()
    {
        var ids = Enumerable.Range(1, 25).ToList();
        var repo = new FakeDataRepository(ToRecords);
        var fetcher = new BatchDataFetcher(repo);

        await fetcher.FetchAllAsync(ids, batchSize: 10);

        Assert.Equal(3, repo.Calls.Count); // 10 + 10 + 5
        Assert.All(repo.Calls, batch => Assert.True(batch.Count <= 10));
    }

    [Fact]
    public async Task FetchAllAsync_AllBatchesSucceed_CombinesAllRecords()
    {
        var ids = Enumerable.Range(1, 25).ToList();
        var repo = new FakeDataRepository(ToRecords);
        var fetcher = new BatchDataFetcher(repo);

        var result = await fetcher.FetchAllAsync(ids, batchSize: 10);

        Assert.False(result.HasFailures);
        Assert.Equal(ids.OrderBy(x => x).ToList(), result.Records.Select(r => r.Id).OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task FetchAllAsync_TransientFailure_RetriesAndSucceeds()
    {
        var attempts = 0;
        var repo = new FakeDataRepository(batch =>
        {
            attempts++;
            if (attempts == 1)
                throw new TimeoutException("simulated transient DB timeout");
            return ToRecords(batch);
        });
        var fetcher = new BatchDataFetcher(repo);

        var result = await fetcher.FetchAllAsync(new List<int> { 1, 2, 3 }, maxRetriesPerBatch: 2);

        Assert.False(result.HasFailures);
        Assert.Equal(3, result.Records.Count);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task FetchAllAsync_BatchFailsPermanently_ReportedAsFailureWithoutLosingOtherBatches()
    {
        var repo = new FakeDataRepository(batch =>
            batch[0] == 1
                ? throw new InvalidOperationException("simulated permanent DB failure")
                : ToRecords(batch));
        var fetcher = new BatchDataFetcher(repo);

        var ids = Enumerable.Range(1, 20).ToList(); // batch 1: 1-10 (always fails), batch 2: 11-20 (succeeds)
        var result = await fetcher.FetchAllAsync(ids, batchSize: 10, maxRetriesPerBatch: 1);

        Assert.True(result.HasFailures);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(Enumerable.Range(1, 10).ToList(), failure.Ids.ToList());
        Assert.IsType<InvalidOperationException>(failure.Exception);

        Assert.Equal(
            Enumerable.Range(11, 10).OrderBy(x => x).ToList(),
            result.Records.Select(r => r.Id).OrderBy(x => x).ToList());
    }
}
