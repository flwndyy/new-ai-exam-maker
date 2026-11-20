using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/me")]
public sealed class MeController(ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Return the authenticated local profile using the Me schema without a token or Cognito sub.</summary>
    [HttpGet]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public ActionResult<MeResponse> Get()
    {
        // TokenValidated has checked this profile against the database in the same request.
        var user = currentUser.User;
        if (user is null) return Challenge();
        return Ok(new MeResponse(user.Id, user.Email, user.FullName, user.Role,
            user.DepartmentId, user.DepartmentName, user.IsActive));
    }
}

// Expose only frontend profile fields rather than the internal database entity.
public sealed record MeResponse(Guid Id, string Email, string FullName, string Role,
    Guid? DepartmentId, string? DepartmentName, bool IsActive);
