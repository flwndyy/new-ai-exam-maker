using ExamMaker.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations;

internal sealed class TopicConfiguration : IEntityTypeConfiguration<Topic>
{
    public void Configure(EntityTypeBuilder<Topic> builder)
    {
        builder.ToTable("topics", t => t.HasCheckConstraint("ck_topics_grade", "grade IN (10, 11, 12)"));
        builder.HasKey(x => x.Id).HasName("pk_topics");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.SubjectId).HasColumnName("subject_id");
        builder.Property(x => x.Grade).HasColumnName("grade");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
        builder.Property(x => x.SortOrder).HasColumnName("sort_order");

        builder.HasOne(x => x.Subject)
            .WithMany(x => x.Topics)
            .HasForeignKey(x => x.SubjectId)
            .HasConstraintName("fk_topics_subjects")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.SubjectId, x.Grade, x.Name }).IsUnique().HasDatabaseName("ux_topics_subject_grade_name");
        builder.HasIndex(x => new { x.SubjectId, x.Grade, x.SortOrder }).HasDatabaseName("ix_topics_subject_grade_sort_order");
    }
}
