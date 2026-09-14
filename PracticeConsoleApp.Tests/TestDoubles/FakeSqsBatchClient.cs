using System.Collections.Concurrent;

namespace PracticeConsoleApp.Tests.TestDoubles;

// Deterministic stand-in for ISqsBatchClient: every call is recorded and the
// response (or exception) for it comes from a caller-supplied delegate, so
// each test can script exactly the SQS behavior it wants to exercise.
internal sealed class FakeSqsBatchClient : ISqsBatchClient
{
    private readonly Func<IReadOnlyList<OutboundMessage>, SqsSendBatchResponse> _handler;

    public ConcurrentBag<IReadOnlyList<OutboundMessage>> Calls { get; } = new();

    public int CallCount => Calls.Count;

    public FakeSqsBatchClient(Func<IReadOnlyList<OutboundMessage>, SqsSendBatchResponse> handler)
    {
        _handler = handler;
    }

    public Task<SqsSendBatchResponse> SendMessageBatchAsync(
        string queueUrl, IReadOnlyList<OutboundMessage> batch, CancellationToken ct = default)
    {
        Calls.Add(batch);
        return Task.FromResult(_handler(batch));
    }
}
