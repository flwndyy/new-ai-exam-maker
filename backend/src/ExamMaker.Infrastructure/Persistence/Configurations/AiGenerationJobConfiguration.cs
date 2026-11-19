using ExamMaker.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations
{
    internal sealed class AiGenerationJobConfiguration
        : IEntityTypeConfiguration<AiGenerationJob>
    {
        public void Configure(EntityTypeBuilder<AiGenerationJob> builder)
        {
            // Đặt tên bảng và giới hạn các giá trị hợp lệ.
            builder.ToTable("ai_generation_jobs", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_generation_jobs_status",
                    "status IN ('Queued', 'Running', 'Succeeded', 'Failed')");

                table.HasCheckConstraint(
                    "ck_ai_generation_jobs_attempts",
                    "attempt >= 0 AND max_attempts >= 1 AND attempt <= max_attempts");

                table.HasCheckConstraint(
                    "ck_ai_generation_jobs_tokens",
                    "input_tokens >= 0 AND output_tokens >= 0");
            });

            // Khóa chính do ứng dụng tự gán.
            builder.HasKey(x => x.Id)
                .HasName("pk_ai_generation_jobs");

            builder.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            // Người gửi yêu cầu, bắt buộc tồn tại trong bảng users.
            builder.Property(x => x.RequestedBy)
                .HasColumnName("requested_by");

            builder.HasOne(x => x.Requester)
                .WithMany()
                .HasForeignKey(x => x.RequestedBy)
                .HasConstraintName("fk_ai_generation_jobs_users")
                .OnDelete(DeleteBehavior.Restrict);

            // Lưu chuỗi JSON đầu vào bằng kiểu jsonb của PostgreSQL.
            builder.Property(x => x.Params)
                .HasColumnName("params")
                .HasColumnType("jsonb")
                .IsRequired();

            builder.Property(x => x.Status)
                .HasColumnName("status")
                .IsRequired();

            builder.Property(x => x.Attempt)
                .HasColumnName("attempt");

            builder.Property(x => x.MaxAttempts)
                .HasColumnName("max_attempts");

            // Thời hạn worker giữ quyền xử lý job.
            builder.Property(x => x.LeaseExpiresAt)
                .HasColumnName("lease_expires_at")
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.ErrorCode)
                .HasColumnName("error_code");

            builder.Property(x => x.ErrorMessage)
                .HasColumnName("error_message");

            builder.Property(x => x.ModelId)
                .HasColumnName("model_id");

            builder.Property(x => x.PromptVersion)
                .HasColumnName("prompt_version");

            // Bộ đếm token kiểu long được ánh xạ sang bigint.
            builder.Property(x => x.InputTokens)
                .HasColumnName("input_tokens");

            builder.Property(x => x.OutputTokens)
                .HasColumnName("output_tokens");

            // Các giá trị DateTime ghi vào database phải dùng UTC.
            builder.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.StartedAt)
                .HasColumnName("started_at")
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.FinishedAt)
                .HasColumnName("finished_at")
                .HasColumnType("timestamp with time zone");

            // Hỗ trợ tìm danh sách job của từng người dùng.
            builder.HasIndex(x => new { x.RequestedBy, x.CreatedAt })
                .HasDatabaseName("ix_ai_generation_jobs_requested_created");

            // Hỗ trợ tìm job Running đã hết thời hạn xử lý.
            builder.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                .HasDatabaseName("ix_ai_generation_jobs_status_lease");
        }
    }
}