using Amazon.SQS;
using Amazon.SQS.Model;
using ExamMaker.Application.Ai.Worker;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>A received SQS message; visibility changes and deletes use its receipt handle.</summary>
public sealed class SqsAiQueueMessage(IAmazonSQS sqs, string queueUrl, Message message) : IAiQueueMessage
{
    public string Body => message.Body ?? string.Empty;

    public Task ChangeVisibilityAsync(TimeSpan visibility, CancellationToken ct) =>
        sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = message.ReceiptHandle,
            VisibilityTimeout = (int)Math.Ceiling(visibility.TotalSeconds),
        }, ct);

    public Task DeleteAsync(CancellationToken ct) =>
        sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = message.ReceiptHandle }, ct);
}
