using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "question_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_question_sets_users",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "question_set_items",
                columns: table => new
                {
                    question_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_set_items", x => new { x.question_set_id, x.question_id });
                    table.ForeignKey(
                        name: "fk_question_set_items_question_sets",
                        column: x => x.question_set_id,
                        principalTable: "question_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_question_set_items_questions",
                        column: x => x.question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_question_set_items_question_id",
                table: "question_set_items",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "IX_question_sets_created_by",
                table: "question_sets",
                column: "created_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "question_set_items");

            migrationBuilder.DropTable(
                name: "question_sets");
        }
    }
}
