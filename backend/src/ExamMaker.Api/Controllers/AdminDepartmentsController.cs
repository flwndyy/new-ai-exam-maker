using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize(Policy = AccessPolicies.Admin)]
[Route("api/v1/admin/departments")]
public sealed class AdminDepartmentsController(ICatalogManagement catalog, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Create a department using the verified administrator identity and return the documented DTO.</summary>
    [HttpPost]
    public async Task<ActionResult<DepartmentView>> Create(DepartmentInput input, CancellationToken cancellationToken)
    {
        var result = await catalog.SaveDepartmentAsync(currentUser.User!.Id, null, input, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>Replace department metadata while preserving database reference constraints.</summary>
    [HttpPut("{id:guid}")]
    public Task<DepartmentView> Update(Guid id, DepartmentInput input, CancellationToken cancellationToken) =>
        catalog.SaveDepartmentAsync(currentUser.User!.Id, id, input, cancellationToken);

    /// <summary>Delete an unreferenced department; dependent data causes a safe conflict response.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await catalog.DeleteDepartmentAsync(currentUser.User!.Id, id, cancellationToken);
        return NoContent();
    }
}
