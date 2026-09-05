using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Розсилання сповіщень про події робочого процесу і збоїв.
/// </summary>
/// <remarks>
/// Сповіщення — не транзакційна частина операції: якщо пошта недоступна,
/// подання документа все одно відбулося. Тому задача читає чергу подій, а не
/// викликається зсередини use-case.
///
/// ⚠ <b>Транспорт доставки поки відсутній свідомо (P-13).</b> У пакеті немає
/// ні таблиці черги сповіщень, ні налаштувань пошти, ні правил «кому що»:
/// <c>sec.User.Email</c> — єдине, що існує. Вигадати адресатів і шаблони
/// означало б ухвалити за замовника рішення, яке видно лише тоді, коли лист
/// прийшов не тому.
/// <para>
/// Тому задача робить ту частину, яка визначена однозначно: збирає збої за
/// період від попереднього свого прогону і **записує зведення** в
/// <c>itg.MaintenanceRun</c>, звідки його видно в обслуговуванні. Мовчазне
/// «нічого не робимо, бо не вирішено» лишило б збої непоміченими взагалі.
/// </para>
/// </remarks>
public sealed class NotificationJob(EcrDbContext db, IClock clock) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "notification";

    /// <summary>
    /// Скільки збоїв входить у зведення.
    /// </summary>
    /// <remarks>
    /// Сто рядків у зведенні — це вже не сповіщення, а інцидент: читати їх
    /// ніхто не буде, а сам факт переповнення важливіший за перелік.
    /// </remarks>
    public const int MaxDigestItems = 100;

    /// <summary>
    /// Наскільки глибоко дивитися назад, якщо задача працює вперше.
    /// </summary>
    /// <remarks>
    /// Доба, а не «все». Перший запуск інакше вивалив би зведення за весь
    /// час існування журналу — і його б закрили, не читаючи.
    /// </remarks>
    public static TimeSpan FirstRunLookback => TimeSpan.FromDays(1);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var now = clock.UtcNow;
        var since = await SinceAsync(now, ct).ConfigureAwait(false);

        var run = new MaintenanceRun(Code, now);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await progress.ReportAsync(20, "Читання збоїв збору", ct).ConfigureAwait(false);

        // Ідентифікатор перетворюється на рядок ВЖЕ після вибірки: усередині
        // запиту це був би виклик, який SQL Server форматує за своєю мовою.
        var failed = await db.CollectionRuns
            .AsNoTracking()
            .Where(r => r.FinishedAt >= since && r.Status != "Succeeded")
            .OrderByDescending(r => r.FinishedAt)
            .Take(MaxDigestItems)
            .Select(r => new { r.SourceEntityId, r.Status, r.ErrorMessage, r.FinishedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var collection = failed
            .Select(r => new DigestItem(
                "collection",
                r.SourceEntityId.ToString(CultureInfo.InvariantCulture),
                r.Status,
                r.ErrorMessage,
                r.FinishedAt))
            .ToList();

        await progress.ReportAsync(60, "Читання збоїв обслуговування", ct).ConfigureAwait(false);

        var maintenance = await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.FinishedAt >= since && r.Status != "Succeeded" && r.JobCode != Code)
            .OrderByDescending(r => r.FinishedAt)
            .Take(MaxDigestItems)
            .Select(r => new DigestItem("maintenance", r.JobCode, r.Status, r.DetailsJson, r.FinishedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = collection.Concat(maintenance).Take(MaxDigestItems).ToList();

        // ⚠ Порожнє зведення теж записується, зі статусом «Succeeded».
        // Задача, що мовчить, коли все добре, і задача, що не запускалася,
        // ззовні виглядають однаково — а це різні стани.
        run.Complete(
            items.Count == 0 ? "Succeeded" : "Degraded",
            JsonSerializer.Serialize(new Digest(since, now, items.Count, items), Options),
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await progress
            .ReportAsync(100, items.Count == 0 ? "Збоїв немає" : $"Збоїв у зведенні: {items.Count}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Налаштування серіалізації зведення; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Від якого моменту брати збої.</summary>
    /// <remarks>
    /// ⚠ Межа — початок ПОПЕРЕДНЬОГО прогону цієї задачі, а не «останні N
    /// годин». Розклад можуть змінити, задачу — перезапустити руками, і фіксоване
    /// вікно тоді або пропустило б збої, або показало б їх удруге.
    /// </remarks>
    private async Task<DateTime> SinceAsync(DateTime now, CancellationToken ct)
    {
        var previous = await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.JobCode == Code)
            .OrderByDescending(r => r.StartedAt)
            .Take(1)
            .Select(r => (DateTime?)r.StartedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return previous ?? now - FirstRunLookback;
    }

    /// <summary>Зведення за період.</summary>
    /// <param name="Since">Початок вікна.</param>
    /// <param name="Until">Кінець вікна.</param>
    /// <param name="Count">Скільки збоїв увійшло.</param>
    /// <param name="Items">Самі збої.</param>
    private sealed record Digest(DateTime Since, DateTime Until, int Count, IReadOnlyList<DigestItem> Items);

    /// <summary>Один збій у зведенні.</summary>
    /// <param name="Kind">Звідки: <c>collection</c> або <c>maintenance</c>.</param>
    /// <param name="Subject">Що саме: сутність джерела або код задачі.</param>
    /// <param name="Status">Статус прогону.</param>
    /// <param name="Details">Подробиці — без стеків (ФВ-6.11).</param>
    /// <param name="At">Коли завершився.</param>
    private sealed record DigestItem(
        string Kind, string Subject, string Status, string? Details, DateTime? At);
}
