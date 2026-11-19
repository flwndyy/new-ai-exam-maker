using System;
using System.Collections.Generic;
using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.QuestionSets
{
    public class QuestionSet
    {
        public Guid Id { get; set; }

        // Tên bộ câu hỏi.
        public string Name { get; set; } = string.Empty;

        // Giáo viên tạo và sở hữu bộ câu hỏi.
        public Guid CreatedBy { get; set; }
        public User? Creator { get; set; }

        // Các câu hỏi thuộc bộ này.
        public ICollection<QuestionSetItem> Items { get; set; }
            = new List<QuestionSetItem>();
    }
}