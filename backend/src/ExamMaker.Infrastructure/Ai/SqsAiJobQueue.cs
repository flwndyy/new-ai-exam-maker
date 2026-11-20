using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using ExamMaker.Application.Ai;

namespace ExamMaker.Infrastructure.Ai;

public sealed class SqsAiJobQueue(IAmazonSQS sqs, string queueUrl) : IAiJobQueue
{
    // The body carries only the job id: the worker reloads everything else from the database.
    public static string CreateMessageBody(Guid jobId) =>
        JsonSerializer.Serialize(new { jobId }, JsonSerializerOptions.Web);

    public async Task EnqueueAsync(Guid jobId, CancellationToken ct)
    {
        await sqs.SendMessageAsync(
            new SendMessageRequest { QueueUrl = queueUrl, MessageBody = CreateMessageBody(jobId) }, ct);
    }
}
