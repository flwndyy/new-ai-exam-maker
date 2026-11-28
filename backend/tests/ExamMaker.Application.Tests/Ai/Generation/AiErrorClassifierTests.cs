using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Tests.Ai.Generation;

public sealed class AiErrorClassifierTests
{
    [Theory]
    [InlineData(AiModelErrorKind.Throttled, AiErrorCodes.Throttled)]
    [InlineData(AiModelErrorKind.Timeout, AiErrorCodes.Timeout)]
    [InlineData(AiModelErrorKind.ServiceUnavailable, AiErrorCodes.ServiceUnavailable)]
    [InlineData(AiModelErrorKind.Network, AiErrorCodes.NetworkError)]
    // Transient provider failures leave the SQS message in flight so it is delivered again.
    public void Transient_model_errors_are_retryable(AiModelErrorKind kind, string code)
    {
        var failure = AiErrorClassifier.Classify(new AiModelException(kind, "provider detail"));

        Assert.True(failure.IsRetryable);
        Assert.Equal(code, failure.Code);
    }

    [Theory]
    [InlineData(AiModelErrorKind.InvalidRequest, AiErrorCodes.InvalidRequest)]
    [InlineData(AiModelErrorKind.AccessDenied, AiErrorCodes.AccessDenied)]
    [InlineData(AiModelErrorKind.ContentFiltered, AiErrorCodes.ContentRefused)]
    // Repeating a rejected request spends attempts (and possibly tokens) without a different outcome.
    public void Permanent_model_errors_are_not_retryable(AiModelErrorKind kind, string code)
    {
        var failure = AiErrorClassifier.Classify(new AiModelException(kind, "provider detail"));

        Assert.False(failure.IsRetryable);
        Assert.Equal(code, failure.Code);
    }

    [Theory]
    [InlineData(AiErrorCodes.InvalidJson)]
    [InlineData(AiErrorCodes.SchemaViolation)]
    [InlineData(AiErrorCodes.OutputTruncated)]
    [InlineData(AiErrorCodes.ContentRefused)]
    public void Unusable_output_after_repairs_is_not_retryable(string code)
    {
        var failure = AiErrorClassifier.Classify(new AiOutputException(code, "questions[0].payload"));

        Assert.False(failure.IsRetryable);
        Assert.Equal(code, failure.Code);
    }

    [Fact]
    public void Timeout_exception_is_retryable_timeout() =>
        Assert.Equal(new AiFailureKey(AiErrorCodes.Timeout, true), Key(AiErrorClassifier.Classify(new TimeoutException())));

    [Fact]
    // HttpClient reports its own timeout as TaskCanceledException.
    public void Cancellation_is_retryable_timeout() =>
        Assert.Equal(new AiFailureKey(AiErrorCodes.Timeout, true), Key(AiErrorClassifier.Classify(new TaskCanceledException())));

    [Fact]
    public void Http_and_io_errors_are_retryable_network_errors()
    {
        Assert.Equal(new AiFailureKey(AiErrorCodes.NetworkError, true), Key(AiErrorClassifier.Classify(new HttpRequestException())));
        Assert.Equal(new AiFailureKey(AiErrorCodes.NetworkError, true), Key(AiErrorClassifier.Classify(new IOException())));
    }

    [Fact]
    // Unknown failures (for example a transient database error) are bounded by maxReceiveCount and then reach the DLQ.
    public void Unknown_errors_are_retryable_unknown() =>
        Assert.Equal(new AiFailureKey(AiErrorCodes.Unknown, true), Key(AiErrorClassifier.Classify(new InvalidOperationException("boom"))));

    [Fact]
    // Raw provider text may echo prompt content; the stored message must not include it.
    public void Failure_message_does_not_echo_exception_text()
    {
        var failure = AiErrorClassifier.Classify(new AiModelException(AiModelErrorKind.InvalidRequest, "secret prompt echo"));

        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        Assert.DoesNotContain("secret prompt echo", failure.Message);
    }

    [Fact]
    public void Codes_fit_job_error_code_constraint()
    {
        foreach (var code in typeof(AiErrorCodes).GetFields().Select(x => (string)x.GetValue(null)!))
        {
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.Matches("^[A-Za-z]+$", code);
        }
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 300)]
    [InlineData(9, 300)]
    // SQS standard has no built-in backoff; the worker extends visibility before the next delivery.
    public void Retry_delay_grows_per_attempt_and_caps_at_visibility_timeout(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), AiRetryPolicy.VisibilityDelay(attempt));

    [Fact]
    public void Retry_delay_rejects_attempts_below_one() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AiRetryPolicy.VisibilityDelay(0));

    private static AiFailureKey Key(AiFailure failure) => new(failure.Code, failure.IsRetryable);

    private sealed record AiFailureKey(string Code, bool Retryable);
}
