using System.Security.Claims;
using ExamMaker.Application.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace ExamMaker.Api.Authentication;

public sealed class CognitoBearerEvents(
    IOptions<CognitoOptions> options, IServiceProvider services, CurrentUser currentUser) : JwtBearerEvents
{
    public const string UserIdClaim = "exammaker:user_id";

    /// <summary>Reject tokens when Cognito is unconfigured without requesting discovery.</summary>
    public override Task MessageReceived(MessageReceivedContext context)
    {
        if (!options.Value.IsConfigured)
        {
            context.Fail("Cognito is not configured.");
        }
        return Task.CompletedTask;
    }

    /// <summary>After signature validation, verify the token type and load an existing local account.</summary>
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var subject = SingleClaim(context.Principal!, "sub");
        if (SingleClaim(context.Principal!, "token_use") != "access"
            || string.IsNullOrWhiteSpace(subject) || subject.Length > 128)
        {
            context.Fail("A user access token with a subject is required.");
            return;
        }

        // Resolve the database only for valid tokens so health checks do not require it.
        var directory = services.GetRequiredService<IUserDirectory>();
        var user = await directory.FindBySubjectAsync(subject, context.HttpContext.RequestAborted);
        if (user is null || !user.IsActive || user.Id == Guid.Empty
            || user.CognitoSub != subject || user.Role is not (Roles.Teacher or Roles.Head or Roles.Admin)
            || (user.Role != Roles.Admin && user.DepartmentId is null))
        {
            context.Fail("The local account is unavailable.");
            return;
        }

        // Rebuild the principal so token-supplied roles or groups cannot grant application privileges.
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

    /// <summary>Return an OpenAPI 401 problem and request a valid bearer token.</summary>
    public override async Task Challenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "Unauthorized", "Chưa xác thực");
    }

    /// <summary>Return a 403 problem when an authenticated user fails authorization.</summary>
    public override Task Forbidden(ForbiddenContext context) =>
        WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, "Forbidden", "Không đủ quyền");

    /// <summary>Accept only a single string-valued claim of the requested type.</summary>
    public static string? SingleClaim(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).ToArray();
        return claims.Length == 1 && claims[0].ValueType == ClaimValueTypes.String ? claims[0].Value : null;
    }

    /// <summary>Write a stable problem code and trace identifier without exposing token details.</summary>
    private static Task WriteProblemAsync(HttpContext context, int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, instance: context.Request.Path,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier })
            .ExecuteAsync(context);
}
