using ExamMaker.Domain.QuestionSets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations
{
    internal sealed class QuestionSetItemConfiguration
        : IEntityTypeConfiguration<QuestionSetItem>
    {
        public void Configure(EntityTypeBuilder<QuestionSetItem> builder)
        {
            builder.ToTable("question_set_items");

            // Mỗi câu hỏi xuất hiện tối đa một lần trong cùng bộ.
            builder.HasKey(x => new { x.QuestionSetId, x.QuestionId })
                .HasName("pk_question_set_items");

            builder.Property(x => x.QuestionSetId)
                .HasColumnName("question_set_id");

            builder.Property(x => x.QuestionId)
                .HasColumnName("question_id");

            // Liên kết đến bộ câu hỏi.
            builder.HasOne(x => x.QuestionSet)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.QuestionSetId)
                .HasConstraintName("fk_question_set_items_question_sets")
                .OnDelete(DeleteBehavior.Restrict);

            // Chặn xóa câu hỏi đang được bộ câu hỏi tham chiếu.
            builder.HasOne(x => x.Question)
                .WithMany()
                .HasForeignKey(x => x.QuestionId)
                .HasConstraintName("fk_question_set_items_questions")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}