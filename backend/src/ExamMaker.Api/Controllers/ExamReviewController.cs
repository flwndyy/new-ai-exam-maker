using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Exams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize(Roles = Roles.Head + "," + Roles.Admin)]
[Route("api/v1/review/exam-versions")]
public sealed class ExamReviewController(IExamManagement exams, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>List submitted versions awaiting another author's department reviewer or administrator.</summary>
    [HttpGet]
    public Task<ExamVersionPage> List([FromQuery] ExamReviewQuery query, CancellationToken cancellationToken) =>
        exams.ReviewQueueAsync(currentUser.User!.Id, query, cancellationToken);
}
