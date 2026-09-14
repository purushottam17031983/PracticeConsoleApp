using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace PracticeConsoleApp
{
    // Real implementation of ISqsBatchClient, backed by the AWS SDK.
    // Same shape as SimulatedSqsClient/FlakySqsClient - SqsBulkPublisher
    // doesn't need to change at all to use this instead.
    public class AwsSqsBatchClient : ISqsBatchClient
    {
        private readonly IAmazonSQS _sqs;

        public AwsSqsBatchClient(IAmazonSQS sqs)
        {
            _sqs = sqs;
        }

        public async Task<SqsSendBatchResponse> SendMessageBatchAsync(
            string queueUrl, IReadOnlyList<OutboundMessage> batch, CancellationToken ct = default)
        {
            var request = new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = batch.Select(m => new SendMessageBatchRequestEntry
                {
                    Id = m.Id, // must be unique within this one batch (not globally)
                    MessageBody = m.Body,

                    // FIFO queues only - remove both for a standard queue:
                    // MessageGroupId = "orders",
                    // MessageDeduplicationId = m.Id,
                }).ToList()
            };

            // A throttling/network problem throws here (caught by
            // SqsBulkPublisher, which retries the whole batch) rather than
            // showing up in the response below.
            var response = await _sqs.SendMessageBatchAsync(request, ct);

            var failures = response.Failed.Select(f => new SqsSendFailure(
                f.Id, f.Code, f.Message, f.SenderFault ?? false)).ToList();

            return new SqsSendBatchResponse(response.Successful.Select(s => s.Id).ToList(), failures);
        }
    }
}
