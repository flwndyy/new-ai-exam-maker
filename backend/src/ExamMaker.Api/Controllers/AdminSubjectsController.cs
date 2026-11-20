using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize(Policy = AccessPolicies.Admin)]
[Route("api/v1/admin/subjects")]
public sealed class AdminSubjectsController(ICatalogManagement catalog, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Create a subject using the verified administrator identity and return the documented DTO.</summary>
    [HttpPost]
    public async Task<ActionResult<SubjectView>> Create(SubjectInput input, CancellationToken cancellationToken)
    {
        var result = await catalog.SaveSubjectAsync(currentUser.User!.Id, null, input, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>Replace subject metadata while preserving database reference constraints.</summary>
    [HttpPut("{id:guid}")]
    public Task<SubjectView> Update(Guid id, SubjectInput input, CancellationToken cancellationToken) =>
        catalog.SaveSubjectAsync(currentUser.User!.Id, id, input, cancellationToken);

    /// <summary>Delete an unreferenced subject; dependent data causes a safe conflict response.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await catalog.DeleteSubjectAsync(currentUser.User!.Id, id, cancellationToken);
        return NoContent();
    }
}
