using System;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.SQS;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PracticeConsoleApp
{
    // The Lambda-hosted version of Program.cs's LambdaBulkSqsPushAsync demo.
    // Deployed handler string: PracticeConsoleApp.SqsBulkPushFunction::PracticeConsoleApp.SqsBulkPushFunction::FunctionHandler
    public class SqsBulkPushFunction
    {
        public record Request(int MessageCount);

        // AmazonSQSClient is expensive to construct (resolves credentials,
        // opens connections) - build it once per execution environment and
        // reuse it across warm invocations instead of per-call.
        private static readonly AmazonSQSClient SqsClient = new();

        public async Task<string> FunctionHandler(Request request, ILambdaContext context)
        {
            var queueUrl = Environment.GetEnvironmentVariable("ORDERS_QUEUE_URL")
                ?? throw new InvalidOperationException("ORDERS_QUEUE_URL environment variable is not set");

            var messages = Enumerable.Range(1, request.MessageCount)
                .Select(i => new OutboundMessage(i.ToString(), $"{{\"orderId\":{i}}}"))
                .ToList();

            var publisher = new SqsBulkPublisher(new AwsSqsBatchClient(SqsClient), queueUrl);
            var result = await publisher.PublishAllAsync(messages, maxDegreeOfParallelism: 50, maxRetries: 3);

            context.Logger.LogInformation(
                $"Sent {result.SucceededIds.Count}/{request.MessageCount} messages, {result.Failures.Count} permanent failures");

            if (result.HasFailures)
            {
                foreach (var failure in result.Failures.Take(20)) // cap log volume
                    context.Logger.LogWarning($"  {failure.MessageId}: {failure.ErrorCode} - {failure.ErrorMessage}");
            }

            return $"Sent {result.SucceededIds.Count}/{request.MessageCount} messages, {result.Failures.Count} permanent failures";
        }
    }
}
