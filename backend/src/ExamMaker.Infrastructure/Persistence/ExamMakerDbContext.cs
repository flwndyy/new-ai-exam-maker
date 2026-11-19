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

    // Cho phép truy vấn và lưu người dùng qua DbContext.
    public DbSet<User> Users => Set<User>();

    // Truy vấn và lưu câu hỏi.
    public DbSet<Question> Questions => Set<Question>();

    // Truy vấn và lưu các yêu cầu sinh câu hỏi AI.
    public DbSet<AiGenerationJob> AiGenerationJobs =>
        Set<AiGenerationJob>();

    public DbSet<QuestionSet> QuestionSets => Set<QuestionSet>();

    public DbSet<QuestionSetItem> QuestionSetItems => Set<QuestionSetItem>();

    // Đề thi, phiên bản và nội dung snapshot của từng câu.
    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<ExamVersion> ExamVersions => Set<ExamVersion>();
    public DbSet<ExamVersionQuestion> ExamVersionQuestions => Set<ExamVersionQuestion>();

    // Quota ngày và nhật ký thao tác phục vụ truy vết.
    public DbSet<AiUsageDaily> AiUsageDaily => Set<AiUsageDaily>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Tự tìm và áp dụng các cấu hình, gồm UserConfiguration.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExamMakerDbContext).Assembly);
    }
}
