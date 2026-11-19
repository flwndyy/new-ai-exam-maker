using ExamMaker.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using ExamMaker.Domain.Users;
using ExamMaker.Domain.Ai;
using ExamMaker.Domain.Questions;
using ExamMaker.Domain.QuestionSets;
using ExamMaker.Domain.Exams;
using ExamMaker.Domain.Auditing;

namespace ExamMaker.Infrastructure.Persistence;

public class ExamMakerDbContext(DbContextOptions<ExamMakerDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Subject> Subjects => Set<Subject>();

    public DbSet<Topic> Topics => Set<Topic>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Question> Questions => Set<Question>();

    public DbSet<AiGenerationJob> AiGenerationJobs =>
        Set<AiGenerationJob>();

    public DbSet<QuestionSet> QuestionSets => Set<QuestionSet>();

    public DbSet<QuestionSetItem> QuestionSetItems => Set<QuestionSetItem>();

    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<ExamVersion> ExamVersions => Set<ExamVersion>();
    public DbSet<ExamVersionQuestion> ExamVersionQuestions => Set<ExamVersionQuestion>();

    public DbSet<AiUsageDaily> AiUsageDaily => Set<AiUsageDaily>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExamMakerDbContext).Assembly);
    }
}
