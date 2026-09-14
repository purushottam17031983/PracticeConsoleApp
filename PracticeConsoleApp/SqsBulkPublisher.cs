using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PracticeConsoleApp
{
    public record OutboundMessage(string Id, string Body);

    // A single message's failure inside a batch response.
    // SenderFault = true means the message itself is bad (too large, invalid
    // attributes) - retrying it will never succeed. SenderFault = false means
    // it's a service-side/transient problem - safe to retry.
    public record SqsSendFailure(string MessageId, string ErrorCode, string ErrorMessage, bool SenderFault);

    public record SqsSendBatchResponse(List<string> SuccessfulIds, List<SqsSendFailure> Failed);

    public class SqsThrottlingException : Exception
    {
        public SqsThrottlingException(string message) : base(message) { }
    }

    // Mirrors IAmazonSQS.SendMessageBatchAsync's shape: entries.Successful and
    // entries.Failed on a per-message basis, plus the whole call can throw
    // (throttling, network) before it even gets a response back.
    public interface ISqsBatchClient
    {
        Task<SqsSendBatchResponse> SendMessageBatchAsync(
            string queueUrl, IReadOnlyList<OutboundMessage> batch, CancellationToken ct = default);
    }

    public class SimulatedSqsClient : ISqsBatchClient
    {
        public async Task<SqsSendBatchResponse> SendMessageBatchAsync(
            string queueUrl, IReadOnlyList<OutboundMessage> batch, CancellationToken ct = default)
        {
            await Task.Delay(5, ct); // simulate network round trip
            return new SqsSendBatchResponse(batch.Select(m => m.Id).ToList(), new List<SqsSendFailure>());
        }
    }

    public record SqsPublishResult(List<string> SucceededIds, List<SqsSendFailure> Failures)
    {
        public bool HasFailures => Failures.Count > 0;
    }

    public class SqsBulkPublisher
    {
        // Hard AWS limit - SendMessageBatch rejects more than 10 entries.
        public const int MaxBatchSize = 10;

        private readonly ISqsBatchClient _client;
        private readonly string _queueUrl;

        public SqsBulkPublisher(ISqsBatchClient client, string queueUrl)
        {
            _client = client;
            _queueUrl = queueUrl;
        }

        public async Task<SqsPublishResult> PublishAllAsync(
            IReadOnlyCollection<OutboundMessage> messages,
            int maxDegreeOfParallelism = 25,
            int maxRetries = 3,
            CancellationToken ct = default)
        {
            var succeeded = new ConcurrentBag<string>();
            var permanentFailures = new ConcurrentBag<SqsSendFailure>();

            var pending = messages.ToList();

            for (var attempt = 0; attempt <= maxRetries && pending.Count > 0; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);

                var retryable = new ConcurrentBag<OutboundMessage>();
                var batches = Chunk(pending, MaxBatchSize);
                using var throttle = new SemaphoreSlim(maxDegreeOfParallelism);

                var tasks = batches.Select(async batch =>
                {
                    await throttle.WaitAsync(ct);
                    try
                    {
                        SqsSendBatchResponse response;
                        try
                        {
                            response = await _client.SendMessageBatchAsync(_queueUrl, batch, ct);
                        }
                        catch (Exception)
                        {
                            // Whole call failed (throttling/network) before SQS could
                            // even look at individual messages - the entire batch is retryable.
                            foreach (var m in batch)
                                retryable.Add(m);
                            return;
                        }

                        foreach (var id in response.SuccessfulIds)
                            succeeded.Add(id);

                        foreach (var failure in response.Failed)
                        {
                            if (failure.SenderFault)
                                permanentFailures.Add(failure); // bad message - resending changes nothing
                            else
                                retryable.Add(batch.First(m => m.Id == failure.MessageId));
                        }
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });

                await Task.WhenAll(tasks);
                pending = retryable.ToList();
            }

            // Still pending after exhausting retries -> give up on these too.
            foreach (var m in pending)
                permanentFailures.Add(new SqsSendFailure(m.Id, "MaxRetriesExceeded", "Gave up after retries", SenderFault: false));

            return new SqsPublishResult(succeeded.ToList(), permanentFailures.ToList());
        }

        private static List<List<OutboundMessage>> Chunk(List<OutboundMessage> source, int size)
        {
            var result = new List<List<OutboundMessage>>();
            for (var i = 0; i < source.Count; i += size)
                result.Add(source.GetRange(i, Math.Min(size, source.Count - i)));
            return result;
        }
    }

    // Wraps a client to simulate real-world SQS failure modes for the demo:
    // one batch throttles once then succeeds on retry (transient), one specific
    // message is permanently rejected (e.g. body too large - SenderFault).
    public class FlakySqsClient : ISqsBatchClient
    {
        private readonly ISqsBatchClient _inner;
        private readonly HashSet<string> _permanentFailureMessageIds;
        private readonly HashSet<string> _throttleOnceBatchesContaining;
        private readonly ConcurrentDictionary<string, bool> _alreadyThrottled = new();

        public FlakySqsClient(
            ISqsBatchClient inner,
            IEnumerable<string> permanentFailureMessageIds,
            IEnumerable<string> throttleOnceBatchesContaining)
        {
            _inner = inner;
            _permanentFailureMessageIds = permanentFailureMessageIds.ToHashSet();
            _throttleOnceBatchesContaining = throttleOnceBatchesContaining.ToHashSet();
        }

        public async Task<SqsSendBatchResponse> SendMessageBatchAsync(
            string queueUrl, IReadOnlyList<OutboundMessage> batch, CancellationToken ct = default)
        {
            var triggerId = batch.Select(m => m.Id).FirstOrDefault(id => _throttleOnceBatchesContaining.Contains(id));
            if (triggerId is not null && _alreadyThrottled.TryAdd(triggerId, true))
                throw new SqsThrottlingException("Rate exceeded for queue");

            var response = await _inner.SendMessageBatchAsync(queueUrl, batch, ct);

            var failed = new List<SqsSendFailure>(response.Failed);
            var succeeded = new List<string>();
            foreach (var id in response.SuccessfulIds)
            {
                if (_permanentFailureMessageIds.Contains(id))
                    failed.Add(new SqsSendFailure(id, "MessageTooLong", "Message body exceeds 256KB", SenderFault: true));
                else
                    succeeded.Add(id);
            }

            return new SqsSendBatchResponse(succeeded, failed);
        }
    }
}
