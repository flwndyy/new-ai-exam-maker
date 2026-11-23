using System.Globalization;

namespace ExamMaker.Application.Ai;

/// <summary>Mirrors the AiUsageRow schema in docs/api/openapi.yaml.</summary>
public sealed record AiUsageRow(AiUserRef User, DateOnly Date, int Jobs, long Tokens, decimal EstimatedCostUsd);

/// <summary>Totals over every row matching the filter, not only the current page.</summary>
public sealed record AiUsageTotals(long Jobs, long Tokens, decimal EstimatedCostUsd);

/// <summary>Mirrors the AiUsagePage schema (PageMeta + items + totals).</summary>
public sealed record AiUsagePage(
    IReadOnlyList<AiUsageRow> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    AiUsageTotals Totals);

/// <summary>Validated report filter; From and To are inclusive UTC dates.</summary>
public sealed record AiUsageFilter(Guid? UserId, DateOnly? From, DateOnly? To, int Page, int PageSize);

public interface IAiUsageReportReader
{
    Task<AiUsagePage> ReadAsync(AiUsageFilter filter, CancellationToken ct);
}

public abstract record AiUsageReportResult
{
    private AiUsageReportResult()
    {
    }

    public sealed record Ok(AiUsagePage Page) : AiUsageReportResult;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AiUsageReportResult;
}

/// <summary>Validates GET /admin/ai-usage query parameters (admin-only; enforced by the controller policy).</summary>
public sealed class AiUsageReportQueries(IAiUsageReportReader reader)
{
    public const int MaxPageSize = 100;

    public async Task<AiUsageReportResult> ListAsync(
        int page, int pageSize, Guid? userId, string? from, string? to, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = [AiMessages.PageInvalid];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [AiMessages.PageSizeInvalid(MaxPageSize)];
        }

        var fromDate = ParseDate(from, "from", errors);
        var toDate = ParseDate(to, "to", errors);
        if (fromDate is not null && toDate is not null && fromDate > toDate)
        {
            errors["to"] = [AiMessages.DateRangeInvalid];
        }

        if (errors.Count > 0)
        {
            return new AiUsageReportResult.Invalid(errors);
        }

        return new AiUsageReportResult.Ok(
            await reader.ReadAsync(new AiUsageFilter(userId, fromDate, toDate, page, pageSize), ct));
    }

    private static DateOnly? ParseDate(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors[field] = [AiMessages.DateInvalid];
        return null;
    }
}
