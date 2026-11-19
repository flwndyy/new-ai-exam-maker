using System;
using ExamMaker.Domain.Ai;
using ExamMaker.Domain.Catalog;
using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Questions
{
    // Bank question with revision tracking, review state and optional AI provenance.
    public class Question
    {
        public Guid Id { get; set; }

        public Guid SubjectId { get; set; }
        public Subject? Subject { get; set; }

        public Guid TopicId { get; set; }
        public Topic? Topic { get; set; }

        public Guid DepartmentId { get; set; }
        public Department? Department { get; set; }

        public int Grade { get; set; }

        public string Type { get; set; } = string.Empty;

        public string Difficulty { get; set; } = string.Empty;

        public string Stem { get; set; } = string.Empty;

        // Type-specific answer data, persisted as jsonb (see context section 8.2).
        public string Payload { get; set; } = "{}";

        public string? Explanation { get; set; }

        public string Status { get; set; } = "Draft";
        public int Revision { get; set; } = 1;

        public string Source { get; set; } = "Manual";

        // Present only for AI questions; the job/ordinal pair makes retries idempotent.
        public Guid? AiJobId { get; set; }
        public AiGenerationJob? AiJob { get; set; }

        public int? AiOrdinal { get; set; }

        public string AiVerification { get; set; } = "NotChecked";
        public string? AiVerificationNote { get; set; }

        public bool AiReviewedByAuthor { get; set; }

        public Guid CreatedBy { get; set; }
        public User? Creator { get; set; }

        public DateTime? SubmittedAt { get; set; }

        public Guid? ReviewedBy { get; set; }
        public User? Reviewer { get; set; }

        public DateTime? ReviewedAt { get; set; }
        public string? ReviewNote { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}