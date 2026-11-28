using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize(Policy = AccessPolicies.Admin)]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController(IUserManagement users, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>List local user profiles with validated filters and pagination.</summary>
    [HttpGet]
    public Task<AdminUserPage> List([FromQuery] UserListQuery query, CancellationToken cancellationToken) =>
        users.ListAsync(query, cancellationToken);

    /// <summary>Read one profile without exposing a password or authentication token.</summary>
    [HttpGet("{id:guid}")]
    public Task<AdminUser> Get(Guid id, CancellationToken cancellationToken) => users.GetAsync(id, cancellationToken);

    /// <summary>Create an invited Cognito account and return its persisted local profile.</summary>
    [HttpPost]
    public async Task<ActionResult<AdminUser>> Create(CreateUserInput input, CancellationToken cancellationToken)
    {
        var user = await users.CreateAsync(currentUser.User!.Id, input, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, user);
    }

    /// <summary>Synchronize profile, role and activation changes using the verified administrator identity.</summary>
    [HttpPut("{id:guid}")]
    public Task<AdminUser> Update(Guid id, UpdateUserInput input, CancellationToken cancellationToken) =>
        users.UpdateAsync(currentUser.User!.Id, id, input, cancellationToken);

    /// <summary>Disable an account without deleting rows referenced by questions, exams or audits.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await users.DeactivateAsync(currentUser.User!.Id, id, cancellationToken);
        return NoContent();
    }

    /// <summary>Report Cognito/database discrepancies without automatically repairing them.</summary>
    [HttpPost("reconcile")]
    public Task<ReconcileReport> Reconcile(CancellationToken cancellationToken) => users.ReconcileAsync(cancellationToken);
}
