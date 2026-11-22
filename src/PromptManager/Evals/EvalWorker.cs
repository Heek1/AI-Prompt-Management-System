using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PromptManager.Data;
using PromptManager.Models;

namespace PromptManager.Evals;

/// <summary>In-process queue of eval run ids. The database is the source of truth; this only wakes the worker.</summary>
public sealed class EvalQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(int evalRunId) => _channel.Writer.TryWrite(evalRunId);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Runs evals one at a time outside HTTP requests, so a long eval neither
/// blocks a page nor dies when the browser tab closes.
/// </summary>
public sealed class EvalWorker(EvalQueue queue, IServiceScopeFactory scopes, ILogger<EvalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EvalExecutor>().ExecuteAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Eval worker could not process eval run {EvalRunId}.", id);
            }
        }
    }

    /// <summary>
    /// After a restart: runs that were in progress are marked failed (their
    /// partial results stay), and queued ones are picked up again.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await db.EvalRuns
                .Where(r => r.Status == EvalRunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, EvalRunStatus.Failed)
                    .SetProperty(r => r.Error, "Interrupted by an application restart. Partial results are kept.")
                    .SetProperty(r => r.FinishedAt, DateTime.UtcNow), ct);

            var queued = await db.EvalRuns
                .Where(r => r.Status == EvalRunStatus.Queued)
                .OrderBy(r => r.Id)
                .Select(r => r.Id)
                .ToListAsync(ct);
            foreach (var id in queued)
                queue.Enqueue(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not recover eval runs after start-up.");
        }
    }
}
