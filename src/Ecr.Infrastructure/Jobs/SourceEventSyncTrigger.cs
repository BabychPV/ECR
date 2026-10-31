// src/Ecr.Infrastructure/Jobs/SourceEventSyncTrigger.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// D-464: ЄДИНЕ місце правила «аркуш знову редагований / період відкрито → негайний синк подій» для мапінгів подій
/// (<c>SourceEventMap</c>) — дзеркало D-462 для мапінгів збору.
/// </summary>
/// <remarks>
/// ⛔ Навіщо. Поки аркуш поданий чи затверджений, синк подій нічого не пише й не видаляє (<c>SheetLocked</c>), а
/// наступний плановий прогін може бути за години; так само події, прочитані до відкриття періоду (<c>PeriodNotOpen</c>),
/// чекали чужого розкладу. Тепер повернення погоджувачем, відкликання, перевідкриття документа й відкриття /
/// перевідкриття періоду одразу ставлять синк сутностей-шаблонів, чиї мапінги пишуть у цей аркуш (документ) — повна
/// звірка всього відкритого (річного) періоду.
/// <para>
/// ⚠ Викликає <see cref="MaterializationScheduler"/> зі своїх двох точок — тим самим він успадковує правило транзакції
/// <see cref="IMaterializationScheduler"/>: черга в базі — усередині транзакції переходу (атомарно), черга поза базою —
/// лише після коміту. Постановка — <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/> на ціль сутності
/// (<see cref="SourceEventSyncTarget.Of"/>): злиття з плановою чи ручною задачею в черзі; виконувана не переривається,
/// а нова стає за нею.
/// </para>
/// <para>
/// ⚠ Ціна для аркуша/проєкту без мапінгів подій — ОДИН легкий запит існування (без задачі).
/// </para>
/// </remarks>
internal static class SourceEventSyncTrigger
{
    /// <summary>Аркуш документа знову редагований: синк сутностей, чиї активні мапінги подій пишуть у таблиці аркуша.</summary>
    /// <param name="db">Контекст (той самий, що в транзакції переходу).</param>
    /// <param name="jobs">Черга.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task EnqueueForSheetAsync(
        EcrDbContext db, IBackgroundJobScheduler jobs, long documentId, int sheetDefId, CancellationToken ct)
    {
        var entities = await (
                from m in db.SourceEventMaps.AsNoTracking()
                join t in db.TableDefs.AsNoTracking() on m.TableDefId equals t.Id
                join e in db.SourceEntities.AsNoTracking() on m.SourceEntityId equals e.Id
                where m.IsActive && e.IsActive && m.DocumentId == documentId && t.SheetDefId == sheetDefId
                select m.SourceEntityId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        await EnqueueAsync(jobs, entities, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Періоди проєкту щойно відкрито чи перевідкрито: синк сутностей з активними мапінгами подій у документи проєкту —
    /// лише якщо хоч один із періодів зараз Open/Grace (перехід <c>Scheduled → Closed</c> синку не потребує).
    /// </summary>
    /// <param name="db">Контекст (той самий, що в транзакції переходу).</param>
    /// <param name="jobs">Черга.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKeys">Періоди переходу.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task EnqueueForPeriodsAsync(
        EcrDbContext db, IBackgroundJobScheduler jobs, int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        if (periodKeys.Count == 0)
        {
            return;
        }

        var keys = periodKeys.ToList();
        var entities = await (
                from m in db.SourceEventMaps.AsNoTracking()
                join d in db.Documents.AsNoTracking() on m.DocumentId equals d.Id
                join e in db.SourceEntities.AsNoTracking() on m.SourceEntityId equals e.Id
                where m.IsActive && e.IsActive && d.ProjectId == projectId
                      && db.Periods.Any(p => p.ProjectId == projectId && keys.Contains(p.PeriodKeyValue)
                                             && (p.State == PeriodState.Open || p.State == PeriodState.Grace))
                select m.SourceEntityId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        await EnqueueAsync(jobs, entities, ct).ConfigureAwait(false);
    }

    private static async Task EnqueueAsync(IBackgroundJobScheduler jobs, List<int> entities, CancellationToken ct)
    {
        // ⚠ Без вікна: задача сама бере повну звірку (усі відкриті періоди документів мапінгів), а не lookback.
        foreach (var entityId in entities.Order())
        {
            await jobs
                .EnqueueCoalescedAsync<ISourceEventSyncJob>(
                    SourceEventSyncTarget.Of(entityId), new SourceEventSyncRequest(entityId), ct)
                .ConfigureAwait(false);
        }
    }
}
