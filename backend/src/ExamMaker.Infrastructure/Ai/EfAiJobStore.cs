using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Domain.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Ai;

public sealed class EfAiJobStore(ExamMakerDbContext db) : IAiJobStore
{
    public async Task<Guid?> TryCreateQueuedJobAsync(
        Guid userId,
        AiGenerateRequest request,
        DateOnly usageDate,
        int jobsLimit,
        int maxAttempts,
        CancellationToken ct)
    {
        // The upsert below would insert a first row even for a limit of 0, so reject that here.
        if (jobsLimit < 1)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Shared with concurrent submissions, exclusive against A5 mutations until the job is committed.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(48683402)", ct);
        // The subject must also still belong to the requester's current department; otherwise the worker
        // would fail the job with RequesterUnavailable and the reserved slot would never be refunded.
        if (!await db.Topics.AsNoTracking().AnyAsync(t => t.Id == request.TopicId
            && t.SubjectId == request.SubjectId && t.Grade == request.Grade
            && db.Users.Any(u => u.Id == userId && u.DepartmentId == t.Subject!.DepartmentId), ct))
            throw new AiCatalogChangedException();

        // Atomic reservation: a concurrent request waits on the row lock, then re-checks the WHERE
        // against the updated row, so the counter can never pass the limit. Zero rows affected
        // means the limit was already reached.
        var reserved = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_usage_daily (user_id, date, jobs, tokens)
            VALUES ({userId}, {usageDate}, 1, 0)
            ON CONFLICT (user_id, date) DO UPDATE SET jobs = ai_usage_daily.jobs + 1
            WHERE ai_usage_daily.jobs < {jobsLimit}
            """, ct);

        if (reserved == 0)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var now = DateTime.UtcNow;
        var job = new AiGenerationJob
        {
            Id = Guid.NewGuid(),
            RequestedBy = userId,
            Params = JsonSerializer.Serialize(request, JsonSerializerOptions.Web),
            Status = "Queued",
            MaxAttempts = maxAttempts,
            CreatedAt = now,
            LastEnqueuedAt = now,
        };

        db.AiGenerationJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return job.Id;
    }
}
