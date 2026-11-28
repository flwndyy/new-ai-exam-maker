using ExamMaker.Api.Authorization;
using ExamMaker.Application.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize(Policy = AccessPolicies.Admin)]
[Route("api/v1/admin/ai-usage")]
public sealed class AdminAiUsageController(AiUsageReportQueries queries) : ControllerBase
{
    /// <summary>Daily AI usage per user with an estimated (not billed) cost; newest day first.</summary>
    [HttpGet]
    [ProducesResponseType<AiUsagePage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? userId, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        switch (await queries.ListAsync(page, pageSize, userId, from, to, ct))
        {
            case AiUsageReportResult.Ok ok:
                return Ok(ok.Page);

            case AiUsageReportResult.Invalid invalid:
                // Goes through ProblemDetailsFactory so the shared code/traceId customization is applied.
                foreach (var (field, messages) in invalid.Errors)
                {
                    foreach (var message in messages)
                    {
                        ModelState.AddModelError(field, message);
                    }
                }

                return ValidationProblem(ModelState);

            default:
                throw new InvalidOperationException("Unknown AI usage report result.");
        }
    }
}
