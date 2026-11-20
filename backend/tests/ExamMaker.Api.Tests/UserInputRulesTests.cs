using ExamMaker.Application.Users;

namespace ExamMaker.Api.Tests;

public sealed class UserInputRulesTests
{
    [Theory]
    [InlineData("root", null)]
    [InlineData("teacher", null)]
    [InlineData("head", null)]
    // Validate domain input before performing any external side effects.
    public void Invalid_roles_or_missing_departments_are_rejected(string role, string? department)
    {
        var error = Assert.Throws<UserOperationException>(() => UserInputRules.Normalize(
            new("test@example.invalid", "Name", role, department is null ? null : Guid.Parse(department))));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void Input_is_normalized_and_whitespace_or_empty_department_is_rejected()
    {
        var input = UserInputRules.Normalize(new(" Test@Example.Invalid ", " Name ", "admin", null));
        Assert.Equal("test@example.invalid", input.Email);
        Assert.Equal("Name", input.FullName);
        Assert.Throws<UserOperationException>(() => UserInputRules.Normalize(input with { FullName = "  " }));
        Assert.Throws<UserOperationException>(() => UserInputRules.Normalize(input with { Email = "invalid" }));
        Assert.Throws<UserOperationException>(() => UserInputRules.Normalize(input with { DepartmentId = Guid.Empty }));
    }

    [Theory]
    [InlineData("teacher", true)]
    [InlineData("head", true)]
    [InlineData("admin", false)]
    public void Administrators_cannot_lock_themselves_out(string role, bool active)
    {
        var id = Guid.NewGuid();
        Assert.Equal(409, Assert.Throws<UserOperationException>(() => UserInputRules.EnsureNotSelfLockout(id, id, role, active)).StatusCode);
        UserInputRules.EnsureNotSelfLockout(id, Guid.NewGuid(), role, active);
        UserInputRules.EnsureNotSelfLockout(id, id, "admin", true);
    }
}
