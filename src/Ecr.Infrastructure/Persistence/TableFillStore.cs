// src/Ecr.Infrastructure/Persistence/TableFillStore.cs

using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Заповненість таблиць документа — двома згрупованими запитами (<c>BE-10</c>).
/// </summary>
/// <remarks>
/// ⛔ <b>Set-based, не по таблиці.</b> Документ має ~91 таблицю; запит на
/// кожну — це 91 похід у базу там, де бюджет усієї відповіді 150 мс. Той
/// самий урок уже записаний над <c>ICellStore.ReadSlicesAsync</c> (<c>Q-165</c>)
/// і <c>IRowStore.GetRowIdsBatchAsync</c> (<c>Q-168</c>).
///
/// ⛔ <b><c>PeriodKey</c> літералом у предикаті КОЖНОЇ з трьох таблиць</b>, а
/// не лише через кореляцію join'а (урок <c>WR-05</c>). <c>doc.CellValue</c> і
/// <c>doc.TableRow</c> лежать на <c>ps_ByPeriodKey</c>, їхні кластерні ключі
/// починаються з <c>PeriodKey</c>, і жодного індексу з іншим стовпцем попереду
/// немає (<c>07-partition-tables.sql</c> падає <c>THROW 50031</c>, якщо такий
/// з'явиться). Запит без цього предиката іде по всіх партиціях.
///
/// ⚠ Запитів саме ДВА, і другий не зайвий. Один іде по
/// <c>doc.CellValue</c> — це і є «один set-based запит» вимоги. Другий іде
/// по <c>doc.TableRow</c> і потрібен тому, що <b>порожній рядок не має
/// жодної комірки</b> (<c>ФВ-3.8</c>): таблиця, у яку ще нічого не ввели,
/// у перший запит не потрапляє взагалі, і знаменник «скільки має заповнити
/// людина» з нього не дістати. Злити їх у <c>LEFT JOIN</c> можна, але тоді
/// обидва агрегати рахуються над декартовим добутком рядків і комірок, і
/// <c>COUNT(DISTINCT)</c> коштує дорожче за другий похід.
/// </remarks>
public sealed class TableFillStore(EcrDbContext db) : ITableFillStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TableFillCounts>> GetFillCountsAsync(
        long documentId,
        PeriodKey periodKey,
        IReadOnlyCollection<int> computedColumnDefIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computedColumnDefIds);

        var key = periodKey.Value;

        // ── 1. Рядки: знаменник ──────────────────────────────────────────
        // Рахуються ВСІ нерозмічені видаленими рядки екземпляра, навіть
        // повністю порожні: саме вони і є те, що людина має заповнити.
        var rowCounts = await (
            from instance in db.TableInstances.AsNoTracking()
            where instance.DocumentId == documentId && instance.PeriodKeyValue == key
            join row in db.TableRows.AsNoTracking()
                    .Where(r => r.PeriodKeyValue == key && !r.IsDeleted)
                on instance.Id equals row.TableInstanceId
            group row by instance.TableDefId into g
            select new { TableDefId = g.Key, RowCount = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ── 2. Комірки: чисельник ────────────────────────────────────────
        // ⚠ `IsEmpty` НЕ фільтрується: явна порожнеча (`R-B4`) — це
        // відповідь людини, а не її відсутність. `IsCalculated` фільтрується
        // — це відповідь перерахунку.
        //
        // ⛔ Обчислювані колонки відсікаються ТУТ, у пам'яті, а не в SQL.
        // `NOT IN (@p1…@pN)` перевіряється на КОЖНІЙ із ~170 тис. комірок
        // документа-періоду: на стенді 2 млн рядків це 340–470 мс CPU проти
        // 190–250 мс із групуванням за колонкою (ключовий стовпець, сотні
        // груп) — і ще окремий план на кожну довжину списку.
        var filledByColumn = await (
            from instance in db.TableInstances.AsNoTracking()
            where instance.DocumentId == documentId && instance.PeriodKeyValue == key
            join row in db.TableRows.AsNoTracking()
                    .Where(r => r.PeriodKeyValue == key && !r.IsDeleted)
                on instance.Id equals row.TableInstanceId
            join cell in db.CellValues.AsNoTracking()
                    .Where(c => c.PeriodKeyValue == key && !c.IsCalculated)
                on row.Id equals cell.TableRowId
            group cell by new { instance.TableDefId, cell.ColumnDefId } into g
            select new { g.Key.TableDefId, g.Key.ColumnDefId, FilledCells = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var excluded = computedColumnDefIds.ToHashSet();
        var filledByTable = filledByColumn
            .Where(x => !excluded.Contains(x.ColumnDefId))
            .GroupBy(x => x.TableDefId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.FilledCells));

        return
        [
            .. rowCounts.Select(r => new TableFillCounts(
                r.TableDefId,
                r.RowCount,
                filledByTable.GetValueOrDefault(r.TableDefId))),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PeriodAccessRuleDef>> GetPeriodAccessRulesAsync(
        int templateVersionId, CancellationToken ct)
        => await db.PeriodAccessRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
