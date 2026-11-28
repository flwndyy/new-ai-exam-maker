using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ExamMaker.Application.Ai.Worker;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>Runs <see cref="AiJobRecovery"/> at start-up and then every <see cref="AiWorkerOptions.RecoveryIntervalSeconds"/>.</summary>
public sealed class AiJobRecoveryService(
    IServiceScopeFactory scopes,
    AiWorkerOptions options,
    ILogger<AiJobRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.RecoveryIntervalSeconds));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AiJobRecovery>().RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "AI job recovery pass failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
