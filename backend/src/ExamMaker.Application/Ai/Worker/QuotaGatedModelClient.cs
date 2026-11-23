using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Ai.Worker;

/// <summary>Thrown before a model call when the requester or the whole system has used up today's token quota.</summary>
public sealed class AiQuotaExceededException(string message) : Exception(message);

/// <summary>
/// Checks the daily token caps (UTC day at call time) before every model call, so no tokens are spent once a cap is reached.
/// One instance per job, bound to the requesting user.
/// </summary>
public sealed class QuotaGatedModelClient(
    IAiModelClient inner,
    IAiUsageReader usage,
    AiQuotaOptions options,
    TimeProvider time,
    Guid userId) : IAiModelClient
{
    public async Task<AiModelResponse> InvokeAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        var date = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        var user = await usage.GetUserUsageAsync(userId, date, cancellationToken);
        if (user.TokensUsed >= options.TokensPerUserPerDay)
            throw new AiQuotaExceededException("User daily token quota reached.");

        if (await usage.GetSystemTokensAsync(date, cancellationToken) >= options.SystemTokensPerDay)
            throw new AiQuotaExceededException("System daily token quota reached.");

        return await inner.InvokeAsync(request, cancellationToken);
    }
}
