namespace ExamMaker.Application.Ai.Generation;

/// <summary>Stable values stored in ai_generation_jobs.error_code.</summary>
public static class AiErrorCodes
{
    public const string Throttled = "Throttled";
    public const string Timeout = "Timeout";
    public const string ServiceUnavailable = "ServiceUnavailable";
    public const string NetworkError = "NetworkError";
    public const string InvalidRequest = "InvalidRequest";
    public const string AccessDenied = "AccessDenied";
    public const string ContentRefused = "ContentRefused";
    public const string InvalidJson = "InvalidJson";
    public const string SchemaViolation = "SchemaViolation";
    public const string OutputTruncated = "OutputTruncated";
    public const string Unknown = "Unknown";

    // Worker-level outcomes (B6); never produced by the classifier.
    public const string QuotaExceeded = "QuotaExceeded";
    public const string RequesterUnavailable = "RequesterUnavailable";
    public const string AttemptsExhausted = "AttemptsExhausted";
}

/// <summary>Provider-neutral failure categories; the Bedrock adapter maps SDK exceptions onto these.</summary>
public enum AiModelErrorKind
{
    Throttled,
    Timeout,
    ServiceUnavailable,
    Network,
    InvalidRequest,
    AccessDenied,
    ContentFiltered,
}

/// <summary>Thrown by <see cref="IAiModelClient"/> implementations for provider failures.</summary>
public sealed class AiModelException(AiModelErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public AiModelErrorKind Kind { get; } = kind;
}

/// <summary>Thrown when model output is unusable after the allowed repair attempts.</summary>
public sealed class AiOutputException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Classified failure: the code and Vietnamese message are stored on the job, never the raw exception text.</summary>
public sealed record AiFailure(string Code, bool IsRetryable, string Message);

/// <summary>Split failures into retryable (leave the SQS message for redelivery) and non-retryable (mark the job Failed).</summary>
public static class AiErrorClassifier
{
    public static AiFailure Classify(Exception exception) => exception switch
    {
        AiModelException model => model.Kind switch
        {
            AiModelErrorKind.Throttled => Retryable(AiErrorCodes.Throttled),
            AiModelErrorKind.Timeout => Retryable(AiErrorCodes.Timeout),
            AiModelErrorKind.ServiceUnavailable => Retryable(AiErrorCodes.ServiceUnavailable),
            AiModelErrorKind.Network => Retryable(AiErrorCodes.NetworkError),
            AiModelErrorKind.InvalidRequest => Permanent(AiErrorCodes.InvalidRequest),
            AiModelErrorKind.AccessDenied => Permanent(AiErrorCodes.AccessDenied),
            AiModelErrorKind.ContentFiltered => Permanent(AiErrorCodes.ContentRefused),
            _ => Retryable(AiErrorCodes.Unknown),
        },
        AiOutputException output => Permanent(output.Code),
        TimeoutException or OperationCanceledException => Retryable(AiErrorCodes.Timeout),
        HttpRequestException or IOException => Retryable(AiErrorCodes.NetworkError),
        // Bounded by maxReceiveCount, after which the message reaches the DLQ and raises an alarm.
        _ => Retryable(AiErrorCodes.Unknown),
    };

    private static AiFailure Retryable(string code) => new(code, true, AiMessages.GenerationFailed(code));

    private static AiFailure Permanent(string code) => new(code, false, AiMessages.GenerationFailed(code));
}

/// <summary>SQS standard queues have no backoff, so the worker sets the next visibility delay itself.</summary>
public static class AiRetryPolicy
{
    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300)];

    /// <summary>Delay after the given 1-based attempt; capped at the 300-second queue visibility timeout.</summary>
    public static TimeSpan VisibilityDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return Delays[Math.Min(attempt, Delays.Length) - 1];
    }
}
