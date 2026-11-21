using ExamMaker.Api.Authorization;
using ExamMaker.Application.Exams;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/exams")]
public sealed class ExamsController(IExamManagement exams, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>List visible exam metadata using documented filters and bounded pagination.</summary>
    [HttpGet]
    public Task<ExamPage> List([FromQuery] ExamQuery query, CancellationToken cancellationToken) => exams.ListAsync(currentUser.User!.Id, query, cancellationToken);

    /// <summary>Read exam metadata and only visible version summaries.</summary>
    [HttpGet("{id:guid}")]
    public Task<ExamDetail> Get(Guid id, CancellationToken cancellationToken) => exams.GetAsync(currentUser.User!.Id, id, cancellationToken);

    /// <summary>Create an owned exam and the first empty draft in one transaction.</summary>
    [HttpPost]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public async Task<ActionResult<ExamDetail>> Create(ExamCreateInput input, CancellationToken cancellationToken)
    {
        var result = await exams.CreateAsync(currentUser.User!.Id, input, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    /// <summary>Update only the author's exam title; subject and grade remain immutable.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public Task<ExamDetail> Update(Guid id, ExamUpdateInput input, CancellationToken cancellationToken) => exams.UpdateAsync(currentUser.User!.Id, id, input, cancellationToken);

    /// <summary>Page visible version summaries without exposing answer-bearing detail.</summary>
    [HttpGet("{id:guid}/versions")]
    public Task<ExamVersionPage> Versions(Guid id, CancellationToken cancellationToken, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        exams.VersionsAsync(currentUser.User!.Id, id, page, pageSize, cancellationToken);

    /// <summary>Copy an eligible prior version into the next numbered draft, defaulting to the latest version.</summary>
    [HttpPost("{id:guid}/versions")]
    [Authorize(Policy = AccessPolicies.ContentAuthor)]
    public async Task<ActionResult<ExamVersionView>> CreateVersion(Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ExamVersionCreateInput? input, CancellationToken cancellationToken)
    {
        var result = await exams.CreateVersionAsync(currentUser.User!.Id, id, input ?? new(), cancellationToken);
        return Created($"/api/v1/exam-versions/{result.Id}", result);
    }
}
