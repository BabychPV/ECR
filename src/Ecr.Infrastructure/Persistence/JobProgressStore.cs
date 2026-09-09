using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IJobProgressStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class JobProgressStore(EcrDbContext db) : IJobProgressStore
{
    /// <inheritdoc />
    public Task QueueAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int? createdByUserId = null)
        => UpsertAsync(jobId, jobCode, utcNow, entry => entry.Queue(utcNow), createdByUserId, ct);

    /// <inheritdoc />
    public Task StartAsync(string jobId, string jobCode, DateTime utcNow, CancellationToken ct)
        => UpsertAsync(jobId, jobCode, utcNow, entry => entry.Begin(utcNow), createdByUserId: null, ct);

    /// <summary>Створює або оновлює запис прогресу.</summary>
    /// <remarks>
    /// Повторний виклик із тим самим ідентифікатором — це перезапуск після
    /// збою, а не друга задача: запис оновлюється, а не дублюється.
    ///
    /// ⚠ <paramref name="createdByUserId"/> зберігається ЛИШЕ при створенні
    /// нового запису (Q-156). Перезапуск після збою (<c>StartAsync</c> на
    /// вже наявний запис) не передає автора — і не повинен: автор уже
    /// записаний першим <c>QueueAsync</c>, а другий виклик його б стер.
    /// </remarks>
    private async Task UpsertAsync(
        string jobId, string jobCode, DateTime utcNow, Action<JobProgress> apply, int? createdByUserId,
        CancellationToken ct)
    {
        var entry = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            entry = new JobProgress(jobId, jobCode, utcNow, createdByUserId);
            db.JobProgresses.Add(entry);
        }

        apply(entry);

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

    /// <inheritdoc />
    public async Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct)
        => await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.JobId == jobId)
            .Select(p => (int?)p.CreatedByUserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSummary>> ListRecentAsync(int limit, CancellationToken ct)
        => await db.JobProgresses
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Take(limit)
            .Select(p => new JobSummary(p.JobId, p.JobCode, p.State, p.Percent, p.UpdatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> FailStaleAsync(string reason, DateTime utcNow, CancellationToken ct)
    {
        // ⛔ Завантажуються сутності, а не масовий UPDATE: `Finish` — доменний
        // метод, і обходити його прямим SQL означало б повторити його правила
        // (обнулення `Message`, запис `Error`) другим місцем, яке одного дня
        // розійдеться з першим.
        var stale = await db.JobProgresses
            .Where(p => p.State == "Running" || p.State == "Queued")
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var entry in stale)
        {
            entry.Finish("Failed", reason, utcNow);
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return stale.Count;
    }
}
