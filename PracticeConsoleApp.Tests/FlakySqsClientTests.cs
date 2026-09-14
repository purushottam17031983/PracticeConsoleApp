namespace PracticeConsoleApp.Tests;

public class FlakySqsClientTests
{
    private static List<OutboundMessage> Messages(params string[] ids) =>
        ids.Select(id => new OutboundMessage(id, $"body-{id}")).ToList();

    [Fact]
    public async Task SimulatedSqsClient_SucceedsForEveryMessage()
    {
        var client = new SimulatedSqsClient();

        var response = await client.SendMessageBatchAsync("queue-url", Messages("1", "2", "3"));

        Assert.Equal(new List<string> { "1", "2", "3" }, response.SuccessfulIds);
        Assert.Empty(response.Failed);
    }

    [Fact]
    public async Task SendMessageBatchAsync_ThrottleTrigger_ThrowsOnceThenSucceeds()
    {
        var flaky = new FlakySqsClient(
            new SimulatedSqsClient(),
            permanentFailureMessageIds: Array.Empty<string>(),
            throttleOnceBatchesContaining: new[] { "2" });

        var batch = Messages("1", "2", "3");

        await Assert.ThrowsAsync<SqsThrottlingException>(() => flaky.SendMessageBatchAsync("queue-url", batch));

        var response = await flaky.SendMessageBatchAsync("queue-url", batch); // retried batch now succeeds
        Assert.Equal(new List<string> { "1", "2", "3" }, response.SuccessfulIds);
    }

    [Fact]
    public async Task SendMessageBatchAsync_PermanentFailureMessageId_ReportedAsSenderFault()
    {
        var flaky = new FlakySqsClient(
            new SimulatedSqsClient(),
            permanentFailureMessageIds: new[] { "2" },
            throttleOnceBatchesContaining: Array.Empty<string>());

        var response = await flaky.SendMessageBatchAsync("queue-url", Messages("1", "2", "3"));

        Assert.Equal(new List<string> { "1", "3" }, response.SuccessfulIds);
        var failure = Assert.Single(response.Failed);
        Assert.Equal("2", failure.MessageId);
        Assert.Equal("MessageTooLong", failure.ErrorCode);
        Assert.True(failure.SenderFault);
    }
}
