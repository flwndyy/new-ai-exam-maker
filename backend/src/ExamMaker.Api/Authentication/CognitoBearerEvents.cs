using System.Security.Claims;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace ExamMaker.Api.Authentication;

public sealed class CognitoBearerEvents(
    IOptions<CognitoOptions> options, IServiceProvider services, CurrentUser currentUser) : JwtBearerEvents
{
    public const string UserIdClaim = "exammaker:user_id";

    /// <summary>Từ chối token khi chưa cấu hình Cognito; không gọi discovery tới địa chỉ giả.</summary>
    public override Task MessageReceived(MessageReceivedContext context)
    {
        if (!options.Value.IsConfigured)
        {
            context.Fail("Cognito is not configured.");
        }
        return Task.CompletedTask;
    }

    /// <summary>Sau kiểm chữ ký, kiểm loại token và tải quyền từ DB; không tự tạo tài khoản.</summary>
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var subject = SingleClaim(context.Principal!, "sub");
        if (SingleClaim(context.Principal!, "token_use") != "access"
            || string.IsNullOrWhiteSpace(subject) || subject.Length > 128)
        {
            context.Fail("A user access token with a subject is required.");
            return;
        }

        // Chỉ tạo DbContext khi thực sự có token hợp lệ, để /health không cần DB.
        var directory = services.GetRequiredService<IUserDirectory>();
        var user = await directory.FindBySubjectAsync(subject, context.HttpContext.RequestAborted);
        if (user is null || !user.IsActive || user.Id == Guid.Empty
            || user.CognitoSub != subject || user.Role is not ("teacher" or "head" or "admin")
            || (user.Role != "admin" && user.DepartmentId is null))
        {
            context.Fail("The local account is unavailable.");
            return;
        }

        // Tạo lại principal để claim role/group do token mang theo không thể tự nâng quyền.
        var claims = new List<Claim>
        {
            new("sub", subject), new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(UserIdClaim, user.Id.ToString()), new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Role, user.Role)
        };
        if (user.DepartmentId is Guid departmentId) claims.Add(new Claim("department_id", departmentId.ToString()));
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
        currentUser.SetUser(user);
    }

    /// <summary>Trả lỗi 401 theo OpenAPI và yêu cầu client gửi Bearer token hợp lệ.</summary>
    public override async Task Challenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "Unauthorized", "Chưa xác thực");
    }

    /// <summary>Trả lỗi 403 khi user đã đăng nhập nhưng không đạt policy.</summary>
    public override Task Forbidden(ForbiddenContext context) =>
        WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, "Forbidden", "Không đủ quyền");

    /// <summary>Chỉ chấp nhận claim xuất hiện đúng một lần và có giá trị chuỗi.</summary>
    public static string? SingleClaim(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).ToArray();
        return claims.Length == 1 && claims[0].ValueType == ClaimValueTypes.String ? claims[0].Value : null;
    }

    /// <summary>Ghi ProblemDetails có code ổn định và traceId; không lộ chi tiết token.</summary>
    private static Task WriteProblemAsync(HttpContext context, int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, instance: context.Request.Path,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier })
            .ExecuteAsync(context);
}
