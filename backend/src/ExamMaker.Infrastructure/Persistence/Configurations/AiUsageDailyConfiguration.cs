using ExamMaker.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations;

internal sealed class AiUsageDailyConfiguration : IEntityTypeConfiguration<AiUsageDaily>
{
    public void Configure(EntityTypeBuilder<AiUsageDaily> builder)
    {
        builder.ToTable("ai_usage_daily", table =>
            table.HasCheckConstraint("ck_ai_usage_daily_counters", "jobs >= 0 AND tokens >= 0"));
        // Một dòng quota cho mỗi người dùng trong một ngày UTC.
        builder.HasKey(x => new { x.UserId, x.Date }).HasName("pk_ai_usage_daily");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Date).HasColumnName("date").HasColumnType("date");
        builder.Property(x => x.Jobs).HasColumnName("jobs");
        builder.Property(x => x.Tokens).HasColumnName("tokens");
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_ai_usage_daily_users").OnDelete(DeleteBehavior.Restrict);
    }
}
