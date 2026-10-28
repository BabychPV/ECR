// src/Ecr.Infrastructure/Persistence/TableFillStore.cs

using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Заповненість таблиць документа — двома згрупованими запитами (<c>BE-10</c>); при Q-17 з увімкненим прапором — ще
/// одним (комірки колонок чужих власників).
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
public sealed class TableFillStore(
    EcrDbContext db,
    IMonthColumnRouter? router = null,
    IMetadataCache? metadata = null) : ITableFillStore
{
    // ⚠ Q-17: `router`/`metadata` необов'язкові з тієї ж причини, що й `archive` у NormalizedCellStore — тести
    // будують `new TableFillStore(db)`. `null` або вимкнений прапор = рахунок як до Q-17 (жодного додаткового запиту).
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

        // ── 2a. Q-17: комірки колонок чужих власників ─────────────────────
        // Введені значення місячного стовпця m / не-місячного стовпця живуть в екземплярі власника, а не перегляду.
        // Фізичні (застарілі) копії таких колонок у перегляді НЕ рахуються — замість них рахуються непорожні комірки
        // власників, приєднані до живих рядків перегляду за RowKey, інакше відсоток заповнення падав би.
        var foreignFilled = await ForeignFilledAsync(
                documentId, periodKey, excluded, [.. rowCounts.Select(r => r.TableDefId)], ct)
            .ConfigureAwait(false);

        var filledByTable = filledByColumn
            .Where(x => !excluded.Contains(x.ColumnDefId)
                        && !foreignFilled.ForeignColumns.Contains((x.TableDefId, x.ColumnDefId)))
            .GroupBy(x => x.TableDefId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.FilledCells));

        foreach (var (tableDefId, cells) in foreignFilled.FilledByTable)
        {
            filledByTable[tableDefId] = filledByTable.GetValueOrDefault(tableDefId) + cells;
        }

        return
        [
            .. rowCounts.Select(r => new TableFillCounts(
                r.TableDefId,
                r.RowCount,
                filledByTable.GetValueOrDefault(r.TableDefId))),
        ];
    }

    /// <summary>
    /// Q-17: непорожні введені комірки колонок із чужими власниками, приєднані за <c>RowKey</c> до живих рядків
    /// екземплярів перегляду, — групи «таблиця × колонка × період власника».
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період перегляду.</param>
    /// <param name="tableDefIds">Таблиці, у яких є колонки з чужими власниками.</param>
    /// <remarks>
    /// ⛔ Ключ партиції — у предикаті кожної з шести джерел (<c>WR-05</c>). Лише введені (<c>IsCalculated = 0</c>)
    /// комірки; видалені рядки обох боків відсікаються. Групується на сервері — у пам'ять їде десяток-сотня рядків.
    /// <para>
    /// ⚠ Періоди-власники — діапазон ключа партиції року перегляду (<c>BETWEEN Рік*100+1 AND Рік*100+12</c>, без
    /// самого періоду перегляду): ОДИН запит замість по запиту на період власника (≤ 11 звернень до
    /// <c>doc.TableInstance</c>/<c>doc.TableRow</c>/<c>doc.CellValue</c> з перекомпіляцією плану кожне). Ключ
    /// партиції входить і в умову зʼєднання рядка з коміркою, тож комірки читаються тими самими діапазонами
    /// (PeriodKey, TableRowId) вузького IX_CellValue_Fill, що й раніше. Відбір «чий власник цей період» робить
    /// виклик у памʼяті по групах «таблиця × колонка × період».
    /// </para>
    /// </remarks>
    public static IQueryable<ForeignFillRow> ForeignFillQuery(
        EcrDbContext db,
        long documentId,
        PeriodKey periodKey,
        IReadOnlyCollection<int> tableDefIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tableDefIds);

        var key = periodKey.Value;
        var firstOwner = (periodKey.Year * 100) + 1;
        var lastOwner = (periodKey.Year * 100) + 12;

        // ⛔ P1-1 (прохід C, 27.10): БЕЗ `ColumnDefId IN (…)` над коміркою. Колишня форма (усі власники одним запитом +
        // список усіх чужих колонок) для кожного рядка власника робила seek по КОЖНІЙ колонці зі списку (синтетика
        // 200 колонок × 12 місяців: 61 380 seek-ів, 185 тис. логічних читань проти 86 при OFF; на Land doc10 —
        // 50–58 с і Execution Timeout). Тут кожен рядок власника читається ОДНИМ діапазоном (PeriodKey, TableRowId)
        // вузького IX_CellValue_Fill, а відбір колонок власника робить виклик у пам'яті по результату
        // «таблиця × колонка × період» (їх сотні, не мільйони).
        return from instance in db.TableInstances.AsNoTracking()
               where instance.DocumentId == documentId
                     && instance.PeriodKeyValue == key
                     && tableDefIds.Contains(instance.TableDefId)
               join row in db.TableRows.AsNoTracking().Where(r => r.PeriodKeyValue == key && !r.IsDeleted)
                   on instance.Id equals row.TableInstanceId
               join owner in db.TableInstances.AsNoTracking()
                       .Where(o => o.DocumentId == documentId
                                   && o.PeriodKeyValue >= firstOwner
                                   && o.PeriodKeyValue <= lastOwner
                                   && o.PeriodKeyValue != key
                                   && tableDefIds.Contains(o.TableDefId))
                   on instance.TableDefId equals owner.TableDefId
               join ownerRow in db.TableRows.AsNoTracking()
                       .Where(r => r.PeriodKeyValue >= firstOwner
                                   && r.PeriodKeyValue <= lastOwner
                                   && r.PeriodKeyValue != key
                                   && !r.IsDeleted)
                   on new { P = owner.PeriodKeyValue, I = owner.Id, K = row.RowKeyValue }
                   equals new { P = ownerRow.PeriodKeyValue, I = ownerRow.TableInstanceId, K = ownerRow.RowKeyValue }
               join cell in db.CellValues.AsNoTracking()
                       .Where(c => c.PeriodKeyValue >= firstOwner
                                   && c.PeriodKeyValue <= lastOwner
                                   && c.PeriodKeyValue != key
                                   && !c.IsCalculated)
                   on new { P = ownerRow.PeriodKeyValue, R = ownerRow.Id }
                   equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
               group cell by new { instance.TableDefId, cell.ColumnDefId, OwnerPeriod = cell.PeriodKeyValue } into g
               select new ForeignFillRow
               {
                   TableDefId = g.Key.TableDefId,
                   ColumnDefId = g.Key.ColumnDefId,
                   OwnerPeriodKey = g.Key.OwnerPeriod,
                   FilledCells = g.Count(),
               };
    }

    /// <summary>Q-17: чисельник по колонках із чужими власниками; без прапора/метаданих — порожньо БЕЗ звернень до бази.</summary>
    private async Task<ForeignFill> ForeignFilledAsync(
        long documentId,
        PeriodKey periodKey,
        HashSet<int> excluded,
        IReadOnlyCollection<int> liveTableDefIds,
        CancellationToken ct)
    {
        if (router is not { Enabled: true } || metadata is null || liveTableDefIds.Count == 0)
        {
            return ForeignFill.None;
        }

        var document = await router.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return ForeignFill.None;
        }

        var snapshot = await metadata.GetAsync(document.TemplateVersionId, ct).ConfigureAwait(false);
        var live = liveTableDefIds.ToHashSet();

        // (таблиця, колонка) → період власника — лише введені колонки (обчислювані/readonly вже в `excluded`).
        var owners = new Dictionary<(int TableDefId, int ColumnDefId), int>();
        foreach (var sheet in snapshot.Sheets.Where(s => !s.IsDeleted))
        {
            foreach (var table in sheet.Tables.Where(t => live.Contains(t.Id) && router.AppliesToTable(t)))
            {
                var foreign = await router.ForeignColumnsAsync(document, table, periodKey, ct).ConfigureAwait(false);
                foreach (var column in foreign.Where(f => !excluded.Contains(f.Column.Id)))
                {
                    owners[(table.Id, column.Column.Id)] = column.OwnerPeriod.Value;
                }
            }
        }

        if (owners.Count == 0)
        {
            return ForeignFill.None;
        }

        // ОДИН запит на всі періоди-власники року (діапазон ключа партиції): див. примітку в `ForeignFillQuery`.
        // Відбір пар «таблиця × колонка» з власником саме цього періоду — тут, у пам'яті.
        var rows = await ForeignFillQuery(db, documentId, periodKey, [.. owners.Keys.Select(o => o.TableDefId).Distinct()])
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var filled = rows
            .Where(r => owners.TryGetValue((r.TableDefId, r.ColumnDefId), out var owner) && owner == r.OwnerPeriodKey)
            .GroupBy(r => r.TableDefId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.FilledCells));

        return new ForeignFill([.. owners.Keys], filled);
    }

    private sealed record ForeignFill(
        HashSet<(int TableDefId, int ColumnDefId)> ForeignColumns, Dictionary<int, int> FilledByTable)
    {
        public static ForeignFill None { get; } = new([], []);
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

/// <summary>Група результату <see cref="TableFillStore.ForeignFillQuery"/>: заповнені комірки колонки власника.</summary>
public sealed class ForeignFillRow
{
    /// <summary>Таблиця.</summary>
    public int TableDefId { get; init; }

    /// <summary>Колонка.</summary>
    public int ColumnDefId { get; init; }

    /// <summary>Період екземпляра-власника.</summary>
    public int OwnerPeriodKey { get; init; }

    /// <summary>Скільки непорожніх введених комірок.</summary>
    public int FilledCells { get; init; }
}