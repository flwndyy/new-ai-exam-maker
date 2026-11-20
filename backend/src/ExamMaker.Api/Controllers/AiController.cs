using System.Globalization;
using ExamMaker.Api.Ai;
using ExamMaker.Api.Authorization;
using ExamMaker.Application.Ai;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/ai")]
public sealed class AiController(
    ICurrentUser currentUser,
    AiGenerationService generation,
    AiJobQueries jobs) : ControllerBase
{
    /// <summary>Checks rate limit and quota, then creates a Queued job and enqueues it (202).</summary>
    [HttpPost("generate")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    [EnableRateLimiting(AiRateLimiting.PolicyName)]
    [ProducesResponseType<AiJobView>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Generate(AiGenerateRequest request, CancellationToken ct)
    {
        if (currentUser.User is not { } user) return Challenge();

        switch (await generation.SubmitAsync(user.Id, request, ct))
        {
            case AiSubmitResult.ValidationFailed invalid:
                return ValidationProblemFor(invalid.Errors);

            case AiSubmitResult.QuotaExceeded exceeded:
                return QuotaExceededProblem(exceeded);

            case AiSubmitResult.Accepted accepted:
                var job = await jobs.GetAsync(user, accepted.JobId, ct)
                    ?? throw new InvalidOperationException($"AI job {accepted.JobId} was created but cannot be read.");
                return Accepted($"/api/v1/ai/jobs/{job.Id}", job);

            default:
                throw new InvalidOperationException("Unknown AI submit result.");
        }
    }

    /// <summary>Teachers and heads list their own jobs; admins list all. Newest first.</summary>
    [HttpGet("jobs")]
    [ProducesResponseType<AiJobPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListJobs(
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (currentUser.User is not { } user) return Challenge();

        return await jobs.ListAsync(user, status, page, pageSize, ct) switch
        {
            AiJobListResult.Ok ok => Ok(ok.Page),
            AiJobListResult.Invalid invalid => ValidationProblemFor(invalid.Errors),
            _ => throw new InvalidOperationException("Unknown AI job list result."),
        };
    }

    /// <summary>Polling endpoint. Jobs the caller may not see are reported as 404.</summary>
    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType<AiJobView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJob(Guid id, CancellationToken ct)
    {
        if (currentUser.User is not { } user) return Challenge();

        var job = await jobs.GetAsync(user, id, ct);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Today's (UTC) quota and usage for the current user.</summary>
    [HttpGet("quota")]
    [ProducesResponseType<AiQuotaSnapshot>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQuota(CancellationToken ct)
    {
        if (currentUser.User is not { } user) return Challenge();

        return Ok(await generation.GetQuotaAsync(user.Id, ct));
    }

    // Goes through ProblemDetailsFactory so the shared code/traceId customization is applied.
    private IActionResult ValidationProblemFor(IReadOnlyDictionary<string, string[]> errors)
    {
        foreach (var (field, messages) in errors)
        {
            foreach (var message in messages)
            {
                ModelState.AddModelError(field, message);
            }
        }

        return ValidationProblem(ModelState);
    }

    private IActionResult QuotaExceededProblem(AiSubmitResult.QuotaExceeded exceeded)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(exceeded.RetryAfter.TotalSeconds));
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Đã hết quota AI hôm nay",
            type: "https://exammaker.example/problems/quota-exceeded");
        // The shared customization maps 429 to RateLimited; quota exhaustion has its own documented code.
        problem.Extensions["code"] = "QuotaExceeded";

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
            ContentTypes = { "application/problem+json" },
        };
    }
}
