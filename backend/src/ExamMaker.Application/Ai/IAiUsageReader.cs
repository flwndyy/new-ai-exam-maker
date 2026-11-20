namespace ExamMaker.Application.Ai;

/// <summary>Reads daily AI usage from ai_usage_daily. Dates are UTC days.</summary>
public interface IAiUsageReader
{
    Task<AiUsage> GetUserUsageAsync(Guid userId, DateOnly date, CancellationToken ct);

    Task<long> GetSystemTokensAsync(DateOnly date, CancellationToken ct);
}
