using ExamMaker.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations;

internal sealed class SubjectConfiguration : IEntityTypeConfiguration<Subject>
{
    public void Configure(EntityTypeBuilder<Subject> builder)
    {
        builder.ToTable("subjects");
        builder.HasKey(x => x.Id).HasName("pk_subjects");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");

        builder.HasOne(x => x.Department)
            .WithMany(x => x.Subjects)
            .HasForeignKey(x => x.DepartmentId)
            .HasConstraintName("fk_subjects_departments")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique().HasDatabaseName("ux_subjects_department_name");
    }
}
