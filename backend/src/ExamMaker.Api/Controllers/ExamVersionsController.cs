using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Exams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/exam-versions")]
public sealed class ExamVersionsController(IExamManagement exams, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Read live editable content or immutable snapshots after enforcing answer access.</summary>
    [HttpGet("{vid:guid}")]
    public Task<ExamVersionView> Get(Guid vid, CancellationToken cancellationToken) => exams.GetVersionAsync(currentUser.User!.Id, vid, cancellationToken);

    /// <summary>Replace ordered source links, item points and duration on an owned editable version.</summary>
    [HttpPut("{vid:guid}")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public Task<ExamVersionView> Update(Guid vid, ExamVersionInput input, CancellationToken cancellationToken) => exams.UpdateVersionAsync(currentUser.User!.Id, vid, input, cancellationToken);

    /// <summary>Freeze current Approved source content and its canonical hash for review.</summary>
    [HttpPost("{vid:guid}/submit")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public Task<ExamVersionView> Submit(Guid vid, CancellationToken cancellationToken) => Act(vid, ExamAction.Submit, null, cancellationToken);

    /// <summary>Withdraw an undecided submission and reopen its editable composition.</summary>
    [HttpPost("{vid:guid}/withdraw")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public Task<ExamVersionView> Withdraw(Guid vid, CancellationToken cancellationToken) => Act(vid, ExamAction.Withdraw, null, cancellationToken);

    /// <summary>Approve exactly the submission round the reviewer has read, never their own content.</summary>
    [HttpPost("{vid:guid}/approve")]
    [Authorize(Roles = Roles.Head + "," + Roles.Admin)]
    public Task<ExamVersionView> Approve(Guid vid,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ExamReviewInput? input, CancellationToken cancellationToken) => Act(vid, ExamAction.Approve, input, cancellationToken);

    /// <summary>Reject the current review round with a mandatory explanation.</summary>
    [HttpPost("{vid:guid}/reject")]
    [Authorize(Roles = Roles.Head + "," + Roles.Admin)]
    public Task<ExamVersionView> Reject(Guid vid,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ExamReviewInput? input, CancellationToken cancellationToken) => Act(vid, ExamAction.Reject, input, cancellationToken);

    /// <summary>Archive an approved or rejected version permanently under the resource permission matrix.</summary>
    [HttpPost("{vid:guid}/archive")]
    public Task<ExamVersionView> Archive(Guid vid, CancellationToken cancellationToken) => Act(vid, ExamAction.Archive, null, cancellationToken);

    /// <summary>Publish one approved version, rejecting an existing publication of the same exam.</summary>
    [HttpPost("{vid:guid}/publish")]
    [Authorize(Policy = AccessPolicies.Admin)]
    public Task<ExamVersionView> Publish(Guid vid, CancellationToken cancellationToken) => Act(vid, ExamAction.Publish, null, cancellationToken);

    /// <summary>Withdraw publication while retaining the approved immutable snapshot and audit history.</summary>
    [HttpPost("{vid:guid}/unpublish")]
    [Authorize(Policy = AccessPolicies.Admin)]
    public Task<ExamVersionView> Unpublish(Guid vid, CancellationToken cancellationToken) => Act(vid, ExamAction.Unpublish, null, cancellationToken);

    private Task<ExamVersionView> Act(Guid id, ExamAction action, ExamReviewInput? input, CancellationToken cancellationToken) =>
        exams.TransitionAsync(currentUser.User!.Id, id, action, input, cancellationToken);
}
