using System;
using ExamMaker.Domain.Ai;
using ExamMaker.Domain.Catalog;
using ExamMaker.Domain.Users;

namespace ExamMaker.Domain.Questions
{
    public class Question
    {
        public Guid Id { get; set; }

        // Môn, chương và tổ quản lý câu hỏi.
        public Guid SubjectId { get; set; }
        public Subject? Subject { get; set; }

        public Guid TopicId { get; set; }
        public Topic? Topic { get; set; }

        // Tổ quản lý được ghi nhận khi tạo câu hỏi.
        public Guid DepartmentId { get; set; }
        public Department? Department { get; set; }

        // Khối lớp: 10, 11 hoặc 12.
        public int Grade { get; set; }

        // MultipleChoice, TrueFalse, ShortAnswer hoặc Essay.
        public string Type { get; set; } = string.Empty;

        // Recognition, Understanding, Application hoặc HighApplication.
        public string Difficulty { get; set; } = string.Empty;

        // Nội dung câu hỏi dạng Markdown và LaTeX.
        public string Stem { get; set; } = string.Empty;

        // Đáp án và dữ liệu riêng theo dạng câu hỏi, lưu bằng jsonb.
        public string Payload { get; set; } = "{}";

        public string? Explanation { get; set; }

        // Trạng thái duyệt và số lần chỉnh sửa.
        public string Status { get; set; } = "Draft";
        public int Revision { get; set; } = 1;

        // Nguồn câu hỏi: AI hoặc Manual.
        public string Source { get; set; } = "Manual";

        // Câu nhập tay có thể không liên kết job AI.
        public Guid? AiJobId { get; set; }
        public AiGenerationJob? AiJob { get; set; }

        // Thứ tự câu trong job, dùng để chống lưu trùng khi retry.
        public int? AiOrdinal { get; set; }

        // Kết quả AI kiểm chứng đáp án và ghi chú.
        public string AiVerification { get; set; } = "NotChecked";
        public string? AiVerificationNote { get; set; }

        // Người tạo đã rà soát câu hỏi AI hay chưa.
        public bool AiReviewedByAuthor { get; set; }

        // Người tạo câu hỏi.
        public Guid CreatedBy { get; set; }
        public User? Creator { get; set; }

        // Thời điểm gửi duyệt và thông tin người duyệt.
        public DateTime? SubmittedAt { get; set; }

        public Guid? ReviewedBy { get; set; }
        public User? Reviewer { get; set; }

        public DateTime? ReviewedAt { get; set; }
        public string? ReviewNote { get; set; }

        // Thời gian lưu theo UTC.
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}