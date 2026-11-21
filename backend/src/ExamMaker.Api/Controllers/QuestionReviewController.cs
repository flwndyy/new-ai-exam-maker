using ExamMaker.Application.Authentication;
using ExamMaker.Application.Questions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize(Roles = Roles.Head + "," + Roles.Admin)]
[Route("api/v1/review/questions")]
public sealed class QuestionReviewController(IQuestionManagement questions, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Return other authors' submitted questions in review order within the verified reviewer's scope.</summary>
    [HttpGet]
    public Task<QuestionPage> List([FromQuery] QuestionReviewQuery query, CancellationToken cancellationToken) =>
        questions.ReviewQueueAsync(currentUser.User!.Id, query, cancellationToken);
}
