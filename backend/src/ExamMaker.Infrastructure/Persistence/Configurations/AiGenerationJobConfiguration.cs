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
            builder.ToTable("ai_generation_jobs", table =>
            {
                table.HasCheckConstraint(
                    "ck_ai_generation_jobs_status",
                    "status IN ('Queued', 'Running', 'Succeeded', 'Failed')");

                table.HasCheckConstraint(
                    "ck_ai_generation_jobs_attempts",
                    "attempt >= 0 AND max_attempts >= 1 AND attempt <= max_attempts");

                table.HasCheckConstraint("ck_ai_generation_jobs_running_lease",
                    "status <> 'Running' OR lease_expires_at IS NOT NULL");
                table.HasCheckConstraint("ck_ai_generation_jobs_finished",
                    "status NOT IN ('Succeeded', 'Failed') OR finished_at IS NOT NULL");
                table.HasCheckConstraint("ck_ai_generation_jobs_error",
                    "(status <> 'Failed' OR (error_code IS NOT NULL AND length(btrim(error_code)) > 0)) " +
                    "AND (status <> 'Succeeded' OR error_code IS NULL)");
                table.HasCheckConstraint(
                    "ck_ai_generation_jobs_tokens",
                    "input_tokens >= 0 AND output_tokens >= 0");
            });

            builder.HasKey(x => x.Id)
                .HasName("pk_ai_generation_jobs");

            builder.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            builder.Property(x => x.RequestedBy)
                .HasColumnName("requested_by");

            builder.HasOne(x => x.Requester)
                .WithMany()
                .HasForeignKey(x => x.RequestedBy)
                .HasConstraintName("fk_ai_generation_jobs_users")
                .OnDelete(DeleteBehavior.Restrict);

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

            builder.Property(x => x.InputTokens)
                .HasColumnName("input_tokens");

            builder.Property(x => x.OutputTokens)
                .HasColumnName("output_tokens");

            builder.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.StartedAt)
                .HasColumnName("started_at")
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.FinishedAt)
                .HasColumnName("finished_at")
                .HasColumnType("timestamp with time zone");

            builder.HasIndex(x => new { x.RequestedBy, x.CreatedAt })
                .HasDatabaseName("ix_ai_generation_jobs_requested_created");

            builder.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
                .HasDatabaseName("ix_ai_generation_jobs_status_lease");
        }
    }
}