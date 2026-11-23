using ExamMaker.Application.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>
/// Admin AI usage report: rows and token/job totals from ai_usage_daily; estimated cost from
/// ai_generation_jobs per model, attributed to the UTC day of COALESCE(finished_at, started_at, created_at).
/// </summary>
public sealed class EfAiUsageReportReader(
    ExamMakerDbContext db, AiCostEstimator cost, ILogger<EfAiUsageReportReader> logger) : IAiUsageReportReader
{
    public async Task<AiUsagePage> ReadAsync(AiUsageFilter filter, CancellationToken ct)
    {
        var usage = db.AiUsageDaily.AsNoTracking();
        if (filter.UserId is { } userId)
        {
            usage = usage.Where(u => u.UserId == userId);
        }

        if (filter.From is { } from)
        {
            usage = usage.Where(u => u.Date >= from);
        }

        if (filter.To is { } to)
        {
            usage = usage.Where(u => u.Date <= to);
        }

        var totalItems = await usage.CountAsync(ct);
        var totalJobs = await usage.SumAsync(u => (long)u.Jobs, ct);
        var totalTokens = await usage.SumAsync(u => u.Tokens, ct);

        var rows = await usage
            .OrderByDescending(u => u.Date)
            .ThenBy(u => u.UserId)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(u => new { u.UserId, u.User!.FullName, u.Date, u.Jobs, u.Tokens })
            .ToListAsync(ct);

        var costs = await ReadJobCostsAsync(filter, ct);
        var costByUserDay = new Dictionary<(Guid, DateOnly), decimal>();
        var totalCost = 0m;
        var unpriced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var group in costs)
        {
            var amount = cost.Estimate(group.ModelId, group.InputTokens, group.OutputTokens, out var priced);
            if (!priced)
            {
                unpriced.Add(group.ModelId ?? "(null)");
            }

            var key = (group.UserId, group.Day);
            costByUserDay[key] = costByUserDay.GetValueOrDefault(key) + amount;
            totalCost += amount;
        }

        if (unpriced.Count > 0)
        {
            logger.LogWarning("AI usage report: no price configured for models {ModelIds}; their cost counts as 0.", unpriced);
        }

        var items = rows
            .Select(r => new AiUsageRow(
                new AiUserRef(r.UserId, r.FullName),
                r.Date,
                r.Jobs,
                r.Tokens,
                Round(costByUserDay.GetValueOrDefault((r.UserId, r.Date)))))
            .ToList();

        var totalPages = (int)Math.Ceiling(totalItems / (double)filter.PageSize);
        return new AiUsagePage(
            items, filter.Page, filter.PageSize, totalItems, totalPages,
            new AiUsageTotals(totalJobs, totalTokens, Round(totalCost)));
    }

    // Raw SQL so the UTC day is computed explicitly instead of relying on how a DateTime.Date
    // projection over timestamptz is translated.
    private Task<List<JobCostGroup>> ReadJobCostsAsync(AiUsageFilter filter, CancellationToken ct)
    {
        var from = filter.From ?? DateOnly.MinValue;
        var to = filter.To ?? DateOnly.MaxValue;
        var query = filter.UserId is { } userId
            ? db.Database.SqlQuery<JobCostGroup>($"""
                SELECT requested_by AS "UserId",
                       (COALESCE(finished_at, started_at, created_at) AT TIME ZONE 'UTC')::date AS "Day",
                       model_id AS "ModelId",
                       SUM(input_tokens)::bigint AS "InputTokens",
                       SUM(output_tokens)::bigint AS "OutputTokens"
                FROM ai_generation_jobs
                WHERE requested_by = {userId}
                  AND (COALESCE(finished_at, started_at, created_at) AT TIME ZONE 'UTC')::date BETWEEN {from} AND {to}
                  AND (input_tokens > 0 OR output_tokens > 0)
                GROUP BY 1, 2, 3
                """)
            : db.Database.SqlQuery<JobCostGroup>($"""
                SELECT requested_by AS "UserId",
                       (COALESCE(finished_at, started_at, created_at) AT TIME ZONE 'UTC')::date AS "Day",
                       model_id AS "ModelId",
                       SUM(input_tokens)::bigint AS "InputTokens",
                       SUM(output_tokens)::bigint AS "OutputTokens"
                FROM ai_generation_jobs
                WHERE (COALESCE(finished_at, started_at, created_at) AT TIME ZONE 'UTC')::date BETWEEN {from} AND {to}
                  AND (input_tokens > 0 OR output_tokens > 0)
                GROUP BY 1, 2, 3
                """);
        return query.ToListAsync(ct);
    }

    private static decimal Round(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed class JobCostGroup
    {
        public Guid UserId { get; set; }

        public DateOnly Day { get; set; }

        public string? ModelId { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }
    }
}
