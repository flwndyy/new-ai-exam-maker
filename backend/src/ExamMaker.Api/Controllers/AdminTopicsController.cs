using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize(Policy = AccessPolicies.Admin)]
[Route("api/v1/admin/topics")]
public sealed class AdminTopicsController(ICatalogManagement catalog, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Create a topic using the verified administrator identity and return the documented DTO.</summary>
    [HttpPost]
    public async Task<ActionResult<TopicView>> Create(TopicInput input, CancellationToken cancellationToken)
    {
        var result = await catalog.SaveTopicAsync(currentUser.User!.Id, null, input, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>Replace topic metadata while preserving database reference constraints.</summary>
    [HttpPut("{id:guid}")]
    public Task<TopicView> Update(Guid id, TopicInput input, CancellationToken cancellationToken) =>
        catalog.SaveTopicAsync(currentUser.User!.Id, id, input, cancellationToken);

    /// <summary>Delete an unreferenced topic; dependent data causes a safe conflict response.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await catalog.DeleteTopicAsync(currentUser.User!.Id, id, cancellationToken);
        return NoContent();
    }
}
