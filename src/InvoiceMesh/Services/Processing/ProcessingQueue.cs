using System.Threading.Channels;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Services.Processing;

/// <summary>The documents waiting for their processing. An upload returns at once; the worker reads the files one after the other.</summary>
public sealed class ProcessingQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(long invoiceId) => _channel.Writer.TryWrite(invoiceId);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Processes the documents of the queue. After a restart it takes up what was waiting or half done (the queue lives in memory, the state in the database),
/// so no upload is lost with a container restart.
/// </summary>
public sealed class ProcessingWorker(IServiceScopeFactory scopes, ProcessingQueue queue, ILogger<ProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ImDbContext>();

            // A document that was being processed when the container stopped starts over.
            await db.Invoice.IgnoreQueryFilters().Where(i => i.ProcessingStatus == ProcessingStatus.Processing)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ProcessingStatus, ProcessingStatus.Queued), stoppingToken);
            var pending = await db.Invoice.Where(i => i.ProcessingStatus == ProcessingStatus.Queued)
                .OrderBy(i => i.Id).Select(i => i.Id).ToListAsync(stoppingToken);
            pending.ForEach(queue.Enqueue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The waiting documents could not be read.");
        }

        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<InvoiceService>().ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Processing document {Id} failed.", id);
            }
        }
    }
}
