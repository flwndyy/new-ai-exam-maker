using ExamMaker.Domain.QuestionSets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations
{
    internal sealed class QuestionSetConfiguration
        : IEntityTypeConfiguration<QuestionSet>
    {
        public void Configure(EntityTypeBuilder<QuestionSet> builder)
        {
            builder.ToTable("question_sets");

            // Khóa chính được ứng dụng tự gán.
            builder.HasKey(x => x.Id)
                .HasName("pk_question_sets");

            builder.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            builder.Property(x => x.Name)
                .HasColumnName("name")
                .IsRequired();

            builder.Property(x => x.CreatedBy)
                .HasColumnName("created_by");

            // Người sở hữu bộ câu hỏi.
            builder.HasOne(x => x.Creator)
                .WithMany()
                .HasForeignKey(x => x.CreatedBy)
                .HasConstraintName("fk_question_sets_users")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}