using ExamMaker.Domain.Catalog;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ExamMaker.Infrastructure.Tests;

// These tests inspect the EF model and migration snapshot only; they never connect to PostgreSQL.
public class CatalogModelTests
{
    private static ExamMakerDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ExamMakerDbContext>()
            .UseNpgsql("Host=localhost;Database=model_tests")
            .Options);

    private static IEntityType Entity<T>(ExamMakerDbContext context) =>
        context.Model.FindEntityType(typeof(T))!;

    [Theory]
    [InlineData(typeof(Department), "departments")]
    [InlineData(typeof(Subject), "subjects")]
    [InlineData(typeof(Topic), "topics")]
    public void Entities_map_to_snake_case_tables(Type type, string table)
    {
        using var context = CreateContext();

        Assert.Equal(table, context.Model.FindEntityType(type)!.GetTableName());
    }

    [Fact]
    public void Topic_columns_match_erd()
    {
        using var context = CreateContext();
        var topic = Entity<Topic>(context);
        var table = StoreObjectIdentifier.Table("topics");

        var columns = topic.GetProperties().Select(p => p.GetColumnName(table)!).Order().ToArray();

        Assert.Equal(new[] { "grade", "id", "name", "sort_order", "subject_id" }, columns);
    }

    [Fact]
    public void Foreign_keys_restrict_deletes()
    {
        using var context = CreateContext();

        var foreignKeys = Entity<Subject>(context).GetForeignKeys()
            .Concat(Entity<Topic>(context).GetForeignKeys())
            .ToList();

        Assert.Equal(2, foreignKeys.Count);
        Assert.All(foreignKeys, fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
    }

    [Fact]
    public void Names_are_unique_within_their_parent()
    {
        using var context = CreateContext();

        Assert.Contains(Entity<Department>(context).GetIndexes(),
            i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual([nameof(Department.Name)]));
        Assert.Contains(Entity<Subject>(context).GetIndexes(),
            i => i.IsUnique && i.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(Subject.DepartmentId), nameof(Subject.Name)]));
        Assert.Contains(Entity<Topic>(context).GetIndexes(),
            i => i.IsUnique && i.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(Topic.SubjectId), nameof(Topic.Grade), nameof(Topic.Name)]));
    }

    [Fact]
    public void Topic_grade_is_limited_to_10_11_12()
    {
        using var context = CreateContext();

        var designModel = context.GetService<IDesignTimeModel>().Model;
        var check = Assert.Single(designModel.FindEntityType(typeof(Topic))!.GetCheckConstraints());

        Assert.Equal("ck_topics_grade", check.Name);
        Assert.Contains("10, 11, 12", check.Sql);
    }

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var context = CreateContext();
        var services = context.GetInfrastructure();
        var snapshot = services.GetRequiredService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var differ = services.GetRequiredService<IMigrationsModelDiffer>();
        var snapshotModel = services.GetRequiredService<IModelRuntimeInitializer>()
            .Initialize(snapshot.Model);

        Assert.False(differ.HasDifferences(
            snapshotModel.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel()));
    }
}
