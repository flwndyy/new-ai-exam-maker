using System;
using System.Collections.Generic;
using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.QuestionSets
{
    // A teacher-owned collection of reusable bank questions.
    public class QuestionSet
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public Guid CreatedBy { get; set; }
        public User? Creator { get; set; }

        public ICollection<QuestionSetItem> Items { get; set; }
            = new List<QuestionSetItem>();
    }
}