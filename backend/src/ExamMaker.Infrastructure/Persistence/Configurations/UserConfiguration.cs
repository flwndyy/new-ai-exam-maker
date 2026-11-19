using ExamMaker.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations
{
    internal sealed class UserConfiguration
        : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder.ToTable("users", table =>
            {
                table.HasCheckConstraint(
                    "ck_users_role",
                    "role IN ('teacher', 'head', 'admin')");
                table.HasCheckConstraint(
                    "ck_users_department",
                    "role = 'admin' OR department_id IS NOT NULL");
            });

            builder.HasKey(x => x.Id)
                .HasName("pk_users");

            builder.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            builder.Property(x => x.CognitoSub)
                .HasColumnName("cognito_sub")
                .IsRequired();

            builder.HasIndex(x => x.CognitoSub)
                .IsUnique()
                .HasDatabaseName("ux_users_cognito_sub");

            builder.Property(x => x.Email)
                .HasColumnName("email")
                .IsRequired();

            builder.Property(x => x.FullName)
                .HasColumnName("full_name")
                .IsRequired();

            builder.Property(x => x.Role)
                .HasColumnName("role")
                .IsRequired();

            builder.Property(x => x.DepartmentId)
                .HasColumnName("department_id");

            builder.Property(x => x.IsActive)
                .HasColumnName("is_active");

            builder.HasOne(x => x.Department)
                .WithMany()

                .HasForeignKey(x => x.DepartmentId)
                .HasConstraintName("fk_users_departments")

                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
