using ExamMaker.Application.Ai.Worker;
using Microsoft.Extensions.Logging;

namespace ExamMaker.Application.Tests.Ai.Worker;

public sealed class AiJobMessageHandlerTests
{
    private static readonly Guid JobId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly string Body = $$"""{"jobId":"{{JobId}}"}""";

    private readonly RecordingQueue _queue = new();

    private (AiJobMessageHandler Handler, List<Guid> Processed, RecordingLogger<AiJobMessageHandler> Logger) Create(
        Func<AiJobOutcome> outcome)
    {
        var processed = new List<Guid>();
        var logger = new RecordingLogger<AiJobMessageHandler>();
        var handler = new AiJobMessageHandler((jobId, _, _) =>
        {
            processed.Add(jobId);
            return Task.FromResult(outcome());
        }, _queue, logger);
        return (handler, processed, logger);
    }

    [Fact]
    public void TryParseJobId_reads_the_job_id()
    {
        Assert.True(AiJobMessageHandler.TryParseJobId(Body, out var jobId));
        Assert.Equal(JobId, jobId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"jobId":"abc"}""")]
    [InlineData("""{"jobId":42}""")]
    [InlineData("""{"jobId":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task Invalid_body_is_deleted_without_processing(string body)
    {
        var (handler, processed, _) = Create(() => new AiJobOutcome.Abandon());
        var message = new FakeAiQueueMessage(new CallLog(), body);

        await handler.HandleAsync(message, CancellationToken.None);

        Assert.Single(message.Deletes);
        Assert.Empty(processed);
    }

    [Fact]
    public async Task Delete_outcome_deletes_message()
    {
        var (handler, processed, _) = Create(() => new AiJobOutcome.Delete());
        var message = new FakeAiQueueMessage(new CallLog(), Body);

        await handler.HandleAsync(message, CancellationToken.None);

        Assert.Equal([JobId], processed);
        Assert.Single(message.Deletes);
        Assert.Empty(message.VisibilityChanges);
    }

    [Fact]
    public async Task RetryAfter_outcome_changes_visibility()
    {
        var (handler, _, _) = Create(() => new AiJobOutcome.RetryAfter(TimeSpan.FromSeconds(120)));
        var message = new FakeAiQueueMessage(new CallLog(), Body);

        await handler.HandleAsync(message, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(120), Assert.Single(message.VisibilityChanges).Visibility);
        Assert.Empty(message.Deletes);
    }

    [Fact]
    public async Task Abandon_outcome_touches_nothing()
    {
        var (handler, _, _) = Create(() => new AiJobOutcome.Abandon());
        var message = new FakeAiQueueMessage(new CallLog(), Body);

        await handler.HandleAsync(message, CancellationToken.None);

        Assert.Empty(message.Deletes);
        Assert.Empty(message.VisibilityChanges);
    }

    [Fact]
    public async Task Outcome_is_applied_with_an_uncancelled_token_after_shutdown()
    {
        var (handler, _, _) = Create(() => new AiJobOutcome.RetryAfter(TimeSpan.Zero));
        var message = new FakeAiQueueMessage(new CallLog(), Body);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await handler.HandleAsync(message, stopping.Token);

        Assert.Equal((TimeSpan.Zero, false), Assert.Single(message.VisibilityChanges));
    }

    [Fact]
    public async Task Delete_failure_is_logged_not_thrown()
    {
        var (handler, _, logger) = Create(() => new AiJobOutcome.Delete());
        var message = new FakeAiQueueMessage(new CallLog(), Body) { ThrowOnDelete = true };

        await handler.HandleAsync(message, CancellationToken.None);

        Assert.Contains(LogLevel.Error, logger.Levels);
    }

    [Fact]
    public async Task Unexpected_processor_exception_is_logged_and_message_left_alone()
    {
        var (handler, _, logger) = Create(() => throw new InvalidOperationException("db down"));
        var message = new FakeAiQueueMessage(new CallLog(), Body);

        await handler.HandleAsync(message, CancellationToken.None);

        Assert.Contains(LogLevel.Error, logger.Levels);
        Assert.Empty(message.Deletes);
        Assert.Empty(message.VisibilityChanges);
    }

    [Fact]
    public async Task Requeue_sends_a_fresh_message_then_deletes_the_old_one_after_shutdown()
    {
        // A fresh message starts with a receive count of 0, so a deploy does not push the job towards the DLQ.
        var (handler, _, _) = Create(() => new AiJobOutcome.Requeue());
        var message = new FakeAiQueueMessage(new CallLog(), Body);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await handler.HandleAsync(message, stopping.Token);

        Assert.Equal([(JobId, false)], _queue.Sent);
        Assert.Equal([false], message.Deletes);
        Assert.Empty(message.VisibilityChanges);
    }

    [Fact]
    public async Task Requeue_falls_back_to_immediate_visibility_when_the_send_fails()
    {
        var (handler, _, _) = Create(() => new AiJobOutcome.Requeue());
        _queue.Fail = true;
        var message = new FakeAiQueueMessage(new CallLog(), Body);

        await handler.HandleAsync(message, CancellationToken.None);

        Assert.Empty(message.Deletes);
        Assert.Equal((TimeSpan.Zero, false), Assert.Single(message.VisibilityChanges));
    }

    private sealed class RecordingQueue : ExamMaker.Application.Ai.IAiJobQueue
    {
        public bool Fail { get; set; }
        public List<(Guid JobId, bool TokenCancelled)> Sent { get; } = [];

        public Task EnqueueAsync(Guid jobId, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("sqs down");
            Sent.Add((jobId, ct.IsCancellationRequested));
            return Task.CompletedTask;
        }
    }
}
