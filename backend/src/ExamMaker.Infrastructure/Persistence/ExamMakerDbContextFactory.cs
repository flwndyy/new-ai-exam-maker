using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ExamMaker.Infrastructure.Persistence;

/// <summary>Design-time factory for `dotnet ef`. Never connects to a database when only generating migrations.</summary>
internal sealed class ExamMakerDbContextFactory : IDesignTimeDbContextFactory<ExamMakerDbContext>
{
    public ExamMakerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ExamMaker")
            ?? "Host=localhost;Database=exammaker_design";

        var options = new DbContextOptionsBuilder<ExamMakerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ExamMakerDbContext(options);
    }
}
