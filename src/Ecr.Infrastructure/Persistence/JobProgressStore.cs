using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IJobProgressStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class JobProgressStore(EcrDbContext db) : IJobProgressStore
{
    /// <inheritdoc />
    public Task QueueAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int? createdByUserId = null,
        string? correlationId = null)
        => UpsertAsync(jobId, jobCode, utcNow, entry => entry.Queue(utcNow, correlationId), createdByUserId, ct);

    /// <inheritdoc />
    public Task StartAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int attempt = 1,
        string? correlationId = null)
        => UpsertAsync(
            jobId, jobCode, utcNow, entry => entry.Begin(utcNow, attempt, correlationId), createdByUserId: null, ct);

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
    public async Task<bool> RestartAsync(string jobId, DateTime utcNow, CancellationToken ct)
    {
        var entry = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return false;
        }

        entry.Queue(utcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<JobStatus?> FindAsync(string jobId, CancellationToken ct)
        => await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.JobId == jobId)
            .Select(p => new JobStatus(
                p.JobId, p.State, p.Percent, p.Message, p.Error, p.Attempt, p.CorrelationId))
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
    public async Task<IReadOnlyList<JobSummary>> ListRecentAsync(
        JobListFilter filter, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = db.JobProgresses.AsNoTracking();

        // ⛔ Три предикати, і кожен — КОН'ЮНКЦІЯ з рештою. «Мої провалені»
        // мусить означати саме це, а не «мої або провалені»: об'єднання
        // віддало б чужі задачі тому, хто права на них не має.
        //
        // ⚠ Автор — це `filter.CreatedByUserId`, який обробник бере з
        // `ICurrentUser`. Жодного шляху сюди з рядка запиту немає за
        // побудовою: тип не має іншого джерела.
        if (filter.CreatedByUserId is { } author)
        {
            query = query.Where(p => p.CreatedByUserId == author);
        }

        if (filter.State is { Length: > 0 } state)
        {
            query = query.Where(p => p.State == state);
        }

        if (filter.JobCode is { Length: > 0 } code)
        {
            query = query.Where(p => p.JobCode == code);
        }

        return await query
            .OrderByDescending(p => p.UpdatedAt)
            .Take(limit)
            .Select(p => new JobSummary(
                p.JobId, p.JobCode, p.State, p.Percent, p.UpdatedAt, p.StartedAt, p.Attempt, p.CorrelationId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task HeartbeatAsync(string jobId, DateTime utcNow, CancellationToken ct)
        // ⚠ Точковий UPDATE, а не завантаження сутності: биття трапляється
        // кожні 30 секунд на КОЖНУ активну задачу, і читати заради нього цілий
        // рядок означало б платити двома запитами за один запис одного поля.
        //
        // ⚠ Фільтр за станом обов'язковий: биття, яке спізнилося й прийшло
        // після `FinishAsync`, інакше воскресило б ознаку життя на вже
        // завершеній задачі.
        => db.JobProgresses
            .Where(p => p.JobId == jobId && (p.State == "Running" || p.State == "Queued"))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.HeartbeatAt, utcNow), ct);

    /// <inheritdoc />
    public async Task<int> FailStaleAsync(string reason, DateTime utcNow, CancellationToken ct)
    {
        // ⛔ Поріг рахується ОДИН раз, до вибірки. Обчислення його всередині
        // циклу зробило б межу рухомою: задача, що почалася поки прибирання
        // йде, могла б потрапити під пізніший, зсунутий поріг.
        var threshold = utcNow - IJobProgressStore.StaleAfter;

        // ⛔ Раніше тут не було предиката взагалі — валився КОЖЕН рядок
        // `Running`/`Queued`. Інстанс не один, і перезапуск сусіда вбивав
        // чужу живу роботу. Покинута задача — це та, чиє биття застигло:
        // процес, який упав, не пише нічого.
        //
        // ⚠ `HeartbeatAt == null` — рядок старший за міграцію, що додала
        // колонку. Процес, який його створив, зупинявся заради розгортання
        // цієї ж міграції, тож він гарантовано мертвий.
        var candidates = await db.JobProgresses
            .AsNoTracking()
            .Where(p => (p.State == "Running" || p.State == "Queued")
                        && (p.HeartbeatAt == null || p.HeartbeatAt < threshold))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var failed = 0;

        foreach (var entry in candidates)
        {
            // ⛔ `Finish` — доменний метод, і правила завершення (запис
            // `Error`, поведінка `Percent`) лишаються ЛИШЕ в ньому: тут він
            // викликається на відчепленій сутності саме щоб обчислити значення,
            // а не щоб продублювати логіку в SQL.
            entry.Finish("Failed", reason, utcNow);

            // ⛔ Умова застарілості ПОВТОРЮЄТЬСЯ в WHERE запису, і це не
            // надмірність. Між вибіркою вище й записом сюди задачу могли
            // перезапустити вручну (`RestartAsync`) або її власник міг
            // прокинутися й ударити — тоді рядок уже не застарілий, і
            // беззастережний UPDATE відтворив би той самий дефект у
            // мікроскопічному вікні. SQL Server перевіряє цю умову й пише
            // атомарно, тож вікна не лишається зовсім.
            var affected = await db.JobProgresses
                .Where(p => p.JobId == entry.JobId
                            && (p.State == "Running" || p.State == "Queued")
                            && (p.HeartbeatAt == null || p.HeartbeatAt < threshold))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(p => p.State, entry.State)
                        .SetProperty(p => p.Error, entry.Error)
                        .SetProperty(p => p.Percent, entry.Percent)
                        .SetProperty(p => p.UpdatedAt, entry.UpdatedAt),
                    ct)
                .ConfigureAwait(false);

            failed += affected;
        }

        // ⚠ Повертається кількість РЕАЛЬНО записаних рядків, а не розмір
        // вибірки: число йде в лог старту, і завищене означало б розслідування
        // задач, яких ніхто не валив.
        return failed;
    }
}
