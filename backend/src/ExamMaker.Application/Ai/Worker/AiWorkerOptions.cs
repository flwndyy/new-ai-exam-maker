namespace ExamMaker.Application.Ai.Worker;

/// <summary>Worker timing and concurrency, bound from configuration section "Worker" (docs/context.md 4.1.2, B6).</summary>
public sealed class AiWorkerOptions
{
    public const string SectionName = "Worker";

    public int MaxConcurrency { get; set; } = 2;

    /// <summary>DB lease and SQS visibility per heartbeat; must not exceed the queue visibility timeout (300 s).</summary>
    public int LeaseSeconds { get; set; } = 300;

    public int HeartbeatSeconds { get; set; } = 60;

    public int JobTimeoutMinutes { get; set; } = 10;

    public int RecoveryIntervalSeconds { get; set; } = 60;

    /// <summary>Re-send a Queued job's message after this long; must exceed the longest retry backoff (300 s).</summary>
    public int RequeueAfterMinutes { get; set; } = 10;

    public int StaleQueuedWarnMinutes { get; set; } = 15;
}
