using Amazon.SQS;
using Amazon.SQS.Model;
using Moq;

namespace PracticeConsoleApp.Tests;

public class AwsSqsBatchClientTests
{
    private static List<OutboundMessage> TwoMessages() =>
        new() { new("1", "body-1"), new("2", "body-2") };

    [Fact]
    public async Task SendMessageBatchAsync_BuildsRequestFromOutboundMessages()
    {
        SendMessageBatchRequest? capturedRequest = null;

        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessageBatchRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new SendMessageBatchResponse
            {
                Successful = new List<SendMessageBatchResultEntry>(),
                Failed = new List<BatchResultErrorEntry>()
            });

        var client = new AwsSqsBatchClient(sqs.Object);

        await client.SendMessageBatchAsync("https://sqs.example/queue", TwoMessages());

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://sqs.example/queue", capturedRequest!.QueueUrl);
        Assert.Equal(2, capturedRequest.Entries.Count);
        Assert.Equal("1", capturedRequest.Entries[0].Id);
        Assert.Equal("body-1", capturedRequest.Entries[0].MessageBody);
        Assert.Equal("2", capturedRequest.Entries[1].Id);
        Assert.Equal("body-2", capturedRequest.Entries[1].MessageBody);
    }

    [Fact]
    public async Task SendMessageBatchAsync_MapsSuccessfulEntriesToIds()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageBatchResponse
            {
                Successful = new List<SendMessageBatchResultEntry>
                {
                    new() { Id = "1" },
                    new() { Id = "2" },
                },
                Failed = new List<BatchResultErrorEntry>()
            });

        var client = new AwsSqsBatchClient(sqs.Object);

        var result = await client.SendMessageBatchAsync("queue-url", TwoMessages());

        Assert.Equal(new List<string> { "1", "2" }, result.SuccessfulIds);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task SendMessageBatchAsync_MapsFailedEntries_NullSenderFaultBecomesFalse()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageBatchResponse
            {
                Successful = new List<SendMessageBatchResultEntry>(),
                Failed = new List<BatchResultErrorEntry>
                {
                    new() { Id = "1", Code = "MessageTooLong", Message = "too big", SenderFault = true },
                    new() { Id = "2", Code = "ServiceUnavailable", Message = "try again", SenderFault = null },
                }
            });

        var client = new AwsSqsBatchClient(sqs.Object);

        var result = await client.SendMessageBatchAsync("queue-url", TwoMessages());

        Assert.Empty(result.SuccessfulIds);
        Assert.Equal(2, result.Failed.Count);

        var first = result.Failed.Single(f => f.MessageId == "1");
        Assert.Equal("MessageTooLong", first.ErrorCode);
        Assert.True(first.SenderFault);

        var second = result.Failed.Single(f => f.MessageId == "2");
        Assert.Equal("ServiceUnavailable", second.ErrorCode);
        Assert.False(second.SenderFault); // null SenderFault from the SDK maps to false
    }

    [Fact]
    public async Task SendMessageBatchAsync_ClientThrows_PropagatesToCaller()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSQSException("Rate exceeded"));

        var client = new AwsSqsBatchClient(sqs.Object);

        await Assert.ThrowsAsync<AmazonSQSException>(
            () => client.SendMessageBatchAsync("queue-url", TwoMessages()));
    }
}
