using ExamMaker.Domain.Questions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations
{
    internal sealed class QuestionConfiguration
        : IEntityTypeConfiguration<Question>
    {
        public void Configure(EntityTypeBuilder<Question> builder)
        {
            // Giới hạn các giá trị hợp lệ theo API contract.
            builder.ToTable("questions", table =>
            {
                table.HasCheckConstraint(
                    "ck_questions_grade",
                    "grade IN (10, 11, 12)");

                table.HasCheckConstraint(
                    "ck_questions_type",
                    "type IN ('MultipleChoice', 'TrueFalse', 'ShortAnswer', 'Essay')");

                table.HasCheckConstraint(
                    "ck_questions_difficulty",
                    "difficulty IN ('Recognition', 'Understanding', 'Application', 'HighApplication')");

                table.HasCheckConstraint(
                    "ck_questions_status",
                    "status IN ('Draft', 'Submitted', 'Approved', 'Rejected', 'Archived')");

                table.HasCheckConstraint(
                    "ck_questions_source",
                    "source IN ('AI', 'Manual')");

                table.HasCheckConstraint(
                    "ck_questions_ai_verification",
                    "ai_verification IN ('NotChecked', 'Agree', 'Disagree')");

                table.HasCheckConstraint(
                    "ck_questions_revision",
                    "revision >= 1");
                // Câu AI cần đủ job và thứ tự để unique index chống trùng có hiệu lực.
                table.HasCheckConstraint(
                    "ck_questions_ai_identity",
                    "(ai_job_id IS NULL AND ai_ordinal IS NULL AND source = 'Manual') OR " +
                    "(ai_job_id IS NOT NULL AND ai_ordinal IS NOT NULL AND ai_ordinal >= 1)");
            });

            // Khóa chính được ứng dụng tự gán.
            builder.HasKey(x => x.Id)
                .HasName("pk_questions");

            builder.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            // Môn, chương và tổ quản lý câu hỏi.
            builder.Property(x => x.SubjectId)
                .HasColumnName("subject_id");

            builder.Property(x => x.TopicId)
                .HasColumnName("topic_id");

            builder.Property(x => x.DepartmentId)
                .HasColumnName("department_id");

            builder.Property(x => x.Grade)
                .HasColumnName("grade");

            builder.Property(x => x.Type)
                .HasColumnName("type")
                .IsRequired();

            builder.Property(x => x.Difficulty)
                .HasColumnName("difficulty")
                .IsRequired();

            // Nội dung câu hỏi và đáp án.
            builder.Property(x => x.Stem)
                .HasColumnName("stem")
                .IsRequired();

            builder.Property(x => x.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb")
                .IsRequired();

            builder.Property(x => x.Explanation)
                .HasColumnName("explanation");

            builder.Property(x => x.Status)
                .HasColumnName("status")
                .IsRequired();

            builder.Property(x => x.Revision)
                .HasColumnName("revision");

            builder.Property(x => x.Source)
                .HasColumnName("source")
                .IsRequired();

            // Liên kết job AI và kết quả kiểm chứng.
            builder.Property(x => x.AiJobId)
                .HasColumnName("ai_job_id");

            builder.Property(x => x.AiOrdinal)
                .HasColumnName("ai_ordinal");

            builder.Property(x => x.AiVerification)
                .HasColumnName("ai_verification")
                .IsRequired();

            builder.Property(x => x.AiVerificationNote)
                .HasColumnName("ai_verification_note");

            builder.Property(x => x.AiReviewedByAuthor)
                .HasColumnName("ai_reviewed_by_author");

            // Người tạo và thông tin duyệt.
            builder.Property(x => x.CreatedBy)
                .HasColumnName("created_by");

            builder.Property(x => x.ReviewedBy)
                .HasColumnName("reviewed_by");

            builder.Property(x => x.ReviewNote)
                .HasColumnName("review_note");

            // Các thời điểm được lưu theo UTC.
            builder.Property(x => x.SubmittedAt)
                .HasColumnName("submitted_at")
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.ReviewedAt)
                .HasColumnName("reviewed_at")
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone");

            // Chặn xóa các bản ghi đang được câu hỏi tham chiếu.
            builder.HasOne(x => x.Subject)
                .WithMany()
                .HasForeignKey(x => x.SubjectId)
                .HasConstraintName("fk_questions_subjects")
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.Topic)
                .WithMany()
                .HasForeignKey(x => x.TopicId)
                .HasConstraintName("fk_questions_topics")
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.Department)
                .WithMany()
                .HasForeignKey(x => x.DepartmentId)
                .HasConstraintName("fk_questions_departments")
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.AiJob)
                .WithMany()
                .HasForeignKey(x => x.AiJobId)
                .HasConstraintName("fk_questions_ai_generation_jobs")
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.Creator)
                .WithMany()
                .HasForeignKey(x => x.CreatedBy)
                .HasConstraintName("fk_questions_creator")
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.Reviewer)
                .WithMany()
                .HasForeignKey(x => x.ReviewedBy)
                .HasConstraintName("fk_questions_reviewer")
                .OnDelete(DeleteBehavior.Restrict);

            // Chống lưu trùng câu hỏi khi worker xử lý lại job AI.
            builder.HasIndex(x => new { x.AiJobId, x.AiOrdinal })
                .IsUnique()
                .HasFilter("ai_job_id IS NOT NULL AND ai_ordinal IS NOT NULL")
                .HasDatabaseName("ux_questions_ai_job_ordinal");
        }
    }
}
