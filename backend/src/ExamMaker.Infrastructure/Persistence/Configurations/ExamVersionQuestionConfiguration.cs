using ExamMaker.Domain.Exams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations;

internal sealed class ExamVersionQuestionConfiguration : IEntityTypeConfiguration<ExamVersionQuestion>
{
    public void Configure(EntityTypeBuilder<ExamVersionQuestion> builder)
    {
        builder.ToTable("exam_version_questions", table =>
        {
            table.HasCheckConstraint("ck_exam_version_questions_order", "sort_order >= 1");
            table.HasCheckConstraint("ck_exam_version_questions_points", "points > 0");
            table.HasCheckConstraint("ck_exam_version_questions_revision", "source_revision >= 1");
            table.HasCheckConstraint("ck_exam_version_questions_type",
                "type IS NULL OR type IN ('MultipleChoice', 'TrueFalse', 'ShortAnswer', 'Essay')");
            table.HasCheckConstraint("ck_exam_version_questions_difficulty",
                "difficulty IS NULL OR difficulty IN ('Recognition', 'Understanding', 'Application', 'HighApplication')");
        });
        // Thứ tự chỉ duy nhất trong từng phiên bản.
        builder.HasKey(x => new { x.ExamVersionId, x.SortOrder }).HasName("pk_exam_version_questions");
        builder.Property(x => x.ExamVersionId).HasColumnName("exam_version_id");
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").ValueGeneratedNever();
        builder.Property(x => x.Points).HasColumnName("points").HasColumnType("numeric");
        builder.Property(x => x.SourceQuestionId).HasColumnName("source_question_id");
        builder.Property(x => x.SourceRevision).HasColumnName("source_revision");
        builder.Property(x => x.Type).HasColumnName("type");
        builder.Property(x => x.Difficulty).HasColumnName("difficulty");
        builder.Property(x => x.StemSnapshot).HasColumnName("stem_snapshot");
        builder.Property(x => x.PayloadSnapshot).HasColumnName("payload_snapshot").HasColumnType("jsonb");
        builder.Property(x => x.ExplanationSnapshot).HasColumnName("explanation_snapshot");
        builder.HasOne(x => x.ExamVersion).WithMany(x => x.Questions).HasForeignKey(x => x.ExamVersionId)
            .HasConstraintName("fk_exam_version_questions_versions").OnDelete(DeleteBehavior.Restrict);
        // Cấm xóa câu nguồn đang có trong đề, kể cả đề Draft.
        builder.HasOne(x => x.SourceQuestion).WithMany().HasForeignKey(x => x.SourceQuestionId)
            .HasConstraintName("fk_exam_version_questions_source").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ExamVersionId, x.SourceQuestionId }).IsUnique()
            .HasDatabaseName("ux_exam_version_questions_source");
    }
}
