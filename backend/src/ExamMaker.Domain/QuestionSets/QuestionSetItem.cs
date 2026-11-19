using System;
using ExamMaker.Domain.Questions;

namespace ExamMaker.Domain.QuestionSets
{
    // Liên kết một bộ câu hỏi với một câu hỏi.
    public class QuestionSetItem
    {
        public Guid QuestionSetId { get; set; }
        public QuestionSet? QuestionSet { get; set; }

        public Guid QuestionId { get; set; }
        public Question? Question { get; set; }
    }
}