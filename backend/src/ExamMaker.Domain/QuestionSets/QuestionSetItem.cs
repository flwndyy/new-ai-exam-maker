using System;
using ExamMaker.Domain.Questions;

namespace ExamMaker.Domain.QuestionSets
{
    // Membership is unique for each collection/question pair.
    public class QuestionSetItem
    {
        public Guid QuestionSetId { get; set; }
        public QuestionSet? QuestionSet { get; set; }

        public Guid QuestionId { get; set; }
        public Question? Question { get; set; }
    }
}