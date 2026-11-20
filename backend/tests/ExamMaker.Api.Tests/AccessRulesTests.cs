using ExamMaker.Application.Authentication;

namespace ExamMaker.Api.Tests;

public sealed class AccessRulesTests
{
    [Theory]
    [InlineData(Roles.Head, true, true, true, ReviewAccess.SelfReview)]
    [InlineData(Roles.Admin, true, true, false, ReviewAccess.SelfReview)]
    [InlineData(Roles.Teacher, true, true, true, ReviewAccess.Forbidden)]
    [InlineData(Roles.Head, true, true, false, ReviewAccess.Forbidden)]
    [InlineData(Roles.Head, true, false, true, ReviewAccess.Allowed)]
    [InlineData(Roles.Admin, true, false, false, ReviewAccess.Allowed)]
    [InlineData(Roles.Admin, false, false, true, ReviewAccess.Forbidden)]
    [InlineData("unknown", true, false, true, ReviewAccess.Forbidden)]
    // Verify reusable application rules without an HTTP host or authorization framework.
    public void Review_checks_role_department_activity_and_ownership(
        string role, bool active, bool owned, bool sameDepartment, ReviewAccess expected)
    {
        var user = AuthTestFactory.Teacher with { Role = role, IsActive = active };
        var resource = new ContentAccessResource(owned ? user.Id : Guid.NewGuid(),
            sameDepartment ? user.DepartmentId!.Value : Guid.NewGuid());
        Assert.Equal(expected, AccessRules.Review(user, resource));
    }

    [Theory]
    [InlineData(false, Roles.Head)]
    [InlineData(true, "unknown")]
    // Invalid or inactive profiles cannot gain ownership or department access.
    public void Invalid_profiles_are_denied_for_other_resource_rules(bool active, string role)
    {
        var user = AuthTestFactory.Teacher with { IsActive = active, Role = role };
        var resource = new ContentAccessResource(user.Id, user.DepartmentId!.Value);
        Assert.False(AccessRules.IsOwner(user, resource));
        Assert.False(AccessRules.IsSameDepartment(user, resource));
    }
}
