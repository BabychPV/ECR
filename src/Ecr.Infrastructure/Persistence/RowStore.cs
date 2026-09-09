using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Доступ до рядків таблиці документа.
/// </summary>
/// <remarks>
/// Окремий порт, а не узагальнений репозиторій: усе тут упирається в
/// партиційний ключ, і кожен метод має отримати <c>PeriodKey</c>, інакше запит
/// піде по всіх партиціях.
///
/// ⚠ Файла немає в дереві `05-skeleton.md` §1: порт уведений `Q-032`,
/// реалізація — `Q-050`.
/// </remarks>
public sealed class RowStore(EcrDbContext db, BulkCellLoader bulk, Domain.Abstractions.IClock clock)
    : IRowStore
{
    /// <inheritdoc />
    public async Task<TableInstanceRef> ResolveTableInstanceAsync(long tableInstanceId, CancellationToken ct)
    {
        // Один запит через увесь ланцюг: екземпляр → документ → проєкт.
        // TemplateVersionId живе на проєкті, і без нього use-case не знає,
        // яку структуру брати з кешу метаданих.
        var found = await (
            from instance in db.TableInstances.AsNoTracking()
            where instance.Id == tableInstanceId
            join document in db.Documents.AsNoTracking() on instance.DocumentId equals document.Id
            join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
            select new TableInstanceRef(
                instance.Id, instance.DocumentId, instance.TableDefId,
                project.TemplateVersionId, instance.PeriodKeyValue))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return found ?? throw new NotFoundException(
            "ECR-DOC-0404", $"Екземпляра таблиці {tableInstanceId} не знайдено.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>RowVersion</c> віддається рядком у Base64: саме в такому вигляді він
    /// їде клієнтові в <c>baseVersion</c> і повертається назад. Порівнювати
    /// байти на рівні застосунку не потрібно — потрібне точне зіставлення
    /// «те саме чи ні».
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> GetRowVersionsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var rows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && r.TableInstanceId == tableInstanceId
                        && !r.IsDeleted)
            .Select(r => new { r.RowKeyValue, r.RowVersion })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.ToDictionary(r => r.RowKeyValue, r => Convert.ToBase64String(r.RowVersion), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, long>> GetRowIdsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var rows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && r.TableInstanceId == tableInstanceId
                        && !r.IsDeleted)
            .Select(r => new { r.RowKeyValue, r.Id })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.ToDictionary(r => r.RowKeyValue, r => r.Id, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>> GetRowIdsBatchAsync(
        IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        if (tableInstanceIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyDictionary<string, long>>();
        }

        var rows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && tableInstanceIds.Contains(r.TableInstanceId)
                        && !r.IsDeleted)
            .Select(r => new { r.TableInstanceId, r.RowKeyValue, r.Id })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.TableInstanceId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyDictionary<string, long> (g) =>
                    g.ToDictionary(r => r.RowKeyValue, r => r.Id, StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>>> GetRowVersionsBatchAsync(
        IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        if (tableInstanceIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyDictionary<string, string>>();
        }

        var rows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && tableInstanceIds.Contains(r.TableInstanceId)
                        && !r.IsDeleted)
            .Select(r => new { r.TableInstanceId, r.RowKeyValue, r.RowVersion })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.TableInstanceId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyDictionary<string, string> (g) =>
                    g.ToDictionary(
                        r => r.RowKeyValue, r => Convert.ToBase64String(r.RowVersion), StringComparer.Ordinal));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>Id</c> береться з <c>SEQUENCE</c> ДО вставки (B02 §2.3): так рядок і
    /// його комірки можна завантажити одним проходом, без другого кроку з
    /// <c>OUTPUT</c>, який на таблиці з тригером недоступний.
    /// </remarks>
    public async Task<long> CreateRowAsync(
        long tableInstanceId, PeriodKey periodKey, RowKey rowKey, int ordinal, CancellationToken ct)
    {
        var id = await bulk.ReserveIdsAsync("doc.TableRowSeq", 1, ct).ConfigureAwait(false);
        // ⚠ Час — через IClock, а не DateTime.UtcNow: інакше поведінку на
        // межі періоду неможливо відтворити в тесті (правило 1 із
        // ForbiddenApiTests, ФВ-1.10a).
        var row = new TableRow(periodKey, id, tableInstanceId, rowKey, ordinal, clock.UtcNow);

        db.TableRows.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> CreateRowsAsync(
        long tableInstanceId, PeriodKey periodKey, IReadOnlyList<RowKey> rowKeys, int ordinal, CancellationToken ct)
    {
        if (rowKeys.Count == 0)
        {
            return [];
        }

        var firstId = await bulk.ReserveIdsAsync("doc.TableRowSeq", rowKeys.Count, ct).ConfigureAwait(false);
        var utcNow = clock.UtcNow;
        var ids = new List<long>(rowKeys.Count);

        for (var i = 0; i < rowKeys.Count; i++)
        {
            var id = firstId + i;
            ids.Add(id);
            db.TableRows.Add(new TableRow(periodKey, id, tableInstanceId, rowKeys[i], ordinal, utcNow));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ids;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Без цього «дотику» <c>RowVersion</c> не піднімається, і оптимістичне
    /// блокування тихо не працює: двоє правлять ті самі комірки, обидва бачать
    /// незмінену версію рядка, і другий перезаписує першого (B04 §2.4).
    /// </remarks>
    public async Task TouchRowsAsync(IReadOnlyList<long> rowIds, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        if (rowIds.Count == 0)
        {
            return;
        }

        await db.TableRows
            .Where(r => rowIds.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ModifiedAt, utcNow), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Прапорець читається, а не обчислюється: обчислення «чи чинний ще запис
    /// реєстру» на кожен зріз убило б бюджет 400 мс. Його ставить нічна
    /// перевірка інваріантів (ФВ-7.7, D-98).
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, bool>> GetOrphanFlagsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var rows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && r.TableInstanceId == tableInstanceId
                        && !r.IsDeleted)
            .Select(r => new { r.Id, r.IsOrphaned })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => r.IsOrphaned);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TableInstanceRef>> GetTableInstancesAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
        => await db.TableInstances
            .AsNoTracking()
            .Where(t => t.DocumentId == documentId && t.PeriodKeyValue == periodKey.Value)
            .Join(db.Documents, t => t.DocumentId, d => d.Id, (t, d) => new { t, d.ProjectId })
            .Join(db.Projects, x => x.ProjectId, p => p.Id,
                  (x, p) => new TableInstanceRef(
                      x.t.Id, x.t.DocumentId, x.t.TableDefId, p.TemplateVersionId, x.t.PeriodKeyValue))
            .Take(MaxTableInstances)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> EnsureTableInstancesAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        // ⚠ Аркуші документа — це і є перелік того, що в ньому заповнюють
        // (ФВ-3.2). Таблиці беруться з тих аркушів, а не з усього шаблону:
        // документ навмисно може містити частину.
        var sheetIds = await db.DocumentSheets
            .AsNoTracking()
            .Where(s => s.DocumentId == documentId)
            .Select(s => s.SheetDefId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (sheetIds.Count == 0)
        {
            return 0;
        }

        var tableDefIds = await db.TableDefs
            .AsNoTracking()
            .Where(t => sheetIds.Contains(t.SheetDefId) && !t.IsDeleted)
            .Select(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var existing = await db.TableInstances
            .AsNoTracking()
            .Where(t => t.DocumentId == documentId && t.PeriodKeyValue == periodKey.Value)
            .Select(t => t.TableDefId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var missing = tableDefIds.Except(existing).Order().ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        // ⚠ Ідентифікатори — з SEQUENCE і ОДНИМ діапазоном на весь набір:
        // дев'яносто окремих звернень до послідовності коштували б дорожче за
        // саму вставку (B02 §2.3).
        var first = await bulk
            .ReserveIdsAsync("doc.TableInstanceSeq", missing.Count, ct)
            .ConfigureAwait(false);

        var utcNow = clock.UtcNow;

        var created = new Dictionary<int, long>(missing.Count);

        for (var i = 0; i < missing.Count; i++)
        {
            created[missing[i]] = first + i;
            db.TableInstances.Add(new TableInstance(periodKey, first + i, documentId, missing[i], utcNow));
        }

        await MaterializeFixedRowsAsync(created, periodKey, utcNow, ct).ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return missing.Count;
    }

    /// <summary>
    /// Заводить рядки щойно створених екземплярів за описами
    /// <c>cfg.RowDef</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Фіксована таблиця не мала жодного рядка НІКОЛИ (директива №09 `W8`
    /// п.2, `S-13`). Екземпляр створювався, колонки приходили, а рядків не
    /// будував ніхто: `doc.TableRow` заповнював лише `CreateRowAsync` — шлях
    /// «оператор додав рядок», який для `RowMode = Fixed` заборонений за
    /// побудовою. Тобто в таблицю, склад рядків якої заданий шаблоном,
    /// неможливо було ввести перше число.
    ///
    /// ⚠ Разом зі створенням екземпляра і ТІЄЮ Ж транзакцією: екземпляр без
    /// своїх рядків — саме той стан, який щойно описано, і залишати його
    /// досяжним хоч на мить означало б лишити дефект живим на шляху збою.
    ///
    /// ⚠ Ідемпотентність тримає та сама умова, що й для екземплярів: рядки
    /// заводяться лише для тих, кого щойно створили. Повторний виклик
    /// створює нуль екземплярів і, отже, нуль рядків.
    /// </remarks>
    private async Task MaterializeFixedRowsAsync(
        IReadOnlyDictionary<int, long> instancesByTableDef,
        PeriodKey periodKey,
        DateTime utcNow,
        CancellationToken ct)
    {
        var tableDefIds = instancesByTableDef.Keys.ToList();

        var rowDefs = await db.RowDefs
            .AsNoTracking()
            .Where(r => tableDefIds.Contains(r.TableDefId) && !r.IsDeleted)
            .OrderBy(r => r.TableDefId)
            .ThenBy(r => r.Ordinal)
            .Select(r => new { r.Id, r.TableDefId, r.RowKeyValue, r.Ordinal })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rowDefs.Count == 0)
        {
            // Динамічна таблиця описів рядків не має — і це не порожнеча, а
            // її природа: рядки в ній заводить оператор.
            return;
        }

        var firstRowId = await bulk
            .ReserveIdsAsync("doc.TableRowSeq", rowDefs.Count, ct)
            .ConfigureAwait(false);

        for (var i = 0; i < rowDefs.Count; i++)
        {
            var def = rowDefs[i];

            db.TableRows.Add(new TableRow(
                periodKey,
                firstRowId + i,
                instancesByTableDef[def.TableDefId],
                RowKey.Create(def.RowKeyValue),
                def.Ordinal,
                utcNow,
                def.Id));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetOrphanedRowIdsAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
        => await db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value && r.IsOrphaned && !r.IsDeleted)
            .Join(db.TableInstances.Where(t => t.DocumentId == documentId),
                  r => r.TableInstanceId, t => t.Id, (r, _) => r.Id)
            .Take(MaxOrphanReport)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Стеля кількості екземплярів таблиць в одному документі.</summary>
    /// <remarks>
    /// Аркушів у шаблоні 24, таблиць на аркуші — одиниці. Тисяча — межа з
    /// величезним запасом; вона тут не для економії, а щоб запит мав межу.
    /// </remarks>
    private const int MaxTableInstances = 1000;

    /// <summary>
    /// Скільки осиротілих рядків показувати.
    /// </summary>
    /// <remarks>
    /// Подання блокує вже перший — решта потрібна лише щоб людина побачила
    /// масштаб. Повний перелік на зламаному реєстрі був би десятками тисяч
    /// рядків, які ніхто не читатиме.
    /// </remarks>
    private const int MaxOrphanReport = 200;
}
