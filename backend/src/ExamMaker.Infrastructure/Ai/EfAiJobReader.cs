using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Domain.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Ai;

public sealed class EfAiJobReader(ExamMakerDbContext db) : IAiJobReader
{
    public async Task<AiJobView?> GetAsync(Guid jobId, CancellationToken ct)
    {
        var rows = await Project(db.AiGenerationJobs.Where(j => j.Id == jobId)).ToListAsync(ct);
        return (await ToViewsAsync(rows, ct)).SingleOrDefault();
    }

    public async Task<AiJobPage> ListAsync(
        Guid? requestedBy, string? status, int page, int pageSize, CancellationToken ct)
    {
        var query = db.AiGenerationJobs.AsQueryable();
        if (requestedBy is { } userId)
        {
            query = query.Where(j => j.RequestedBy == userId);
        }

        if (status is not null)
        {
            query = query.Where(j => j.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await Project(query
                .OrderByDescending(j => j.CreatedAt)
                .ThenByDescending(j => j.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync(ct);

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return new AiJobPage(await ToViewsAsync(rows, ct), page, pageSize, total, totalPages);
    }

    private static IQueryable<JobRow> Project(IQueryable<AiGenerationJob> query) =>
        query.AsNoTracking().Select(j => new JobRow(
            j.Id, j.RequestedBy, j.Requester!.FullName, j.Status, j.Params, j.Attempt, j.MaxAttempts,
            j.ErrorCode, j.ErrorMessage, j.ModelId, j.PromptVersion, j.InputTokens, j.OutputTokens,
            j.CreatedAt, j.StartedAt, j.FinishedAt));

    private async Task<List<AiJobView>> ToViewsAsync(List<JobRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var jobIds = rows.Select(r => r.Id).ToList();
        var questions = await db.Questions
            .AsNoTracking()
            .Where(q => q.AiJobId != null && jobIds.Contains(q.AiJobId.Value))
            .OrderBy(q => q.AiOrdinal)
            .Select(q => new { JobId = q.AiJobId!.Value, q.Id })
            .ToListAsync(ct);
        var questionsByJob = questions.ToLookup(q => q.JobId, q => q.Id);

        return rows.Select(r => new AiJobView(
                r.Id,
                new AiUserRef(r.RequestedBy, r.RequesterName),
                r.Status,
                JsonSerializer.Deserialize<AiGenerateRequest>(r.Params, JsonSerializerOptions.Web)!,
                r.Attempt,
                r.MaxAttempts,
                r.ErrorCode,
                r.ErrorMessage,
                r.ModelId,
                r.PromptVersion,
                r.InputTokens,
                r.OutputTokens,
                questionsByJob[r.Id].ToList(),
                ToUtc(r.CreatedAt),
                r.StartedAt is { } started ? ToUtc(started) : null,
                r.FinishedAt is { } finished ? ToUtc(finished) : null))
            .ToList();
    }

    // timestamptz values are UTC; pin the kind so the offset is always zero.
    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record JobRow(
        Guid Id,
        Guid RequestedBy,
        string RequesterName,
        string Status,
        string Params,
        int Attempt,
        int MaxAttempts,
        string? ErrorCode,
        string? ErrorMessage,
        string? ModelId,
        string? PromptVersion,
        long InputTokens,
        long OutputTokens,
        DateTime CreatedAt,
        DateTime? StartedAt,
        DateTime? FinishedAt);
}
