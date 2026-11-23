using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Worker;
using Microsoft.Extensions.Logging;

namespace ExamMaker.Application.Tests.Ai.Worker;

public sealed class AiJobRecoveryTests
{
    [Fact]
    public async Task Resends_every_stale_job_and_continues_after_a_send_failure()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var store = new FakeRecoveryStore { Stale = ids };
        var queue = new FakeQueue { FailFor = ids[1] };

        await new AiJobRecovery(store, queue, new AiWorkerOptions(), new RecordingLogger<AiJobRecovery>())
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal([ids[0], ids[2]], queue.Sent);
    }

    [Fact]
    public async Task Logs_warning_when_old_queued_jobs_exist()
    {
        var logger = new RecordingLogger<AiJobRecovery>();

        await new AiJobRecovery(new FakeRecoveryStore { OldQueued = 2 }, new FakeQueue(), new AiWorkerOptions(), logger)
            .RunOnceAsync(CancellationToken.None);

        Assert.Contains(LogLevel.Warning, logger.Levels);
    }

    [Fact]
    public async Task No_warning_when_no_old_queued_jobs()
    {
        var logger = new RecordingLogger<AiJobRecovery>();

        await new AiJobRecovery(new FakeRecoveryStore(), new FakeQueue(), new AiWorkerOptions(), logger)
            .RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(LogLevel.Warning, logger.Levels);
    }

    [Fact]
    public async Task Passes_configured_ages_and_exhausted_message_to_store()
    {
        var store = new FakeRecoveryStore();

        await new AiJobRecovery(store, new FakeQueue(), new AiWorkerOptions(), new RecordingLogger<AiJobRecovery>())
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), store.ResendAge);
        Assert.Equal(TimeSpan.FromMinutes(15), store.WarnAge);
        Assert.Equal("Đã thử lại nhiều lần nhưng không xử lý được yêu cầu.", store.ExhaustedMessage);
    }

    [Fact]
    public async Task Store_failure_in_one_step_does_not_skip_the_others()
    {
        var id = Guid.NewGuid();
        var store = new FakeRecoveryStore { Stale = [id], ThrowOnExpired = true, OldQueued = 1 };
        var queue = new FakeQueue();
        var logger = new RecordingLogger<AiJobRecovery>();

        await new AiJobRecovery(store, queue, new AiWorkerOptions(), logger).RunOnceAsync(CancellationToken.None);

        Assert.Equal([id], queue.Sent);
        Assert.Contains(LogLevel.Error, logger.Levels);
        Assert.Contains(LogLevel.Warning, logger.Levels);
    }

    private sealed class FakeRecoveryStore : IAiJobRecoveryStore
    {
        public IReadOnlyList<Guid> Stale { get; init; } = [];
        public int OldQueued { get; init; }
        public bool ThrowOnExpired { get; init; }
        public TimeSpan? ResendAge { get; private set; }
        public TimeSpan? WarnAge { get; private set; }
        public string? ExhaustedMessage { get; private set; }

        public Task<AiExpiredLeaseResult> RecoverExpiredLeasesAsync(string exhaustedMessage, CancellationToken ct)
        {
            ExhaustedMessage = exhaustedMessage;
            if (ThrowOnExpired) throw new InvalidOperationException("db down");
            return Task.FromResult(new AiExpiredLeaseResult(0, 0));
        }

        public Task<IReadOnlyList<Guid>> MarkStaleQueuedForResendAsync(TimeSpan olderThan, CancellationToken ct)
        {
            ResendAge = olderThan;
            return Task.FromResult(Stale);
        }

        public Task<int> CountQueuedOlderThanAsync(TimeSpan age, CancellationToken ct)
        {
            WarnAge = age;
            return Task.FromResult(OldQueued);
        }
    }

    private sealed class FakeQueue : IAiJobQueue
    {
        public Guid? FailFor { get; init; }
        public List<Guid> Sent { get; } = [];

        public Task EnqueueAsync(Guid jobId, CancellationToken ct)
        {
            if (jobId == FailFor) throw new InvalidOperationException("sqs down");
            Sent.Add(jobId);
            return Task.CompletedTask;
        }
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> Levels { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
}
