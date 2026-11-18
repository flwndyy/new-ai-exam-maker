using ExamMaker.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Persistence;

public class ExamMakerDbContext(DbContextOptions<ExamMakerDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Subject> Subjects => Set<Subject>();

    public DbSet<Topic> Topics => Set<Topic>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExamMakerDbContext).Assembly);
    }
}
