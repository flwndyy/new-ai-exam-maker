using Microsoft.AspNetCore.Authorization;

namespace ExamMaker.Api.Authorization;

public static class ContentAuthorizationResults
{
    public const string SelfReview = "SelfReview";

    /// <summary>Map a failed resource authorization result to the documented API problem code.</summary>
    public static IResult ToForbiddenProblem(this AuthorizationResult result, HttpContext context)
    {
        if (result.Succeeded)
            throw new ArgumentException("A failed authorization result is required.", nameof(result));
        var selfReview = result.Failure?.FailureReasons.Any(reason => reason.Message == SelfReview) == true;
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: selfReview ? "Không được tự duyệt nội dung" : "Không đủ quyền",
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = selfReview ? SelfReview : "Forbidden",
                ["traceId"] = context.TraceIdentifier
            });
    }
}
