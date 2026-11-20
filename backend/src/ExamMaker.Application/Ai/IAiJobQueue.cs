namespace ExamMaker.Application.Ai;

public interface IAiJobQueue
{
    /// <summary>Sends the job id to the AI job queue (the SQS message body is {jobId}).</summary>
    Task EnqueueAsync(Guid jobId, CancellationToken ct);
}
