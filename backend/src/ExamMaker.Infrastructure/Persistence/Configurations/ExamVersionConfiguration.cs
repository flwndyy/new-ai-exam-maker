using ExamMaker.Domain.Exams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations;

internal sealed class ExamVersionConfiguration : IEntityTypeConfiguration<ExamVersion>
{
    public void Configure(EntityTypeBuilder<ExamVersion> builder)
    {
        builder.ToTable("exam_versions", table =>
        {
            table.HasCheckConstraint("ck_exam_versions_status",
                "status IN ('Draft', 'Submitted', 'Approved', 'Rejected', 'Published', 'Archived')");
            table.HasCheckConstraint("ck_exam_versions_version_no", "version_no >= 1");
            table.HasCheckConstraint("ck_exam_versions_duration", "duration_minutes BETWEEN 1 AND 600");
            table.HasCheckConstraint("ck_exam_versions_total_points", "total_points >= 0");
            table.HasCheckConstraint("ck_exam_versions_published",
                "status <> 'Published' OR (published_by IS NOT NULL AND published_at IS NOT NULL " +
                "AND content_hash IS NOT NULL AND length(btrim(content_hash)) > 0)");
            table.HasCheckConstraint("ck_exam_versions_rejected",
                "status <> 'Rejected' OR (review_note IS NOT NULL AND length(btrim(review_note)) > 0)");
        });
        builder.HasKey(x => x.Id).HasName("pk_exam_versions");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ExamId).HasColumnName("exam_id");
        builder.Property(x => x.VersionNo).HasColumnName("version_no");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
        builder.Property(x => x.TotalPoints).HasColumnName("total_points").HasColumnType("numeric");
        builder.Property(x => x.ContentHash).HasColumnName("content_hash");
        builder.Property(x => x.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
        builder.Property(x => x.ReviewedAt).HasColumnName("reviewed_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ReviewNote).HasColumnName("review_note");
        builder.Property(x => x.PublishedBy).HasColumnName("published_by");
        builder.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        builder.HasOne(x => x.Exam).WithMany(x => x.Versions).HasForeignKey(x => x.ExamId)
            .HasConstraintName("fk_exam_versions_exams").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewedBy)
            .HasConstraintName("fk_exam_versions_reviewer").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Publisher).WithMany().HasForeignKey(x => x.PublishedBy)
            .HasConstraintName("fk_exam_versions_publisher").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ExamId, x.VersionNo }).IsUnique()
            .HasDatabaseName("ux_exam_versions_exam_version_no");
        // PostgreSQL arbitrates concurrent publication of versions of the same exam.
        builder.HasIndex(x => x.ExamId).IsUnique().HasFilter("status = 'Published'")
            .HasDatabaseName("ux_exam_versions_one_published");
    }
}
