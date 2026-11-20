using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // Keep legacy email casing while enforcing the API's case-insensitive uniqueness contract.
            // Existing duplicates must be resolved explicitly; this migration never merges accounts.
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_users_email_lower ON users (lower(btrim(email)));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ux_users_email_lower;");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "users");
        }
    }
}
