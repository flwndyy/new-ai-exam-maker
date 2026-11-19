using System;
using ExamMaker.Domain.Catalog;

namespace ExamMaker.Domain.Users
{
    public class User
    {
        public Guid Id { get; set; }

        public string CognitoSub { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string Role { get; set; } = string.Empty;

        public Guid? DepartmentId { get; set; }

        public Department? Department { get; set; }

        public bool IsActive { get; set; } = true;
    }
}