using ExamMaker.Application.Authentication;

namespace ExamMaker.Application.Ai;

public abstract record AiJobListResult
{
    private AiJobListResult()
    {
    }

    public sealed record Ok(AiJobPage Page) : AiJobListResult;

    /// <param name="Errors">Field name (camelCase, as in the API contract) to messages.</param>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AiJobListResult;
}

/// <summary>
/// Job visibility rules from docs/api/openapi.yaml: teachers and heads see only the jobs they
/// requested; admins see all jobs. A job the user may not see is reported as not found.
/// </summary>
public sealed class AiJobQueries(IAiJobReader reader)
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> Statuses = ["Queued", "Running", "Succeeded", "Failed"];

    public async Task<AiJobView?> GetAsync(AuthenticatedUser user, Guid jobId, CancellationToken ct)
    {
        if (!user.IsActive)
        {
            return null;
        }

        var job = await reader.GetAsync(jobId, ct);
        return job is not null && (IsAdmin(user) || job.RequestedBy.Id == user.Id) ? job : null;
    }

    public async Task<AiJobListResult> ListAsync(
        AuthenticatedUser user, string? status, int page, int pageSize, CancellationToken ct)
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

        if (status is not null && !Statuses.Contains(status))
        {
            errors["status"] = [AiMessages.JobStatusInvalid];
        }

        if (errors.Count > 0)
        {
            return new AiJobListResult.Invalid(errors);
        }

        var requestedBy = IsAdmin(user) ? (Guid?)null : user.Id;
        return new AiJobListResult.Ok(await reader.ListAsync(requestedBy, status, page, pageSize, ct));
    }

    private static bool IsAdmin(AuthenticatedUser user) => user.IsActive && user.Role == Roles.Admin;
}
