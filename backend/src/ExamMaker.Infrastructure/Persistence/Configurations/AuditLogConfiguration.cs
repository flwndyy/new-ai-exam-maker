using ExamMaker.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs", table =>
        {
            table.HasCheckConstraint("ck_audit_logs_entity_type",
                "entity_type IN ('Question', 'Exam', 'ExamVersion', 'QuestionSet', 'User', 'AiJob', 'Department', 'Subject', 'Topic')");
            table.HasCheckConstraint("ck_audit_logs_system_note",
                "actor_id IS NOT NULL OR (note IS NOT NULL AND length(btrim(note)) > 0)");
            table.HasCheckConstraint("ck_audit_logs_action", "length(btrim(action)) > 0");
        });
        builder.HasKey(x => x.Id).HasName("pk_audit_logs");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ActorId).HasColumnName("actor_id");
        builder.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");
        builder.Property(x => x.IsSecondary).HasColumnName("is_secondary").HasDefaultValue(false);
        builder.Property(x => x.Action).HasColumnName("action").IsRequired();
        builder.Property(x => x.FromStatus).HasColumnName("from_status");
        builder.Property(x => x.ToStatus).HasColumnName("to_status");
        builder.Property(x => x.Note).HasColumnName("note");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId)
            .HasConstraintName("fk_audit_logs_users").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAt })
            .HasDatabaseName("ix_audit_logs_entity_created");
        builder.HasIndex(x => new { x.ActorId, x.CreatedAt })
            .HasDatabaseName("ix_audit_logs_actor_created");
        builder.HasIndex(x => new { x.DepartmentId, x.CreatedAt })
            .HasDatabaseName("ix_audit_logs_department_created");
        builder.HasIndex(x => new { x.CreatedAt, x.Id })
            .HasDatabaseName("ix_audit_logs_created_id");
    }
}
