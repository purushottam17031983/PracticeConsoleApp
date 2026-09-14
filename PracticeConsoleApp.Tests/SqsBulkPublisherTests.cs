using PracticeConsoleApp.Tests.TestDoubles;

namespace PracticeConsoleApp.Tests;

public class SqsBulkPublisherTests
{
    private static List<OutboundMessage> Messages(int count) =>
        Enumerable.Range(1, count).Select(i => new OutboundMessage(i.ToString(), $"body-{i}")).ToList();

    [Fact]
    public async Task PublishAllAsync_NoMessages_ReturnsEmptyResultWithoutCallingClient()
    {
        var client = new FakeSqsBatchClient(_ => throw new InvalidOperationException("should not be called"));
        var publisher = new SqsBulkPublisher(client, "queue-url");

        var result = await publisher.PublishAllAsync(new List<OutboundMessage>());

        Assert.Empty(result.SucceededIds);
        Assert.Empty(result.Failures);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task PublishAllAsync_AllMessagesSucceed_ReturnsAllAsSucceeded()
    {
        var messages = Messages(25);
        var client = new FakeSqsBatchClient(batch =>
            new SqsSendBatchResponse(batch.Select(m => m.Id).ToList(), new List<SqsSendFailure>()));

        var publisher = new SqsBulkPublisher(client, "queue-url");
        var result = await publisher.PublishAllAsync(messages);

        Assert.False(result.HasFailures);
        Assert.Equal(
            messages.Select(m => m.Id).OrderBy(x => x).ToList(),
            result.SucceededIds.OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task PublishAllAsync_ChunksMessagesIntoBatchesOfAtMostMaxBatchSize()
    {
        var messages = Messages(25);
        var client = new FakeSqsBatchClient(batch =>
            new SqsSendBatchResponse(batch.Select(m => m.Id).ToList(), new List<SqsSendFailure>()));

        var publisher = new SqsBulkPublisher(client, "queue-url");
        await publisher.PublishAllAsync(messages);

        Assert.Equal(3, client.CallCount); // 10 + 10 + 5
        Assert.All(client.Calls, batch => Assert.True(batch.Count <= SqsBulkPublisher.MaxBatchSize));
    }

    [Fact]
    public async Task PublishAllAsync_SenderFaultFailure_IsPermanentAndNeverRetried()
    {
        var messages = Messages(1);
        var client = new FakeSqsBatchClient(_ => new SqsSendBatchResponse(
            new List<string>(),
            new List<SqsSendFailure> { new("1", "MessageTooLong", "Message body exceeds 256KB", SenderFault: true) }));

        var publisher = new SqsBulkPublisher(client, "queue-url");
        var result = await publisher.PublishAllAsync(messages, maxRetries: 3);

        Assert.Empty(result.SucceededIds);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("MessageTooLong", failure.ErrorCode);
        Assert.Equal(1, client.CallCount); // never retried
    }

    [Fact]
    public async Task PublishAllAsync_TransientFailure_RetriesAndEventuallySucceeds()
    {
        var messages = Messages(1);
        var attempt = 0;
        var client = new FakeSqsBatchClient(batch =>
        {
            attempt++;
            return attempt == 1
                ? new SqsSendBatchResponse(
                    new List<string>(),
                    new List<SqsSendFailure> { new("1", "ServiceUnavailable", "try again", SenderFault: false) })
                : new SqsSendBatchResponse(batch.Select(m => m.Id).ToList(), new List<SqsSendFailure>());
        });

        var publisher = new SqsBulkPublisher(client, "queue-url");
        var result = await publisher.PublishAllAsync(messages, maxRetries: 3);

        Assert.Equal(new List<string> { "1" }, result.SucceededIds);
        Assert.Empty(result.Failures);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task PublishAllAsync_WholeBatchThrows_IsRetriedAndCanSucceed()
    {
        var messages = Messages(1);
        var attempt = 0;
        var client = new FakeSqsBatchClient(batch =>
        {
            attempt++;
            if (attempt == 1)
                throw new SqsThrottlingException("Rate exceeded for queue");
            return new SqsSendBatchResponse(batch.Select(m => m.Id).ToList(), new List<SqsSendFailure>());
        });

        var publisher = new SqsBulkPublisher(client, "queue-url");
        var result = await publisher.PublishAllAsync(messages, maxRetries: 3);

        Assert.Equal(new List<string> { "1" }, result.SucceededIds);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task PublishAllAsync_ExceedsMaxRetries_GivesUpAsMaxRetriesExceeded()
    {
        var messages = Messages(1);
        var client = new FakeSqsBatchClient(_ => new SqsSendBatchResponse(
            new List<string>(),
            new List<SqsSendFailure> { new("1", "ServiceUnavailable", "try again", SenderFault: false) }));

        var publisher = new SqsBulkPublisher(client, "queue-url");
        var result = await publisher.PublishAllAsync(messages, maxRetries: 2);

        Assert.Empty(result.SucceededIds);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("MaxRetriesExceeded", failure.ErrorCode);
        Assert.Equal(3, client.CallCount); // initial attempt + 2 retries
    }
}
