using ExamMaker.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamMaker.Infrastructure.Persistence.Configurations
{
    // Cấu hình ánh xạ class User sang bảng trong database.
    internal sealed class UserConfiguration
        : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            // Chỉ cho phép ba vai trò theo API contract.
            builder.ToTable("users", table =>
            {
                table.HasCheckConstraint(
                    "ck_users_role",
                    "role IN ('teacher', 'head', 'admin')");
                // Giáo viên và tổ trưởng phải thuộc một tổ chuyên môn.
                table.HasCheckConstraint(
                    "ck_users_department",
                    "role = 'admin' OR department_id IS NOT NULL");
            });

            // Chọn Id làm khóa chính.
            builder.HasKey(x => x.Id)
                .HasName("pk_users");

            // Ánh xạ Id sang cột id.
            // Code phải tự gán Guid trước khi lưu người dùng.
            builder.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            // Lưu mã định danh người dùng từ Cognito.
            // Cột này không được NULL.
            builder.Property(x => x.CognitoSub)
                .HasColumnName("cognito_sub")
                .IsRequired();

            // Tạo index duy nhất, ngăn trùng mã Cognito.
            builder.HasIndex(x => x.CognitoSub)
                .IsUnique()
                .HasDatabaseName("ux_users_cognito_sub");

            // Lưu email, không cho phép NULL.
            builder.Property(x => x.Email)
                .HasColumnName("email")
                .IsRequired();

            // Lưu họ tên, không cho phép NULL.
            builder.Property(x => x.FullName)
                .HasColumnName("full_name")
                .IsRequired();

            // Lưu vai trò, không cho phép NULL.
            // Các giá trị vai trò hợp lệ cần ràng buộc riêng.
            builder.Property(x => x.Role)
                .HasColumnName("role")
                .IsRequired();

            // Lưu ID tổ chuyên môn.
            // Cho phép NULL vì DepartmentId có kiểu Guid?.
            builder.Property(x => x.DepartmentId)
                .HasColumnName("department_id");

            // Lưu trạng thái hoạt động của tài khoản.
            builder.Property(x => x.IsActive)
                .HasColumnName("is_active");

            // Mỗi người dùng thuộc tối đa một tổ.
            // Một tổ có thể có nhiều người dùng.
            builder.HasOne(x => x.Department)
                .WithMany()

                // DepartmentId là khóa ngoại trỏ đến tổ.
                .HasForeignKey(x => x.DepartmentId)
                .HasConstraintName("fk_users_departments")

                // Chặn xóa tổ đang có người dùng tham chiếu.
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
