using ExamMaker.Application.Authentication;

namespace ExamMaker.Api.Authentication;

// Hold only the current request user; never register as a singleton or use static state.
public sealed class CurrentUser : ICurrentUser
{
    public AuthenticatedUser? User { get; private set; }

    /// <summary>Store the database profile in the request scope after successful token validation.</summary>
    public void SetUser(AuthenticatedUser user) => User = user;
}
