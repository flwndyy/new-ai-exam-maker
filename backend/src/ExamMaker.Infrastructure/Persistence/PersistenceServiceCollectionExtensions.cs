using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExamMaker.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "ExamMaker";

    public static IServiceCollection AddExamMakerPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The connection string comes from Parameter Store or environment variables
        // (ConnectionStrings__ExamMaker). It is resolved lazily, so hosts start without a database.
        services.AddDbContext<ExamMakerDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured.");

            options.UseNpgsql(connectionString);
        });

        return services;
    }
}
