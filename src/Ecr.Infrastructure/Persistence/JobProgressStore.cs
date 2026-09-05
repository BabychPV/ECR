using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IJobProgressStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class JobProgressStore(EcrDbContext db) : IJobProgressStore
{
    /// <inheritdoc />
    public async Task StartAsync(string jobId, string jobCode, DateTime utcNow, CancellationToken ct)
    {
        var existing = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        // Повторний старт того самого ідентифікатора — це перезапуск після
        // збою, а не друга задача: запис оновлюється, а не дублюється.
        if (existing is null)
        {
            db.JobProgresses.Add(new JobProgress(jobId, jobCode, utcNow));
        }
        else
        {
            existing.Report(0, null, utcNow);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReportAsync(
        string jobId, int percent, string? message, DateTime utcNow, CancellationToken ct)
    {
        var entry = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        // Прогрес задачі, якої немає в журналі, — не привід падати: сама
        // задача від цього не стає менш корисною.
        if (entry is null)
        {
            return;
        }

        entry.Report(percent, message, utcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FinishAsync(
        string jobId, string state, string? errorMessage, DateTime utcNow, CancellationToken ct)
    {
        var entry = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return;
        }

        entry.Finish(state, errorMessage, utcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobStatus?> FindAsync(string jobId, CancellationToken ct)
        => await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.JobId == jobId)
            .Select(p => new JobStatus(p.JobId, p.State, p.Percent, p.Message, p.Error))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
