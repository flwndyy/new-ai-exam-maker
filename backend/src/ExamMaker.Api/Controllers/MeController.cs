using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/me")]
public sealed class MeController(ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Trả hồ sơ nội bộ đã xác thực, đúng schema Me; không trả token hoặc Cognito sub.</summary>
    [HttpGet]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public ActionResult<MeResponse> Get()
    {
        // Thông tin này đã được TokenValidated đối chiếu với database trong cùng request.
        var user = currentUser.User;
        if (user is null) return Challenge();
        return Ok(new MeResponse(user.Id, user.Email, user.FullName, user.Role,
            user.DepartmentId, user.DepartmentName, user.IsActive));
    }
}

// DTO chỉ chứa các trường frontend cần; tách khỏi entity để không lộ dữ liệu nội bộ.
public sealed record MeResponse(Guid Id, string Email, string FullName, string Role,
    Guid? DepartmentId, string? DepartmentName, bool IsActive);
