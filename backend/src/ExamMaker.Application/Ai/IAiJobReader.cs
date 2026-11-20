namespace ExamMaker.Application.Ai;

/// <summary>Read side of ai_generation_jobs. Performs no authorization; see <see cref="AiJobQueries"/>.</summary>
public interface IAiJobReader
{
    Task<AiJobView?> GetAsync(Guid jobId, CancellationToken ct);

    /// <summary>
    /// Lists jobs ordered by created_at descending. A null <paramref name="requestedBy"/> or
    /// <paramref name="status"/> means no filter on that column.
    /// </summary>
    Task<AiJobPage> ListAsync(Guid? requestedBy, string? status, int page, int pageSize, CancellationToken ct);
}
