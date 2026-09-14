using System.Collections.Concurrent;

namespace PracticeConsoleApp.Tests.TestDoubles;

// Deterministic stand-in for IDataRepository: every call is recorded and the
// result (or exception) for it comes from a caller-supplied delegate, so
// each test can script exactly the DB behavior it wants to exercise.
internal sealed class FakeDataRepository : IDataRepository
{
    private readonly Func<IReadOnlyList<int>, List<DataRecord>> _handler;

    public ConcurrentBag<IReadOnlyList<int>> Calls { get; } = new();

    public FakeDataRepository(Func<IReadOnlyList<int>, List<DataRecord>> handler)
    {
        _handler = handler;
    }

    public Task<List<DataRecord>> FetchByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        Calls.Add(ids);
        return Task.FromResult(_handler(ids));
    }
}
