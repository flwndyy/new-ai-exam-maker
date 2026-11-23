using Microsoft.Extensions.Time.Testing;

namespace ExamMaker.Infrastructure.Tests.Ai.Worker;

/// <summary>The fake queue must behave like the SQS settings of O11, or the end-to-end worker tests prove nothing.</summary>
public sealed class FakeSqsQueueTests
{
    private readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Receive_hides_message_for_300_seconds_and_counts_receives()
    {
        var queue = new FakeSqsQueue(time);
        var jobId = Guid.NewGuid();
        await queue.EnqueueAsync(jobId, CancellationToken.None);

        var first = Assert.Single(queue.Receive());
        Assert.Equal(jobId, first.JobId);
        Assert.Equal($"{{\"jobId\":\"{jobId}\"}}", first.Body);
        Assert.Equal(1, first.ReceiveCount);
        Assert.Empty(queue.Receive());

        time.Advance(TimeSpan.FromSeconds(299));
        Assert.Empty(queue.Receive());
        time.Advance(TimeSpan.FromSeconds(1));
        var again = Assert.Single(queue.Receive());
        Assert.Same(first, again);
        Assert.Equal(2, again.ReceiveCount);
    }

    [Fact]
    public async Task Fourth_receive_moves_message_to_dlq()
    {
        var queue = new FakeSqsQueue(time);
        await queue.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            Assert.Single(queue.Receive());
            time.Advance(TimeSpan.FromSeconds(300));
        }

        Assert.Empty(queue.Receive());
        var dead = Assert.Single(queue.Dlq);
        Assert.Equal(3, dead.ReceiveCount);
        Assert.Empty(queue.InQueue);
    }

    [Fact]
    public async Task Change_visibility_and_delete_apply_to_the_queue()
    {
        var queue = new FakeSqsQueue(time);
        var jobId = Guid.NewGuid();
        await queue.EnqueueAsync(jobId, CancellationToken.None);
        var message = Assert.Single(queue.Receive());

        await message.ChangeVisibilityAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal([TimeSpan.FromSeconds(30)], message.VisibilityChanges);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Same(message, Assert.Single(queue.Receive()));
        Assert.Same(message, Assert.Single(queue.For(jobId)));

        await message.DeleteAsync(CancellationToken.None);
        Assert.True(message.Deleted);
        Assert.Empty(queue.InQueue);
        Assert.Empty(queue.For(jobId));
    }

    [Fact]
    public async Task FailNextDeletes_throws_and_keeps_the_message()
    {
        var queue = new FakeSqsQueue(time) { FailNextDeletes = 1 };
        await queue.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
        var message = Assert.Single(queue.Receive());

        await Assert.ThrowsAsync<InvalidOperationException>(() => message.DeleteAsync(CancellationToken.None));
        Assert.False(message.Deleted);
        Assert.Single(queue.InQueue);

        await message.DeleteAsync(CancellationToken.None);
        Assert.True(message.Deleted);
        Assert.Empty(queue.InQueue);
    }
}
