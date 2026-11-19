using ExamMaker.Domain.Exams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations;

internal sealed class ExamConfiguration : IEntityTypeConfiguration<Exam>
{
    public void Configure(EntityTypeBuilder<Exam> builder)
    {
        builder.ToTable("exams", table =>
        {
            table.HasCheckConstraint("ck_exams_grade", "grade IN (10, 11, 12)");
            table.HasCheckConstraint("ck_exams_title", "length(btrim(title)) > 0");
        });
        builder.HasKey(x => x.Id).HasName("pk_exams");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
        builder.Property(x => x.SubjectId).HasColumnName("subject_id");
        builder.Property(x => x.Grade).HasColumnName("grade");
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");

        // Giữ các bản ghi được đề tham chiếu, kể cả khi xóa người dùng.
        builder.HasOne(x => x.Subject).WithMany().HasForeignKey(x => x.SubjectId)
            .HasConstraintName("fk_exams_subjects").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId)
            .HasConstraintName("fk_exams_departments").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatedBy)
            .HasConstraintName("fk_exams_users").OnDelete(DeleteBehavior.Restrict);
    }
}
