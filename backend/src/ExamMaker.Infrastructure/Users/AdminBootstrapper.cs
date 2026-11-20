using ExamMaker.Application.Users;

namespace ExamMaker.Infrastructure.Users;

// This wrapper is registered exclusively by the private CLI, not by the API host.
public sealed class AdminBootstrapper(UserManagementService users) : IAdminBootstrapper
{
    /// <summary>Enter the first-admin provisioning path from the deployment tool only.</summary>
    public Task<AdminUser> BootstrapAsync(string email, string fullName, CancellationToken cancellationToken) =>
        users.ProvisionFirstAdminAsync(email, fullName, cancellationToken);
}
