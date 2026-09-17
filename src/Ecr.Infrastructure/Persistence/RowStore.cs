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

        // ⚠ Ідентифікатор їде в `Details` РЯДКОМ: `ResolveGenericMessageAsync`
        // підставляє лише поля типу `string`, тож `long` лишився б у тексті
        // незаміненим плейсхолдером (`Q-341`).
        return found ?? throw new NotFoundException(
            "ECR-DOC-0404",
            $"Екземпляра таблиці {tableInstanceId} не знайдено.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0404.tableInstance",
                ["tableInstanceId"] = tableInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
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
    /// <exception cref="BusinessRuleException">
    /// Ключ рядка вже існує (<c>ECR-ROW-0409</c>) — той самий код, що й у
    /// перевірці «до запису», перетворений із конфлікту БАЗИ (див. коментар
    /// <see cref="IsRowKeyConflict"/>).
    /// </exception>
    public async Task<long> CreateRowAsync(
        long tableInstanceId, PeriodKey periodKey, RowKey rowKey, int ordinal, CancellationToken ct)
    {
        var id = await bulk.ReserveIdsAsync("doc.TableRowSeq", 1, ct).ConfigureAwait(false);
        // ⚠ Час — через IClock, а не DateTime.UtcNow: інакше поведінку на
        // межі періоду неможливо відтворити в тесті (правило 1 із
        // ForbiddenApiTests, ФВ-1.10a).
        var row = new TableRow(periodKey, id, tableInstanceId, rowKey, ordinal, clock.UtcNow);

        db.TableRows.Add(row);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsRowKeyConflict(ex))
        {
            throw DuplicateRowKeyException([rowKey.Value]);
        }

        return id;
    }

    /// <inheritdoc />
    /// <exception cref="BusinessRuleException">
    /// Хоч би один ключ уже існує (<c>ECR-ROW-0409</c>) — див.
    /// <see cref="CreateRowAsync"/>.
    /// </exception>
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

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsRowKeyConflict(ex))
        {
            throw DuplicateRowKeyException([.. rowKeys.Select(k => k.Value)]);
        }

        return ids;
    }

    /// <summary>
    /// Q-245. Конфлікт <c>UQ_TableRow_Key</c> — той самий клас дефекту, що
    /// вже виправлений як Q-241 (TOCTOU без атомарності): і
    /// <c>CreateRowHandler</c>, і <c>PatchCellsHandler.EnforceRowCreationRules</c>
    /// перевіряють дублікат ключа проти знімка, прочитаного на ПОЧАТКУ
    /// обробки запиту, а не проти стану бази в момент запису. Індекс
    /// <c>UQ_TableRow_Key</c> реально не пускає дублікат у дані — але без
    /// цього перехоплення другий із двох одночасних запитів на ТОЙ САМИЙ
    /// ключ падав необробленим <c>DbUpdateException</c> аж до
    /// <c>ExceptionHandlingMiddleware</c>, де немає гілки ні на
    /// <c>DbUpdateException</c>, ні на <c>SqlException</c> — і отримував
    /// голий <c>500</c> замість того самого чистого <c>409 ECR-ROW-0409</c>,
    /// який та сама перевірка вже дає в нераситовому випадку.
    /// </summary>
    /// <remarks>
    /// ⛔ Перевірка «це дублікат ключа?» винесена в <see cref="SqlConflict"/>
    /// (integration-pending фікс необроблених `500` на дублікаті ролі/проєкту/
    /// запису довідника, той самий клас дефекту за межами `doc.TableRow`):
    /// три нові виклики (<c>UserStore.AddRoleAsync</c>,
    /// <c>UnitOfWork.SaveChangesAsync</c>) читають РІВНО ту саму умову, і три
    /// копії магічних чисел 2601/2627 розійшлися б до першої правки одного з
    /// примірників.
    /// </remarks>
    private static bool IsRowKeyConflict(DbUpdateException ex)
        => SqlConflict.IsUniqueConstraintViolation(ex);

    private static BusinessRuleException DuplicateRowKeyException(IReadOnlyList<string> rowKeys)
        => new(
            "ECR-ROW-0409",
            rowKeys.Count == 1
                ? $"Рядок із ключем {rowKeys[0]} у цій таблиці вже існує."
                // ⚠ «Принаймні один», не «усі»: конфлікт бази називає лише те,
                // що ЯКИЙСЬ ключ із батчу зайнятий — SaveChanges падає одним
                // винятком на весь батч, і без додаткового запиту неможливо
                // сказати, котрий саме (а зайвий запит під час обробки збою
                // конкурентного запису — саме те зайве ускладнення, якого
                // це виправлення уникає).
                : $"Принаймні один ключ уже існує серед: {string.Join(", ", rowKeys)}.",
            new Dictionary<string, object?> { ["rowKeys"] = rowKeys });

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Без цього «дотику» <c>RowVersion</c> не піднімається, і оптимістичне
    /// блокування тихо не працює: двоє правлять ті самі комірки, обидва бачать
    /// незмінену версію рядка, і другий перезаписує першого (B04 §2.4).
    ///
    /// ⛔ Фільтр тут — тільки <c>Id</c>, і це НЕ недогляд, хоча саме такий
    /// фільтр був половиною тихого lost update. Предикат на <c>RowVersion</c>
    /// належить не сюди: «підняти <c>ModifiedAt</c>» — наслідок уже ухваленого
    /// рішення, а не саме рішення. Звірка версії робиться атомарно раніше й у
    /// ТІЙ САМІЙ транзакції — <c>NormalizedCellStore.ClaimRowsAsync</c>
    /// (<c>UPDATE … WHERE RowVersion = …</c>), який заразом бере на ці рядки
    /// ексклюзивне блокування до кінця батчу. Тобто коли керування доходить
    /// сюди, рядки вже наші, і другий предикат на версію не просто зайвий — він
    /// не збігся б НІКОЛИ, бо захоплення саме цю версію вже й змінило.
    ///
    /// ⚠ Додати сюди звірку «про всяк випадок» = зламати запис: див. рядок вище.
    ///
    /// ⛔ А ось <c>PeriodKey</c> у фільтрі — обов'язковий, і його відсутність
    /// БУЛА недоглядом. Кластерний ключ <c>doc.TableRow</c> — <c>(PeriodKey,
    /// Id)</c>, таблиця лежить на <c>ps_ByPeriodKey</c>, і жодного індексу з
    /// <c>Id</c> попереду немає й бути не може: <c>07-partition-tables.sql</c>
    /// вирівнює КОЖЕН індекс цих таблиць по схемі партиціонування й падає
    /// (<c>THROW 50031</c>), якщо хоч один лишився поза нею. Тобто «додати
    /// індекс під <c>Id</c>» тут не варіант у принципі — невирівняний індекс
    /// заборонений розгортанням, бо ламає <c>SWITCH PARTITION</c> архівації
    /// (<c>D-23</c>), а вирівняний однаково не дав би засічки без ключа
    /// партиції.
    ///
    /// Заміряно на 4.8 млн рядків / 24 партиції: без <c>PeriodKey</c> —
    /// <c>Index Scan</c>, scan count 25, 34 308 логічних читань; із ним —
    /// <c>Clustered Index Seek</c> з <c>RangePartitionNew</c>, 60 читань.
    /// І це всередині транзакції запису, під блокуваннями, на найгарячішому
    /// шляху системи.
    /// </remarks>
    public async Task TouchRowsAsync(
        IReadOnlyList<long> rowIds, PeriodKey periodKey, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        if (rowIds.Count == 0)
        {
            return;
        }

        await db.TableRows
            .Where(r => r.PeriodKeyValue == periodKey.Value && rowIds.Contains(r.Id))
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
