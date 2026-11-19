using ExamMaker.Domain.Ai;
using ExamMaker.Domain.Auditing;
using ExamMaker.Domain.Exams;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ExamMaker.Infrastructure.Tests;

public class A2SchemaTests
{
    private static ExamMakerDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ExamMakerDbContext>()
            .UseNpgsql("Host=localhost;Database=model_tests").Options);

    [Fact]
    public void Model_contains_every_table_in_section_eight()
    {
        using var context = CreateContext();
        var tables = context.Model.GetEntityTypes().Select(x => x.GetTableName()).Order().ToArray();
        string[] expected = ["ai_generation_jobs", "ai_usage_daily", "audit_logs", "departments",
            "exam_version_questions", "exam_versions", "exams", "question_set_items", "question_sets",
            "questions", "subjects", "topics", "users"];
        Assert.Equal(expected.Order(), tables);
        Assert.All(context.Model.GetEntityTypes().SelectMany(x => x.GetForeignKeys()),
            key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
    }

    [Fact]
    public void Published_versions_have_a_database_unique_filter()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(ExamVersion))!;
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique
            && index.GetFilter() == "status = 'Published'"
            && index.Properties.Select(x => x.Name).SequenceEqual([nameof(ExamVersion.ExamId)]));
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(x => x.Name)
                .SequenceEqual([nameof(ExamVersion.ExamId), nameof(ExamVersion.VersionNo)]));
    }

    [Fact]
    public void Snapshot_and_usage_keys_preserve_order_and_prevent_duplicates()
    {
        using var context = CreateContext();
        var item = context.Model.FindEntityType(typeof(ExamVersionQuestion))!;
        Assert.Equal(new[] { "ExamVersionId", "SortOrder" }, item.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Contains(item.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(x => x.Name).SequenceEqual(["ExamVersionId", "SourceQuestionId"]));
        Assert.Equal("numeric", item.FindProperty(nameof(ExamVersionQuestion.Points))!.GetColumnType());
        Assert.Equal("jsonb", item.FindProperty(nameof(ExamVersionQuestion.PayloadSnapshot))!.GetColumnType());
        Assert.True(item.FindProperty(nameof(ExamVersionQuestion.PayloadSnapshot))!.IsNullable);
        var version = context.Model.FindEntityType(typeof(ExamVersion))!;
        Assert.Equal("numeric", version.FindProperty(nameof(ExamVersion.TotalPoints))!.GetColumnType());
        var usage = context.Model.FindEntityType(typeof(AiUsageDaily))!;
        Assert.Equal(new[] { "UserId", "Date" }, usage.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal("bigint", usage.FindProperty(nameof(AiUsageDaily.Tokens))!.GetColumnType());
    }

    [Theory]
    [InlineData(typeof(Exam), "ck_exams_grade")]
    [InlineData(typeof(ExamVersion), "ck_exam_versions_status")]
    [InlineData(typeof(ExamVersionQuestion), "ck_exam_version_questions_points")]
    [InlineData(typeof(AiUsageDaily), "ck_ai_usage_daily_counters")]
    [InlineData(typeof(AuditLog), "ck_audit_logs_entity_type")]
    public void New_tables_keep_their_check_constraints(Type type, string name)
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        Assert.Contains(model.FindEntityType(type)!.GetCheckConstraints(), check => check.Name == name);
    }
}
