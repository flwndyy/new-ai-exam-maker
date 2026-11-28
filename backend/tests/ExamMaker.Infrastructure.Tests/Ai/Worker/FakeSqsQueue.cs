using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Worker;

namespace ExamMaker.Infrastructure.Tests.Ai.Worker;

/// <summary>
/// In-memory stand-in for the O11 standard queue: 300 s visibility timeout, receive counting and redrive to a DLQ once a
/// message would be received more than <c>maxReceiveCount</c> times. Time comes from the test's <see cref="TimeProvider"/>.
/// </summary>
internal sealed class FakeSqsQueue(TimeProvider time, int maxReceiveCount = 3) : IAiJobQueue
{
    public static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(300);

    private readonly object gate = new();
    private readonly List<FakeSqsMessage> queue = [];
    private readonly List<FakeSqsMessage> dlq = [];

    /// <summary>How many following <see cref="FakeSqsMessage.DeleteAsync"/> calls throw (a lost delete after commit).</summary>
    public int FailNextDeletes { get; set; }

    public DateTimeOffset Now => time.GetUtcNow();

    public IReadOnlyList<FakeSqsMessage> InQueue
    {
        get { lock (gate) return queue.ToList(); }
    }

    public IReadOnlyList<FakeSqsMessage> Dlq
    {
        get { lock (gate) return dlq.ToList(); }
    }

    public IEnumerable<FakeSqsMessage> For(Guid jobId) => InQueue.Where(m => m.JobId == jobId);

    public Task EnqueueAsync(Guid jobId, CancellationToken ct)
    {
        lock (gate) queue.Add(new FakeSqsMessage(this, jobId, Now));
        return Task.CompletedTask;
    }

    /// <summary>Returns every visible message and hides it for the visibility timeout, redriving over-received ones.</summary>
    public IReadOnlyList<FakeSqsMessage> Receive()
    {
        lock (gate)
        {
            var received = new List<FakeSqsMessage>();
            foreach (var message in queue.Where(m => m.VisibleAt <= Now).ToList())
            {
                if (message.ReceiveCount >= maxReceiveCount)
                {
                    queue.Remove(message);
                    dlq.Add(message);
                    continue;
                }

                message.ReceiveCount++;
                message.VisibleAt = Now + VisibilityTimeout;
                received.Add(message);
            }

            return received;
        }
    }

    internal void ChangeVisibility(FakeSqsMessage message, TimeSpan visibility)
    {
        lock (gate)
        {
            message.VisibilityChanges.Add(visibility);
            message.VisibleAt = Now + visibility;
        }
    }

    internal void Delete(FakeSqsMessage message)
    {
        lock (gate)
        {
            if (FailNextDeletes > 0)
            {
                FailNextDeletes--;
                throw new InvalidOperationException("Simulated SQS delete failure.");
            }

            queue.Remove(message);
            message.Deleted = true;
        }
    }
}

internal sealed class FakeSqsMessage(FakeSqsQueue owner, Guid jobId, DateTimeOffset visibleAt) : IAiQueueMessage
{
    public Guid JobId { get; } = jobId;

    public string Body { get; } = $"{{\"jobId\":\"{jobId}\"}}";

    public int ReceiveCount { get; internal set; }

    public DateTimeOffset VisibleAt { get; internal set; } = visibleAt;

    public bool Deleted { get; internal set; }

    public List<TimeSpan> VisibilityChanges { get; } = [];

    public Task ChangeVisibilityAsync(TimeSpan visibility, CancellationToken ct)
    {
        owner.ChangeVisibility(this, visibility);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        owner.Delete(this);
        return Task.CompletedTask;
    }
}
