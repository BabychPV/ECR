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
public sealed class RowStore(
    EcrDbContext db, BulkCellLoader bulk, Domain.Abstractions.IClock clock, ArchiveAwareCellReader? archive = null)
    : IRowStore
{
    // ⚠ F-13. `archive` — необов'язковий параметр, а не звичайна залежність:
    // десятки тестів у Ecr.Infrastructure.Tests/Ecr.Adapters.Tests
    // конструюють `RowStore` напряму трьома аргументами (db, bulk, clock), і
    // жоден із них не входить у список файлів цього фіксу — робити параметр
    // обов'язковим означало б правити їх усі заради архівного фолбеку, якого
    // ці тести не перевіряють. У DI (`DependencyInjection.cs`) реєстрація
    // звичайна, `archive` завжди резолвиться. `null` тут означає «викликач
    // свідомо не дає архівного читача» — фолбек тоді просто вимкнений
    // (поведінка та сама, що й до цього фіксу), а не падіння з NRE.
    /// <inheritdoc />
    /// <remarks>
    /// <c>WR-05</c>/O3: пошук за <c>Id</c> іде через
    /// <see cref="TableInstancesByIdQuery"/> — див. там, чому це seek, а не
    /// скан усіх партицій, хоч період викликачеві невідомий.
    /// </remarks>
    public async Task<TableInstanceRef> ResolveTableInstanceAsync(long tableInstanceId, CancellationToken ct)
        => (await ResolveTableInstancesAsync([tableInstanceId], ct).ConfigureAwait(false))[tableInstanceId];

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ P8: єдина реалізація і для одного екземпляра
    /// (<see cref="ResolveTableInstanceAsync"/>), і для книги імпорту.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, TableInstanceRef>> ResolveTableInstancesAsync(
        IReadOnlyCollection<long> tableInstanceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        var ids = tableInstanceIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<long, TableInstanceRef>();
        }

        // Один запит через увесь ланцюг: екземпляр → документ → проєкт.
        // TemplateVersionId живе на проєкті, і без нього use-case не знає,
        // яку структуру брати з кешу метаданих.
        var found = (await FindTableInstancesAsync(
                db,
                ids,
                instances =>
                    from instance in instances
                    join document in db.Documents.AsNoTracking() on instance.DocumentId equals document.Id
                    join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
                    select new TableInstanceRef(
                        instance.Id, instance.DocumentId, instance.TableDefId,
                        project.TemplateVersionId, instance.PeriodKeyValue),
                r => r.TableInstanceId,
                ct).ConfigureAwait(false))
            .ToDictionary(r => r.TableInstanceId);

        // ⛔ Відсутній — відмова, а не пропуск: словник без запису викликач
        // міг би прочитати як «такого екземпляра не просили».
        //
        // ⚠ Ідентифікатор їде в `Details` РЯДКОМ: `ResolveGenericMessageAsync`
        // підставляє лише поля типу `string`, тож `long` лишився б у тексті
        // незаміненим плейсхолдером (`Q-341`).
        foreach (var missing in ids.Where(id => !found.ContainsKey(id)))
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Екземпляра таблиці {missing} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.tableInstance",
                    ["tableInstanceId"] = missing.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return found;
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
        var rows = await RowsQuery(db, tableInstanceId, periodKey)
            .Select(r => new { r.RowKeyValue, r.RowVersion })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count > 0 || archive is null)
        {
            return rows.ToDictionary(
                r => r.RowKeyValue, r => Convert.ToBase64String(r.RowVersion), StringComparer.Ordinal);
        }

        // Гарячий запит порожній — період міг бути заархівований
        // (`arc.usp_ArchiveYear` truncate'ить партицію `doc.TableRow`
        // цілком, F-13). `archive is null` — виклик поза DI (тести); там
        // фолбек не потрібен (перевірено вище).
        var archived = await archive.ReadArchivedRowVersionsAsync(tableInstanceId, periodKey, ct)
            .ConfigureAwait(false);
        return archived.ToDictionary(r => r.RowKey, r => r.RowVersion, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, long>> GetRowIdsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var rows = await RowsQuery(db, tableInstanceId, periodKey)
            .Select(r => new { r.RowKeyValue, r.Id })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count > 0 || archive is null)
        {
            return rows.ToDictionary(r => r.RowKeyValue, r => r.Id, StringComparer.Ordinal);
        }

        // Гарячий запит порожній — той самий слід архівації, що й вище.
        var archived = await archive.ReadArchivedRowIdsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);
        return archived.ToDictionary(r => r.RowKey, r => r.Id, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ P8: поштучний метод — це <see cref="GetRowsBatchAsync"/> з одним
    /// екземпляром. Предикат — <see cref="RowsBatchQuery"/> (<c>PeriodKey</c> +
    /// <c>TableInstanceId IN (…)</c>), і сторож <c>WR-05</c>
    /// (<c>PartitionKeyQueryTests</c>) бере його як будь-яку публічну фабрику.
    ///
    /// ⚠ Архівний фолбек (F-13) — ті самі три читання <c>arc.*</c>, лише на
    /// порожньому гарячому результаті. Гарячий шлях платить одним запитом.
    /// </remarks>
    public async Task<IReadOnlyList<RowState>> GetRowsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
        => (await GetRowsBatchAsync([tableInstanceId], periodKey, ct).ConfigureAwait(false))
            .TryGetValue(tableInstanceId, out var rows)
            ? rows
            : [];

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Архівний фолбек — на порожньому гарячому результаті для ВСІХ
    /// екземплярів, як у <see cref="GetRowIdsBatchAsync"/>: архівується
    /// партиція періоду цілком, тож «частина таблиць у гарячій, частина в
    /// архіві» для одного періоду не буває.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<RowState>>> GetRowsBatchAsync(
        IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        if (tableInstanceIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<RowState>>();
        }

        var rows = await RowsBatchQuery(db, tableInstanceIds, periodKey)
            .Select(r => new { r.TableInstanceId, r.RowKeyValue, r.Id, r.RowVersion, r.IsOrphaned })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count > 0 || archive is null)
        {
            return rows
                .GroupBy(r => r.TableInstanceId)
                .ToDictionary(
                    g => g.Key,
                    IReadOnlyList<RowState> (g) => [.. g.Select(r => new RowState(
                        r.RowKeyValue, r.Id, Convert.ToBase64String(r.RowVersion), r.IsOrphaned))]);
        }

        var result = new Dictionary<long, IReadOnlyList<RowState>>();
        foreach (var tableInstanceId in tableInstanceIds.Distinct())
        {
            var archived = await ReadArchivedRowsAsync(archive, tableInstanceId, periodKey, ct).ConfigureAwait(false);
            if (archived.Count > 0)
            {
                result[tableInstanceId] = archived;
            }
        }

        return result;
    }

    /// <summary>Рядки заархівованого екземпляра — три читання <c>arc.*</c> (F-13).</summary>
    private static async Task<IReadOnlyList<RowState>> ReadArchivedRowsAsync(
        ArchiveAwareCellReader archive, long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var ids = await archive.ReadArchivedRowIdsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return [];
        }

        var versions = (await archive.ReadArchivedRowVersionsAsync(tableInstanceId, periodKey, ct)
                .ConfigureAwait(false))
            .ToDictionary(r => r.RowKey, r => r.RowVersion, StringComparer.Ordinal);
        var orphans = (await archive.ReadArchivedOrphanFlagsAsync(tableInstanceId, periodKey, ct)
                .ConfigureAwait(false))
            .ToDictionary(r => r.Id, r => r.IsOrphaned);

        return [.. ids.Select(r => new RowState(
            r.RowKey,
            r.Id,
            versions.GetValueOrDefault(r.RowKey, string.Empty),
            orphans.GetValueOrDefault(r.Id)))];
    }

    /// <summary>Живі рядки одного екземпляра таблиці в його періоді.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період — він же ключ партиції.</param>
    /// <returns>Незавершений запит; проєкцію добирає викликач.</returns>
    /// <remarks>
    /// ⚠ Три методи порту питають ОДНЕ й те саме, різняться лише проєкцією
    /// (<c>RowVersion</c>, <c>Id</c>, <c>IsOrphaned</c>). Три копії предиката
    /// розійшлися б до першої правки одного з них — і розійшлися б тихо: на
    /// малій таблиці різниці не видно, а на партиціонованій ціна помилки —
    /// повний скан (див. <see cref="TouchRowsAsync"/>).
    ///
    /// ⚠ <b>public static</b>: сторож <c>WR-05</c>
    /// (<c>Ecr.Architecture.Tests/PartitionKeyQueryTests</c>) перевіряє
    /// <c>ToQueryString()</c> саме цього запиту, а не його копії в тесті.
    /// </remarks>
    public static IQueryable<TableRow> RowsQuery(EcrDbContext db, long tableInstanceId, PeriodKey periodKey)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && r.TableInstanceId == tableInstanceId
                        && !r.IsDeleted);
    }

    /// <summary>Живі рядки кількох екземплярів таблиць одного періоду.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="tableInstanceIds">Екземпляри таблиць.</param>
    /// <param name="periodKey">Період — він же ключ партиції.</param>
    /// <returns>Незавершений запит; проєкцію добирає викликач.</returns>
    public static IQueryable<TableRow> RowsBatchQuery(
        EcrDbContext db, IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        return db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && tableInstanceIds.Contains(r.TableInstanceId)
                        && !r.IsDeleted);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>> GetRowIdsBatchAsync(
        IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        if (tableInstanceIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyDictionary<string, long>>();
        }

        var rows = await RowsBatchQuery(db, tableInstanceIds, periodKey)
            .Select(r => new { r.TableInstanceId, r.RowKeyValue, r.Id })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count > 0 || archive is null)
        {
            return rows
                .GroupBy(r => r.TableInstanceId)
                .ToDictionary(
                    g => g.Key,
                    IReadOnlyDictionary<string, long> (g) =>
                        g.ToDictionary(r => r.RowKeyValue, r => r.Id, StringComparer.Ordinal));
        }

        // Гарячий запит порожній для ВСІХ переданих екземплярів — типовий
        // слід заархівованого періоду (F-13). Батч тут не найгарячіший шлях
        // (перегляд/експорт заархівованого документа, не PATCH), а його
        // розмір обмежений кількістю таблиць одного документа (~90,
        // `RowStore.MaxTableInstances`), тому цикл по екземплярах прийнятний
        // — і не дублює SQL, уже написаний у ReadArchivedRowIdsAsync.
        var result = new Dictionary<long, IReadOnlyDictionary<string, long>>();
        foreach (var tableInstanceId in tableInstanceIds)
        {
            var archived = await archive.ReadArchivedRowIdsAsync(tableInstanceId, periodKey, ct)
                .ConfigureAwait(false);
            if (archived.Count > 0)
            {
                result[tableInstanceId] = archived.ToDictionary(r => r.RowKey, r => r.Id, StringComparer.Ordinal);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>>> GetRowVersionsBatchAsync(
        IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        if (tableInstanceIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyDictionary<string, string>>();
        }

        var rows = await RowsBatchQuery(db, tableInstanceIds, periodKey)
            .Select(r => new { r.TableInstanceId, r.RowKeyValue, r.RowVersion })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count > 0 || archive is null)
        {
            return rows
                .GroupBy(r => r.TableInstanceId)
                .ToDictionary(
                    g => g.Key,
                    IReadOnlyDictionary<string, string> (g) =>
                        g.ToDictionary(
                            r => r.RowKeyValue, r => Convert.ToBase64String(r.RowVersion), StringComparer.Ordinal));
        }

        // Той самий слід архівації, що й у GetRowIdsBatchAsync поруч.
        var result = new Dictionary<long, IReadOnlyDictionary<string, string>>();
        foreach (var tableInstanceId in tableInstanceIds)
        {
            var archived = await archive.ReadArchivedRowVersionsAsync(tableInstanceId, periodKey, ct)
                .ConfigureAwait(false);
            if (archived.Count > 0)
            {
                result[tableInstanceId] = archived.ToDictionary(r => r.RowKey, r => r.RowVersion, StringComparer.Ordinal);
            }
        }

        return result;
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
        => (await CreateRowsBatchAsync(
            [new RowCreationBatch(tableInstanceId, periodKey, rowKeys, ordinal)], ct).ConfigureAwait(false))[0];

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ P8: єдина реалізація і для одного екземпляра (<see cref="CreateRowsAsync"/>),
    /// і для книги імпорту — ОДИН діапазон <c>SEQUENCE</c> на всі набори в
    /// порядку входу, ОДИН <c>SaveChangesAsync</c>.
    /// </remarks>
    /// <exception cref="BusinessRuleException">
    /// Ключ уже існує (<c>ECR-ROW-0409</c>) — див. <see cref="DuplicateRowKeyException"/>
    /// і <see cref="BlameDuplicateAsync"/>.
    /// </exception>
    public async Task<IReadOnlyList<IReadOnlyList<long>>> CreateRowsBatchAsync(
        IReadOnlyList<RowCreationBatch> batches, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batches);

        var total = batches.Sum(b => b.RowKeys.Count);
        if (total == 0)
        {
            return [.. batches.Select(IReadOnlyList<long> (_) => [])];
        }

        var nextId = await bulk.ReserveIdsAsync("doc.TableRowSeq", total, ct).ConfigureAwait(false);
        var utcNow = clock.UtcNow;
        var result = new List<IReadOnlyList<long>>(batches.Count);

        foreach (var batch in batches)
        {
            var ids = new List<long>(batch.RowKeys.Count);
            foreach (var rowKey in batch.RowKeys)
            {
                ids.Add(nextId);
                db.TableRows.Add(new TableRow(
                    batch.PeriodKey, nextId++, batch.TableInstanceId, rowKey, batch.Ordinal, utcNow));
            }

            result.Add(ids);
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsRowKeyConflict(ex))
        {
            var nonEmpty = batches.Where(b => b.RowKeys.Count > 0).ToList();
            if (nonEmpty.Count == 1)
            {
                // Один набір — рівно та відмова, що була в поштучного
                // (без додаткового запиту й без поля екземпляра).
                throw DuplicateRowKeyException([.. nonEmpty[0].RowKeys.Select(k => k.Value)]);
            }

            throw await BlameDuplicateAsync(nonEmpty, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Та сама відмова <c>ECR-ROW-0409</c>, що дав би поштучний виклик на
    /// ПЕРШОМУ (у порядку входу) наборі, де ключ зайнятий, плюс поле
    /// <c>tableInstanceId</c> (як у <c>ExcelImporter.Blame</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ P8 (DAT-05): відмова без адреси на книзі з ~91 таблиці змушувала б
    /// шукати винну таблицю перебором. Конфлікт бази називає лише «якийсь ключ
    /// зайнятий», тож винного знаходить один запит уже на шляху збою: EF
    /// відкотив свою вставку (власна транзакція або точка збереження у
    /// ширшій), і з'єднання придатне. Зайнятим вважається і ключ, який
    /// повторюється у САМОМУ пакеті (поштучний виклик другого набору впав би
    /// на ключі першого). Винного не знайдено (гонитву вже прибрали) — відмова
    /// з усіма ключами, без екземпляра.
    /// </remarks>
    private async Task<BusinessRuleException> BlameDuplicateAsync(
        IReadOnlyList<RowCreationBatch> batches, CancellationToken ct)
    {
        var instanceIds = batches.Select(b => b.TableInstanceId).Distinct().ToList();
        var periods = batches.Select(b => b.PeriodKey.Value).Distinct().ToList();
        var keys = batches.SelectMany(b => b.RowKeys).Select(k => k.Value).Distinct().ToList();

        // ⚠ Без фільтра IsDeleted: UQ_TableRow_Key його теж не має.
        var existing = await db.TableRows.AsNoTracking()
            .Where(r => periods.Contains(r.PeriodKeyValue)
                        && instanceIds.Contains(r.TableInstanceId)
                        && keys.Contains(r.RowKeyValue))
            .Select(r => new { r.PeriodKeyValue, r.TableInstanceId, r.RowKeyValue })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var taken = existing.Select(r => (r.PeriodKeyValue, r.TableInstanceId, r.RowKeyValue)).ToHashSet();

        foreach (var batch in batches)
        {
            var batchKeys =batch.RowKeys.Select(k => k.Value).ToArray();
            var collides = false;
            foreach (var key in batchKeys)
            {
                collides |= !taken.Add((batch.PeriodKey.Value, batch.TableInstanceId, key));
            }

            if (collides)
            {
                var single = DuplicateRowKeyException(batchKeys);
                var details = new Dictionary<string, object?>(single.Details!, StringComparer.Ordinal)
                {
                    ["tableInstanceId"] = batch.TableInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                return new BusinessRuleException(single.ErrorCode, single.Message, details);
            }
        }

        return DuplicateRowKeyException([.. keys]);
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

    private static BusinessRuleException DuplicateRowKeyException(string[] rowKeys)
        => new(
            "ECR-ROW-0409",
            rowKeys.Length == 1
                ? $"Рядок із ключем {rowKeys[0]} у цій таблиці вже існує."
                // ⚠ «Принаймні один», не «усі»: конфлікт бази називає лише те,
                // що ЯКИЙСЬ ключ із батчу зайнятий — SaveChanges падає одним
                // винятком на весь батч, і без додаткового запиту неможливо
                // сказати, котрий саме (а зайвий запит під час обробки збою
                // конкурентного запису — саме те зайве ускладнення, якого
                // це виправлення уникає).
                : $"Принаймні один ключ уже існує серед: {string.Join(", ", rowKeys)}.",
            // ⚠ Той самий факт, що вже несуть CreateRowHandler/PatchCellsHandler
            // (перевірка ДО запису): тут — програна гонитва проти бази, той
            // самий код і ті самі ключі каталогу.
            rowKeys.Length == 1
                ? new Dictionary<string, object?>
                {
                    ["rowKeys"] = rowKeys,
                    ["messageKey"] = "err.ECR-ROW-0409.rowKeyExists",
                    ["rowKey"] = rowKeys[0],
                }
                : new Dictionary<string, object?>
                {
                    ["rowKeys"] = rowKeys,
                    ["messageKey"] = "err.ECR-ROW-0409.rowKeysExist",
                });

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

        await TouchRowsQuery(db, rowIds, periodKey)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ModifiedAt, utcNow), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Рядки, яких торкається «дотик», — за ідентифікаторами в межах періоду.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="rowIds">Ідентифікатори рядків.</param>
    /// <param name="periodKey">Період — він же ключ партиції.</param>
    /// <returns>Незавершений запит; оновлення добирає викликач.</returns>
    /// <remarks>
    /// ⚠ Запит винесений, щоб сторож <c>WR-05</c> бачив саме його: це той
    /// самий предикат, чия відсутність коштувала 34 308 логічних читань проти
    /// 60 (див. <see cref="TouchRowsAsync"/>), і єдина причина, чому він зараз
    /// правильний, — що колись за це заплатили. Без сторожа ніщо не заважає
    /// заплатити вдруге.
    /// </remarks>
    public static IQueryable<TableRow> TouchRowsQuery(
        EcrDbContext db, IReadOnlyList<long> rowIds, PeriodKey periodKey)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rowIds);

        return db.TableRows
            .Where(r => r.PeriodKeyValue == periodKey.Value && rowIds.Contains(r.Id));
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
        var rows = await RowsQuery(db, tableInstanceId, periodKey)
            .Select(r => new { r.Id, r.IsOrphaned })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count > 0 || archive is null)
        {
            return rows.ToDictionary(r => r.Id, r => r.IsOrphaned);
        }

        // Гарячий запит порожній — той самий слід архівації (F-13). Значення
        // завжди `false`: `OrphanScanJob` архіву не торкається.
        var archived = await archive.ReadArchivedOrphanFlagsAsync(tableInstanceId, periodKey, ct)
            .ConfigureAwait(false);
        return archived.ToDictionary(r => r.Id, r => r.IsOrphaned);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ F-13. Якщо гарячий запит повернув порожній список — фолбек на
    /// <c>arc.TableInstance</c>: період міг бути заархівований
    /// (`arc.usp_ArchiveYear` truncate'ить `doc.TableInstance` разом із
    /// рештою партиції), а не просто «документ ще не відкривали».
    /// </remarks>
    public async Task<IReadOnlyList<TableInstanceRef>> GetTableInstancesAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var hot = await TableInstancesQuery(db, documentId, periodKey)
            .OrderBy(t => t.Id)
            .Join(db.Documents, t => t.DocumentId, d => d.Id, (t, d) => new { t, d.ProjectId })
            .Join(db.Projects, x => x.ProjectId, p => p.Id,
                  (x, p) => new TableInstanceRef(
                      x.t.Id, x.t.DocumentId, x.t.TableDefId, p.TemplateVersionId, x.t.PeriodKeyValue))
            .Take(MaxTableInstances)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (hot.Count > 0 || archive is null)
        {
            return hot;
        }

        return await archive.ReadArchivedTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);
    }

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

        var existing = await TableInstancesQuery(db, documentId, periodKey)
            .Select(t => t.TableDefId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ F-13. `existing` порожній має ДВА зовсім різних читання: «період
        // ще не відкривали» (тоді нижче треба матеріалізувати) і «період
        // заархівовано» (`arc.usp_ArchiveYear` truncate'ить
        // `doc.TableInstance` разом із рештою партиції — і без цієї
        // перевірки метод мовчки фабрикував би НОВІ порожні екземпляри з
        // НОВИМИ Id замість архівних даних, ГОЛОВНИЙ баг F-13). Різницю
        // видає лише запит до `arc.*`.
        if (existing.Count == 0 && tableDefIds.Count > 0 && archive is not null
            && await archive.HasArchivedTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            return 0;
        }

        var missing = tableDefIds.Except(existing).Order().ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        // ⛔ L6-05: період мусить належати проєкту документа. Доти перевірявся
        // лише формат ключа, і `GET …/tables?periodKey=999999` створював
        // екземпляри (і рядки) таблиць для періоду, якого в календарі немає.
        // Перевірка — лише коли є що створювати: повторне відкриття за наявними
        // екземплярами зайвого запиту не платить.
        var periodBelongs = await db.Periods
            .AsNoTracking()
            .AnyAsync(p => p.PeriodKeyValue == periodKey.Value
                           && db.Documents.Any(d => d.Id == documentId && d.ProjectId == p.ProjectId), ct)
            .ConfigureAwait(false);

        if (!periodBelongs)
        {
            throw new BusinessRuleException(
                Domain.Errors.ErrorCodes.PeriodOutOfProject,
                $"Період {periodKey.Value} не належить проєкту документа {documentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0422.periodNotInProjectOfDocument",
                    ["periodKey"] = periodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
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

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (SqlConflict.ViolatesIndex(ex, "UQ_TableInstance"))
        {
            // ⛔ L6-05: два одночасні перші відкриття — звичайна річ (див. опис
            // порту). Друге програє на `UQ_TableInstance` і доти віддавало 500;
            // переможець уже створив і екземпляри, і рядки, тож тут лишається
            // відпустити свої незбережені й відповісти «нічого не створено».
            foreach (var entry in db.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added && e.Entity is TableInstance or TableRow)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }

            return 0;
        }

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
        => await OrphanedRowIdsQuery(db, documentId, periodKey)
            .OrderBy(id => id)
            .Take(MaxOrphanReport)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, int>> GetTableDefIdsOfRowsAsync(
        IReadOnlyCollection<long> rowIds, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        if (rowIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        var ids = rowIds.Distinct().ToList();

        // ⚠ `PeriodKey` літералом на ОБОХ партиціонованих таблицях — той самий
        // урок, що в `OrphanedRowIdsQuery` нижче.
        return await db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value && ids.Contains(r.Id))
            .Join(db.TableInstances.AsNoTracking().Where(t => t.PeriodKeyValue == periodKey.Value),
                  r => r.TableInstanceId, t => t.Id, (r, t) => new { r.Id, t.TableDefId })
            .ToDictionaryAsync(x => x.Id, x => x.TableDefId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Екземпляри таблиць документа в одному періоді.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період — він же ключ партиції.</param>
    /// <returns>Незавершений запит; проєкцію добирає викликач.</returns>
    /// <remarks>
    /// ⚠ <c>doc.TableInstance</c> партиціонована так само, як <c>doc.TableRow</c>
    /// (<c>07-partition-tables.sql:50</c>), і її кластерний ключ теж
    /// <c>(PeriodKey, Id)</c> — тобто «дешевий довідник» вона лише на вигляд.
    /// </remarks>
    public static IQueryable<TableInstance> TableInstancesQuery(
        EcrDbContext db, long documentId, PeriodKey periodKey)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.TableInstances
            .AsNoTracking()
            .Where(t => t.PeriodKeyValue == periodKey.Value && t.DocumentId == documentId);
    }

    /// <summary>
    /// Екземпляри таблиць за <c>Id</c>, коли період викликачеві НЕвідомий
    /// (<c>WR-05</c>/O3).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="tableInstanceIds">Ідентифікатори екземплярів.</param>
    /// <returns>Незавершений запит; з'єднання й проєкцію добирає викликач.</returns>
    /// <remarks>
    /// ⛔ Кластерний ключ <c>doc.TableInstance</c> — <c>(PeriodKey, Id)</c>, і
    /// всі її індекси вирівняні по <c>ps_ByPeriodKey</c>
    /// (<c>07-partition-tables.sql</c> падає з <c>THROW 50031</c> на
    /// невирівняному). Предикат лише за <c>Id</c> тому читає ВЕСЬ індекс у
    /// всіх партиціях: замір I2 — 1 984 логічних читання і ~135 мс ЦП на
    /// кожен <c>GET</c> зрізу (<c>docs/build/perf/I2-annual-recalc-2026-09-29.md</c>).
    ///
    /// <para>Період тут узяти нізвідки — саме цей запит його й визначає. Тому
    /// ключ партиції береться з множини ВСІХ періодів (<c>doc.Period</c> —
    /// десятки рядків): <c>PeriodKey IN (SELECT PeriodKey FROM doc.Period)</c>
    /// дає оптимізатору зовнішній цикл по періодах і рівність за обома
    /// стовпцями ключа, тобто по одному seek на партицію замість скану.
    /// Заміряно на <c>EcrPerfI2</c> (349 560 екземплярів, 24 партиції):
    /// 48 читань замість 1 984.</para>
    ///
    /// ⚠ Екземпляр, чийого періоду немає в <c>doc.Period</c>, цей запит НЕ
    /// знайде. Щоб результат лишився тим самим, що й до O3, викликачі ходять
    /// через <see cref="FindTableInstancesAsync{TResult}"/>, яка для таких
    /// (і для неіснуючих) <c>Id</c> повторює колишній пошук без ключа.
    /// </remarks>
    public static IQueryable<TableInstance> TableInstancesByIdQuery(
        EcrDbContext db, IReadOnlyCollection<long> tableInstanceIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        return db.TableInstances
            .AsNoTracking()
            .Where(t => db.Periods.Select(p => p.PeriodKeyValue).Contains(t.PeriodKeyValue)
                        && tableInstanceIds.Contains(t.Id));
    }

    /// <summary>
    /// Екземпляри таблиць за <c>Id</c>, коли період викликачеві ВІДОМИЙ (O3b).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="tableInstanceIds">Ідентифікатори екземплярів.</param>
    /// <param name="periodKey">Період — він же ключ партиції.</param>
    /// <returns>Незавершений запит; з'єднання й проєкцію добирає викликач.</returns>
    /// <remarks>
    /// ⛔ Рівність за обома стовпцями кластерного ключа <c>(PeriodKey, Id)</c> —
    /// один seek в одній партиції. Замір на <c>EcrPerfI2</c> (зріз 2 088
    /// комірок): 2 читання <c>doc.TableInstance</c> замість 1 984, ЦП запиту
    /// 6 мс замість 101. Для пакета це ще й єдиний дешевий шлях:
    /// <see cref="TableInstancesByIdQuery"/> на 90 <c>Id</c> робить 24 × 90
    /// seek'ів (4 321 читання) — дорожче за скан, який він мав прибрати.
    /// </remarks>
    public static IQueryable<TableInstance> TableInstancesInPeriodQuery(
        EcrDbContext db, IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        return db.TableInstances
            .AsNoTracking()
            .Where(t => t.PeriodKeyValue == periodKey.Value && tableInstanceIds.Contains(t.Id));
    }

    /// <summary>
    /// Екземпляри таблиць за <c>Id</c>: спершу з ключем партиції
    /// (<see cref="TableInstancesByIdQuery"/>), для незнайдених — колишнім
    /// пошуком без ключа.
    /// </summary>
    /// <typeparam name="TResult">Проєкція викликача.</typeparam>
    /// <param name="db">Контекст.</param>
    /// <param name="tableInstanceIds">Ідентифікатори; без повторів.</param>
    /// <param name="shape">З'єднання й проєкція поверх екземплярів.</param>
    /// <param name="idOf">Ідентифікатор екземпляра в проєкції.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Знайдені рядки — той самий набір, що й запит без ключа.</returns>
    /// <remarks>
    /// ⚠ Другий запит — не оптимізація, а гарантія еквівалентності: він
    /// виконується лише тоді, коли першого не вистачило (неіснуючий <c>Id</c>
    /// → 404, або період без рядка в <c>doc.Period</c>), і коштує рівно
    /// стільки, скільки весь пошук коштував до O3. На робочому шляху —
    /// жодного разу.
    /// </remarks>
    internal static async Task<List<TResult>> FindTableInstancesAsync<TResult>(
        EcrDbContext db,
        IReadOnlyCollection<long> tableInstanceIds,
        Func<IQueryable<TableInstance>, IQueryable<TResult>> shape,
        Func<TResult, long> idOf,
        CancellationToken ct)
    {
        var found = await shape(TableInstancesByIdQuery(db, tableInstanceIds))
            .ToListAsync(ct).ConfigureAwait(false);

        var seen = found.Select(idOf).ToHashSet();
        var missing = tableInstanceIds.Where(id => !seen.Contains(id)).ToList();
        if (missing.Count == 0)
        {
            return found;
        }

        found.AddRange(await shape(db.TableInstances.AsNoTracking().Where(t => missing.Contains(t.Id)))
            .ToListAsync(ct).ConfigureAwait(false));

        return found;
    }

    /// <summary>Осиротілі рядки документа в одному періоді.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період — він же ключ партиції.</param>
    /// <returns>Незавершений запит ідентифікаторів рядків.</returns>
    /// <remarks>
    /// ⛔ <c>PeriodKey</c> був лише на <c>doc.TableRow</c>; підзапит екземплярів
    /// ішов за самим <c>DocumentId</c> — тобто по всіх партиціях
    /// <c>doc.TableInstance</c>. Помітити це важче, ніж у сусідніх запитах:
    /// зовнішня частина предикат має, і на перший погляд запит «з періодом».
    /// Саме тому перевіряється ЗГЕНЕРОВАНИЙ SQL, де видно кожну з двох таблиць
    /// окремо, а не текст джерела, де достатньо одного збігу слова
    /// <c>PeriodKeyValue</c>, щоб сторож по тексту заспокоївся.
    /// </remarks>
    public static IQueryable<long> OrphanedRowIdsQuery(
        EcrDbContext db, long documentId, PeriodKey periodKey)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value && r.IsOrphaned && !r.IsDeleted)
            .Join(TableInstancesQuery(db, documentId, periodKey),
                  r => r.TableInstanceId, t => t.Id, (r, _) => r.Id);
    }

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
