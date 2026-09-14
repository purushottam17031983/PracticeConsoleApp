using PracticeConsoleApp;

var fetcher = new BatchDataFetcher(new SimulatedDataRepository());

await RunAsync(fetcher, requestSize: 10000);
await RunAsync(fetcher, requestSize: 3457); // size varies from call to call

await RunWithFailuresAsync();

await LambdaBulkSqsPushAsync();
await LambdaBulkSqsPushWithFailuresAsync();

async Task RunAsync(BatchDataFetcher f, int requestSize)
{
    var ids = Enumerable.Range(1, requestSize).ToList();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await f.FetchAllAsync(ids, batchSize: 1000, maxDegreeOfParallelism: 4);
    sw.Stop();

    Console.WriteLine($"Requested {requestSize} ids -> got {result.Records.Count} combined records in {sw.ElapsedMilliseconds} ms");
}

async Task RunWithFailuresAsync()
{
    Console.WriteLine();
    Console.WriteLine("-- Simulating a DB failure inside one batch --");

    const int requestSize = 5000;
    const int batchSize = 1000;
    var ids = Enumerable.Range(1, requestSize).ToList();

    // Batch 2 (ids 1001-2000) times out once then succeeds on retry.
    // Batch 4 (ids 3001-4000) fails every time - a real, permanent failure.
    var flakyRepo = new FlakyDataRepository(
        new SimulatedDataRepository(),
        transientFailureBatchStarts: new[] { 1001 },
        permanentFailureBatchStarts: new[] { 3001 });

    var flakyFetcher = new BatchDataFetcher(flakyRepo);
    var result = await flakyFetcher.FetchAllAsync(ids, batchSize: batchSize, maxDegreeOfParallelism: 4, maxRetriesPerBatch: 2);

    Console.WriteLine($"Requested {requestSize} ids -> got {result.Records.Count} records back, {result.Failures.Count} batch(es) failed");

    foreach (var failure in result.Failures)
    {
        var firstId = failure.Ids[0];
        var lastId = failure.Ids[^1];
        Console.WriteLine($"  Batch [{firstId}-{lastId}] failed: {failure.Exception.GetType().Name} - {failure.Exception.Message}");
    }

    if (result.HasFailures)
    {
        // Caller's choice: retry just the failed ids, log and continue with
        // partial data, or throw to fail the whole request - here we retry once.
        var idsToRetry = result.Failures.SelectMany(f => f.Ids).ToList();
        Console.WriteLine($"Retrying {idsToRetry.Count} ids from failed batches...");

        var retryResult = await flakyFetcher.FetchAllAsync(idsToRetry, batchSize: batchSize, maxDegreeOfParallelism: 4, maxRetriesPerBatch: 0);
        Console.WriteLine($"Retry -> got {retryResult.Records.Count} records, {retryResult.Failures.Count} still failing");
    }
}

// Simulates a Lambda handler that fans out ~1,000,000 messages to SQS.
// SendMessageBatch caps at 10 messages/call, so this is really ~100,000 API
// calls - too slow one-at-a-time inside Lambda's execution window, so they
// go out concurrently, throttled to a sane number in flight at once.
async Task LambdaBulkSqsPushAsync()
{
    Console.WriteLine();
    Console.WriteLine("-- Lambda pushing 1,000,000 messages to SQS --");

    const int messageCount = 1_000_000;
    var messages = Enumerable.Range(1, messageCount)
        .Select(i => new OutboundMessage(i.ToString(), $"{{\"orderId\":{i}}}"))
        .ToList();

    var publisher = new SqsBulkPublisher(new SimulatedSqsClient(), queueUrl: "https://sqs.us-east-1.amazonaws.com/123456789012/orders-queue");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await publisher.PublishAllAsync(messages, maxDegreeOfParallelism: 200, maxRetries: 3);
    sw.Stop();

    Console.WriteLine($"Sent {result.SucceededIds.Count}/{messageCount} messages ({Math.Ceiling(messageCount / (double)SqsBulkPublisher.MaxBatchSize)} batch calls) in {sw.ElapsedMilliseconds} ms, {result.Failures.Count} permanent failures");
}

// Same scenario, but one message is permanently rejected (e.g. body too big)
// and one batch gets throttled once before succeeding on retry - shows both
// failure paths landing where they should.
async Task LambdaBulkSqsPushWithFailuresAsync()
{
    Console.WriteLine();
    Console.WriteLine("-- Lambda -> SQS with a throttled batch and a rejected message --");

    const int messageCount = 50;
    var messages = Enumerable.Range(1, messageCount)
        .Select(i => new OutboundMessage(i.ToString(), $"{{\"orderId\":{i}}}"))
        .ToList();

    var flakyClient = new FlakySqsClient(
        new SimulatedSqsClient(),
        permanentFailureMessageIds: new[] { "23" },
        throttleOnceBatchesContaining: new[] { "31" });

    var publisher = new SqsBulkPublisher(flakyClient, queueUrl: "https://sqs.us-east-1.amazonaws.com/123456789012/orders-queue");
    var result = await publisher.PublishAllAsync(messages, maxDegreeOfParallelism: 10, maxRetries: 3);

    Console.WriteLine($"Sent {result.SucceededIds.Count}/{messageCount} messages, {result.Failures.Count} permanent failure(s)");
    foreach (var failure in result.Failures)
        Console.WriteLine($"  Message {failure.MessageId} failed: {failure.ErrorCode} - {failure.ErrorMessage} (SenderFault={failure.SenderFault})");
}

// ---------------------------------------------------------------------------
// Real usage (not run here - this sandbox has no AWS credentials/queue).
// Swapping SimulatedSqsClient for AwsSqsBatchClient is the only functional
// change; SqsBulkPublisher's retry/throttle/failure logic needs nothing else.
// This is what an actual Lambda handler (FunctionHandler(SQSEvent/APIGatewayProxyRequest, ILambdaContext))
// or any long-running service would construct at startup:
//
//   using var sqs = new Amazon.SQS.AmazonSQSClient(); // picks up region/credentials
//                                                      // from the Lambda execution role automatically
//   var publisher = new SqsBulkPublisher(
//       new AwsSqsBatchClient(sqs),
//       queueUrl: Environment.GetEnvironmentVariable("ORDERS_QUEUE_URL")!);
//
//   var result = await publisher.PublishAllAsync(messages, maxDegreeOfParallelism: 50, maxRetries: 3);
//
// Required alongside the code change:
//   1. IAM: the Lambda's execution role needs sqs:SendMessage and
//      sqs:SendMessageBatch on that queue's ARN (SendMessageBatch calls
//      SendMessage under the hood, both are checked).
//   2. Queue URL: pass it in (env var/config), don't hardcode it - it's
//      account+region+queue-name specific.
//   3. Credentials/region: AmazonSQSClient() with no arguments resolves both
//      from the Lambda execution environment automatically; only pass an
//      explicit RegionEndpoint/credentials for a non-Lambda host (local
//      dev, EC2, container) that doesn't already have them in the environment.
//   4. FIFO queue (only if the queue name ends in ".fifo"): every entry needs
//      MessageGroupId, and either MessageDeduplicationId or content-based
//      dedup enabled on the queue - see the commented-out lines in
//      AwsSqsBatchClient.SendMessageBatchAsync.
//   5. maxDegreeOfParallelism: lower it for a FIFO queue (capped at ~3000
//      msgs/sec across the queue) or if the Lambda's own memory/CPU
//      allocation can't sustain 200 concurrent HTTP calls - standard
//      queues have effectively no ceiling, FIFO does.
// ---------------------------------------------------------------------------
