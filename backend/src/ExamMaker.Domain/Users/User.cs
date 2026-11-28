using System;
using ExamMaker.Domain.Catalog;

namespace ExamMaker.Domain.Users
{
    // Local authorization profile linked to the immutable Cognito subject identifier.
    public class User
    {
        public Guid Id { get; set; }

        public string CognitoSub { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string Role { get; set; } = string.Empty;

        // Optional for admins; the database requires a department for teachers and heads.
        public Guid? DepartmentId { get; set; }

        public Department? Department { get; set; }

        public bool IsActive { get; set; } = true;

        // Records insertion time in UTC; existing accounts are backfilled by the migration.
        public DateTimeOffset CreatedAt { get; set; }
    }
}
