using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Questions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/questions")]
public sealed class QuestionsController(IQuestionManagement questions, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>List only the caller's visible bank questions, applying catalog, state and provenance filters.</summary>
    [HttpGet]
    public Task<QuestionPage> List([FromQuery] QuestionQuery query, CancellationToken cancellationToken) =>
        questions.ListAsync(currentUser.User!.Id, query, cancellationToken);

    /// <summary>Read a visible question; inaccessible identifiers are reported as not found.</summary>
    [HttpGet("{id:guid}")]
    public Task<QuestionView> Get(Guid id, CancellationToken cancellationToken) =>
        questions.GetAsync(currentUser.User!.Id, id, cancellationToken);

    /// <summary>Create a manual draft with its creator and department derived from trusted server data.</summary>
    [HttpPost]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public async Task<ActionResult<QuestionView>> Create(QuestionInput input, CancellationToken cancellationToken)
    {
        var result = await questions.CreateAsync(currentUser.User!.Id, input, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    /// <summary>Edit an owned question, reopening approved content as a new draft revision.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public Task<QuestionView> Update(Guid id, QuestionInput input, CancellationToken cancellationToken) =>
        questions.UpdateAsync(currentUser.User!.Id, id, input, cancellationToken);

    /// <summary>Delete an editable owned question only when reference checks allow it.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await questions.DeleteAsync(currentUser.User!.Id, id, cancellationToken);
        return NoContent();
    }

    /// <summary>Submit an owned draft or rejected question, requiring AI author review first.</summary>
    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public Task<QuestionView> Submit(Guid id, CancellationToken cancellationToken) =>
        questions.TransitionAsync(currentUser.User!.Id, id, QuestionAction.Submit, null, cancellationToken);

    /// <summary>Withdraw an undecided submission and clear its review-round metadata.</summary>
    [HttpPost("{id:guid}/withdraw")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public Task<QuestionView> Withdraw(Guid id, CancellationToken cancellationToken) =>
        questions.TransitionAsync(currentUser.User!.Id, id, QuestionAction.Withdraw, null, cancellationToken);

    /// <summary>Archive an approved question under the role/department rules without altering exam snapshots.</summary>
    [HttpPost("{id:guid}/archive")]
    public Task<QuestionView> Archive(Guid id, CancellationToken cancellationToken) =>
        questions.TransitionAsync(currentUser.User!.Id, id, QuestionAction.Archive, null, cancellationToken);

    /// <summary>Record the author review required before submitting an AI question.</summary>
    [HttpPost("{id:guid}/mark-reviewed")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public Task<QuestionView> MarkReviewed(Guid id, CancellationToken cancellationToken) =>
        questions.TransitionAsync(currentUser.User!.Id, id, QuestionAction.MarkReviewed, null, cancellationToken);

    /// <summary>Approve another author's submitted question, optionally recording a review note.</summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = Roles.Head + "," + Roles.Admin)]
    public Task<QuestionView> Approve(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] QuestionReviewInput? input,
        CancellationToken cancellationToken) =>
        questions.TransitionAsync(currentUser.User!.Id, id, QuestionAction.Approve, input?.Note, cancellationToken);

    /// <summary>Reject another author's submitted question with a mandatory reason.</summary>
    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = Roles.Head + "," + Roles.Admin)]
    public Task<QuestionView> Reject(Guid id, QuestionReviewInput input, CancellationToken cancellationToken) =>
        questions.TransitionAsync(currentUser.User!.Id, id, QuestionAction.Reject, input.Note, cancellationToken);
}
