namespace ExamMaker.Application.Ai;

public abstract record AiSubmitResult
{
    private AiSubmitResult()
    {
    }

    public sealed record Accepted(Guid JobId) : AiSubmitResult;

    /// <param name="Errors">Field name (camelCase, as in the API contract) to messages.</param>
    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : AiSubmitResult;

    public sealed record QuotaExceeded(AiQuotaReason Reason, TimeSpan RetryAfter) : AiSubmitResult;
}
