using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PracticeConsoleApp
{
    // What comes back from the database for a single record.
    public record DataRecord(int Id, string Value);

    // Stands in for a real repository (EF Core / Dapper / ADO.NET etc).
    // A real DB call for "WHERE Id IN (...)" can't take an unbounded number
    // of parameters (SQL Server caps around 2100), so callers must chunk
    // the id list before calling this.
    public interface IDataRepository
    {
        Task<List<DataRecord>> FetchByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
    }

    public class SimulatedDataRepository : IDataRepository
    {
        public async Task<List<DataRecord>> FetchByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        {
            // Simulate network/DB latency for one round trip.
            await Task.Delay(50, ct);
            return ids.Select(id => new DataRecord(id, $"Value-{id}")).ToList();
        }
    }

    // Wraps a repository to simulate real-world failures for demo/testing:
    // one batch fails once then succeeds on retry (transient), another batch
    // fails every time (permanent) so it shows up in BatchFetchResult.Failures.
    public class FlakyDataRepository : IDataRepository
    {
        private readonly IDataRepository _inner;
        private readonly HashSet<int> _transientFailureBatchStarts;
        private readonly HashSet<int> _permanentFailureBatchStarts;
        private readonly ConcurrentDictionary<int, int> _attemptsByBatchStart = new();

        public FlakyDataRepository(
            IDataRepository inner,
            IEnumerable<int> transientFailureBatchStarts,
            IEnumerable<int> permanentFailureBatchStarts)
        {
            _inner = inner;
            _transientFailureBatchStarts = transientFailureBatchStarts.ToHashSet();
            _permanentFailureBatchStarts = permanentFailureBatchStarts.ToHashSet();
        }

        public async Task<List<DataRecord>> FetchByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        {
            var batchKey = ids[0];

            if (_permanentFailureBatchStarts.Contains(batchKey))
                throw new InvalidOperationException($"Simulated permanent DB failure for batch starting at {batchKey}");

            if (_transientFailureBatchStarts.Contains(batchKey))
            {
                var attempt = _attemptsByBatchStart.AddOrUpdate(batchKey, 1, (_, n) => n + 1);
                if (attempt == 1)
                    throw new TimeoutException($"Simulated transient DB timeout for batch starting at {batchKey}");
            }

            return await _inner.FetchByIdsAsync(ids, ct);
        }
    }

    // A batch that failed after retries, with the ids so the caller can decide
    // whether to retry them, log them, or surface an error to the user.
    public record BatchFailure(IReadOnlyList<int> Ids, Exception Exception);

    // Partial-success result: whatever batches succeeded, plus whatever failed.
    public record BatchFetchResult(List<DataRecord> Records, List<BatchFailure> Failures)
    {
        public bool HasFailures => Failures.Count > 0;
    }

    // Splits an arbitrarily large request into batches, fetches each batch
    // from the database, and combines everything back into one result set.
    public class BatchDataFetcher
    {
        private readonly IDataRepository _repository;

        public BatchDataFetcher(IDataRepository repository)
        {
            _repository = repository;
        }

        public async Task<BatchFetchResult> FetchAllAsync(
            IReadOnlyCollection<int> requestedIds,
            int batchSize = 1000,
            int maxDegreeOfParallelism = 4,
            int maxRetriesPerBatch = 2,
            CancellationToken ct = default)
        {
            if (requestedIds.Count == 0)
                return new BatchFetchResult(new List<DataRecord>(), new List<BatchFailure>());

            var batches = Chunk(requestedIds, batchSize);

            var combined = new ConcurrentBag<DataRecord>();
            var failures = new ConcurrentBag<BatchFailure>();
            using var throttle = new SemaphoreSlim(maxDegreeOfParallelism);

            var tasks = batches.Select(async batch =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    // Catch here, per batch - a Task.WhenAll over tasks that
                    // can throw only surfaces the first exception and abandons
                    // the rest of the results, even though those batches
                    // already succeeded. Swallowing into `failures` instead
                    // lets every other batch finish and keeps their data.
                    var batchResult = await FetchWithRetryAsync(batch, maxRetriesPerBatch, ct);
                    foreach (var record in batchResult)
                        combined.Add(record);
                }
                catch (Exception ex) when (ct.IsCancellationRequested is false)
                {
                    failures.Add(new BatchFailure(batch, ex));
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

            return new BatchFetchResult(combined.ToList(), failures.ToList());
        }

        private async Task<List<DataRecord>> FetchWithRetryAsync(
            IReadOnlyList<int> batch, int maxRetries, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await _repository.FetchByIdsAsync(batch, ct);
                }
                catch (Exception) when (attempt < maxRetries)
                {
                    // Transient failure (timeout, deadlock, connection drop) -
                    // back off and try this same batch again before giving up on it.
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)), ct);
                }
            }
        }

        private static List<List<int>> Chunk(IReadOnlyCollection<int> source, int batchSize)
        {
            var result = new List<List<int>>();
            var current = new List<int>(batchSize);

            foreach (var id in source)
            {
                current.Add(id);
                if (current.Count == batchSize)
                {
                    result.Add(current);
                    current = new List<int>(batchSize);
                }
            }

            if (current.Count > 0)
                result.Add(current);

            return result;
        }
    }
}
