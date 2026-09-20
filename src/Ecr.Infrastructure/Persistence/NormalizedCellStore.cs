using System.Data;
using System.Globalization;
using System.Text;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Нормалізоване сховище комірок — **базова модель** (D-21).
/// </summary>
/// <remarks>
/// Бюджет: <c>ReadSliceAsync</c> — p95 &lt; 600 мс на 500×60,
/// <c>ApplyAsync</c> — p95 &lt; 150 мс на 100 комірок (tz/08 §8.2).
/// Ці числа і є критерієм гейта Етапу 0: якщо не проходить після індексів і
/// стиснення — вибірково по таблицях вмикається гібрид, а не глобально.
/// </remarks>
public sealed class NormalizedCellStore(EcrDbContext db) : ICellStore
{
    /// <summary>
    /// Скільки адрес іде в один запит там, де кожна несе власні параметри.
    /// </summary>
    /// <remarks>
    /// Обмеження не з голови: SQL Server приймає максимум 2100 параметрів на
    /// запит. Лишилися три такі місця — <see cref="ClaimRowsAsync"/> (2 на
    /// рядок), <see cref="DeleteAsync"/> (3 на комірку) і
    /// <see cref="TouchRowsAsync"/> (1 на рядок); 100 × 3 = 300, із запасом.
    ///
    /// ⚠ Раніше константа звалася <c>MergeChunkSize</c> і стосувалася
    /// насамперед <c>MERGE</c>. Після <c>WR-02</c> це неправда: батч значень
    /// їде ОДНИМ табличним параметром будь-якого розміру
    /// (<see cref="UpsertAsync"/>), і чанкування його не стосується. Ім'я
    /// перейменоване разом зі зміною, бо константа з брехливим іменем
    /// переживає будь-який коментар.
    /// </remarks>
    private const int ParameterChunkSize = 100;

    /// <summary>
    /// Форма <c>doc.CellValueTvp</c> — колонка в колонку зі скриптом
    /// <c>15-cell-tvp.sql</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>WR-02</c>. Типи мусять збігатися з типами стовпців точно, інакше
    /// SQL Server ставить неявну конверсію і план знову залежить від того, що
    /// приїхало — тобто зникає рівно те, заради чого табличний параметр і
    /// заводили. Числа ті самі, що в <see cref="NumericPrecision"/>,
    /// <see cref="NumericScale"/> і <see cref="DateScale"/>, і походять із
    /// <c>CellValueConfiguration.cs:40-42</c>.
    ///
    /// ⛔ <c>ValueString</c> — <c>nvarchar(max)</c>, хоча стовпець
    /// <c>nvarchar(1000)</c>. Причина та сама, що вела до <c>Size = -1</c> у
    /// параметрі рядка до <c>WR-02</c>, і вона важить більше за точність типу:
    /// <see cref="SqlMetaData"/> з
    /// оголошеною довжиною ОБРІЗАЄ довше значення на клієнті (його
    /// <c>Adjust</c> робить це мовчки), тобто <c>DAT-03</c> повернувся б через
    /// іншу двері. Межу тримає стовпець: задовге значення доїжджає до СУБД і
    /// падає помилкою усічення — гучно, як і має.
    ///
    /// ⚠ Масив статичний і спільний на всі виклики: <see cref="SqlMetaData"/>
    /// незмінний, а будувати дванадцять описів на кожен батч на шляху з
    /// бюджетом p95 150 мс — марна робота.
    /// </remarks>
    private static readonly SqlMetaData[] CellTvpShape =
    [
        new("PeriodKey", SqlDbType.Int),
        new("TableRowId", SqlDbType.BigInt),
        new("ColumnDefId", SqlDbType.Int),
        new("TableDefId", SqlDbType.Int),
        new("ValueString", SqlDbType.NVarChar, SqlMetaData.Max),
        new("ValueNumeric", SqlDbType.Decimal, NumericPrecision, NumericScale),
        new("ValueDate", SqlDbType.DateTime2, 0, DateScale),
        new("ValueBool", SqlDbType.Bit),
        new("ValueRegistryEntryId", SqlDbType.Int),
        new("ValueUnitId", SqlDbType.Int),
        new("IsCalculated", SqlDbType.Bit),
        new("IsEmpty", SqlDbType.Bit),
    ];

    /// <summary>Ім'я табличного типу в СУБД.</summary>
    private const string CellTvpTypeName = "doc.CellValueTvp";

    /// <summary>Точність <c>doc.CellValue.ValueNumeric</c> — <c>decimal(28,16)</c>.</summary>
    /// <remarks>
    /// ⛔ <c>WR-01</c>. Без явних <c>Precision</c>/<c>Scale</c>
    /// <c>Microsoft.Data.SqlClient</c> виводить їх ЗІ ЗНАЧЕННЯ: <c>1m</c> їде як
    /// <c>decimal(1,0)</c>, <c>1.25m</c> — як <c>decimal(3,2)</c>, <c>0.0001m</c>
    /// — як <c>decimal(5,4)</c>. Сигнатура запиту, а з нею й ключ кешу планів,
    /// змінюється від САМИХ ЧИСЕЛ, тож кожен батч із новим масштабом компілює
    /// власний план. Виміряно лінійкою <c>MS-01</c> (§3.2): 20 <c>PATCH</c> із
    /// різним масштабом → <b>21 план</b> <c>MERGE doc.CellValue</c> у кеші.
    ///
    /// ⚠ Числа взяті з <c>CellValueConfiguration.cs:41</c>
    /// (<c>HasPrecision(28, 16)</c>), а не «з голови»: параметр вужчий за
    /// стовпець округлював би значення на клієнті ще до відправки — рівно та
    /// вада класу <c>DAT-03</c>, тільки для чисел.
    /// </remarks>
    private const byte NumericPrecision = 28;

    /// <summary>
    /// Масштаб <c>doc.CellValue.ValueNumeric</c> — шістнадцять знаків
    /// (<c>D-148</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Мусить збігатися зі стовпцем і з <c>doc.CellValueTvp</c>
    /// (<c>15-cell-tvp.sql</c>) ТОЧНО. Менше значення тут не падає нічим:
    /// <c>SqlMetaData.Adjust</c> округлює на КЛІЄНТІ, тобто СУБД отримує вже
    /// огризок і чесно його зберігає. Саме тому всі три числа переводяться
    /// одним комітом, а не «по черзі, як зручно».
    /// </remarks>
    private const byte NumericScale = 16;

    /// <summary>Масштаб <c>doc.CellValue.ValueDate</c> — <c>datetime2(3)</c>.</summary>
    /// <remarks>
    /// ⚠ На відміну від <see cref="NumericScale"/>, у кеші планів це НЕ
    /// змінює нічого, і сказати інакше було б неправдою: виміряно окремим
    /// тестом (<c>WritePathPlanCacheTests</c>, «різняться лише датою») — двадцять
    /// записів із двадцятьма різними датами давали ОДИН план і до цієї правки.
    /// <c>SqlClient</c> для <c>DateTime2</c> без явного масштабу бере сталий
    /// типовий (7), а не виводить його зі значення.
    ///
    /// ⛔ Причина масштабу інша, і без нього <c>WR-06</c> тихо не працював би
    /// на живих даних. Стовпець <c>datetime2(3)</c>
    /// (<c>CellValueConfiguration.cs:42</c>) зберігає значення, округлене до
    /// мілісекунд, а параметр масштабу 7 везе ще й «хвіст» тиків
    /// (<c>DateTime.UtcNow</c> його завжди має). Порівняння «збережене проти
    /// надісланого» тоді не збігається НІКОЛИ, і умовний <c>MERGE</c>
    /// переписував би дату на кожному записі — тобто робив би рівно те, проти
    /// чого його ставили, і виглядав би при цьому справним.
    /// </remarks>
    private const byte DateScale = 3;

    /// <summary>
    /// Двійкове порівняння рядків у предикаті <c>WR-06</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього <c>WR-06</c> був би ТИХИМ ЗАГУБЛЕНИМ ОНОВЛЕННЯМ, а не
    /// оптимізацією. Порівняння в <c>EXCEPT</c> іде в колації стовпця, а вона
    /// тут <c>Latin1_General_100_CI_AS_SC</c> — <b>регістронечутлива</b>
    /// (перевірено запитом до розгорнутої бази, а не з документації). Тобто
    /// правка «київ» → «Київ» вважалася б «нічого не змінилося», комірка
    /// лишалася б зі старим значенням, а рядок аудиту стверджував би перехід,
    /// якого не сталося. Рівно той клас дефекту, проти якого писався
    /// <c>ClaimRowsAsync</c>, тільки зайшов би з іншого боку.
    ///
    /// ⚠ <c>DATALENGTH</c> поруч із колацією — не перестраховка: кінцеві
    /// пробіли ігноруються при порівнянні <c>nvarchar</c> у <b>будь-якій</b>
    /// колації, зокрема двійковій (перевірено: <c>N'a' = N'a '</c> під
    /// <c>Latin1_General_BIN2</c> — істина). Довжина в байтах ловить саме цю
    /// різницю і більше нічого.
    ///
    /// ⛔ Чому не <c>CAST(… AS varbinary(2000))</c>, що виглядало б простіше:
    /// значення довше за стовпець тихо обрізалося б у самому порівнянні, воно
    /// збіглося б зі старим, <c>UPDATE</c> не виконався б — і помилка усічення
    /// <c>DAT-03</c> (другий рубіж, <c>CellStoreTests</c>) перестала б
    /// виникати. Оптимізація, що знімає захист, коштує дорожче за те, що
    /// економить.
    /// </remarks>
    private const string ExactCollation = "Latin1_General_BIN2";

    /// <summary>
    /// Скільки рядків <c>doc.CellValue</c> змінив <c>MERGE</c> останнього
    /// <see cref="ApplyAsync"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Це ДІАГНОСТИКА, а не частина порту <c>ICellStore</c>: число потрібне
    /// рівно для того, щоб <c>WR-06</c> можна було довести прямо — повторний
    /// запис тих самих значень мусить дати <b>0</b>, а не «тест не впав».
    /// Значення — те саме <c>@@ROWCOUNT</c>, яке повертає
    /// <c>ExecuteNonQuery</c> бойового запиту, а не окремий підрахунок збоку:
    /// лічильник, що рахує НЕ ТЕ, що виконується, доказом не є.
    ///
    /// ⚠ Стан на екземплярі, тому число дійсне лише доти, доки цей самий
    /// екземпляр не викликали вдруге. <c>ApplyAsync</c> не реентерабельний і
    /// без цього.
    /// </remarks>
    public int LastUpsertRowsAffected { get; private set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct)
    {
        // ОДИН запит. TableInstance приєднаний не заради своїх полів, а заради
        // PeriodKey: він дає оптимізатору кореляцію, за якою відсікається
        // партиція. Без нього довелося б або читати період окремим запитом,
        // або сканувати всі 25 партицій.
        var rows = await (
            from instance in db.TableInstances.AsNoTracking()
            where instance.Id == tableInstanceId
            join row in db.TableRows.AsNoTracking()
                on new { P = instance.PeriodKeyValue, I = instance.Id }
                equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
            join cell in db.CellValues.AsNoTracking()
                on new { P = row.PeriodKeyValue, R = row.Id }
                equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
            where !row.IsDeleted
            select new
            {
                cell.PeriodKeyValue,
                cell.TableRowId,
                cell.ColumnDefId,
                cell.TableDefId,
                cell.ValueString,
                cell.ValueNumeric,
                cell.ValueDate,
                cell.ValueBool,
                cell.ValueRegistryEntryId,
                cell.ValueUnitId,
                cell.IsCalculated,
                cell.IsEmpty,
            }).ToListAsync(ct).ConfigureAwait(false);

        // Порожніх комірок у базі не існує взагалі — клієнт бере
        // ColumnDef.DefaultValue (ФВ-3.8). Явна порожнеча — це рядок із
        // IsEmpty = 1, і він повертається (R-B4).
        return [.. rows.Select(r => new CellRecord(
            new CellAddress(new PeriodKey(r.PeriodKeyValue), r.TableRowId, r.ColumnDefId),
            r.TableDefId,
            new CellValueData
            {
                ValueString = r.ValueString,
                ValueNumeric = r.ValueNumeric,
                ValueDate = r.ValueDate,
                ValueBool = r.ValueBool,
                ValueRegistryEntryId = r.ValueRegistryEntryId,
                ValueUnitId = r.ValueUnitId,
                IsCalculated = r.IsCalculated,
                IsEmpty = r.IsEmpty,
            }))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadSlicesAsync(
        IReadOnlyList<long> tableInstanceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableInstanceIds);
        if (tableInstanceIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<CellRecord>>();
        }

        var rows = await (
            from instance in db.TableInstances.AsNoTracking()
            where tableInstanceIds.Contains(instance.Id)
            join row in db.TableRows.AsNoTracking()
                on new { P = instance.PeriodKeyValue, I = instance.Id }
                equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
            join cell in db.CellValues.AsNoTracking()
                on new { P = row.PeriodKeyValue, R = row.Id }
                equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
            where !row.IsDeleted
            select new
            {
                instance.Id,
                cell.PeriodKeyValue,
                cell.TableRowId,
                cell.ColumnDefId,
                cell.TableDefId,
                cell.ValueString,
                cell.ValueNumeric,
                cell.ValueDate,
                cell.ValueBool,
                cell.ValueRegistryEntryId,
                cell.ValueUnitId,
                cell.IsCalculated,
                cell.IsEmpty,
            }).ToListAsync(ct).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Id)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<CellRecord> (g) => [.. g.Select(r => new CellRecord(
                    new CellAddress(new PeriodKey(r.PeriodKeyValue), r.TableRowId, r.ColumnDefId),
                    r.TableDefId,
                    new CellValueData
                    {
                        ValueString = r.ValueString,
                        ValueNumeric = r.ValueNumeric,
                        ValueDate = r.ValueDate,
                        ValueBool = r.ValueBool,
                        ValueRegistryEntryId = r.ValueRegistryEntryId,
                        ValueUnitId = r.ValueUnitId,
                        IsCalculated = r.IsCalculated,
                        IsEmpty = r.IsEmpty,
                    }))]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var result = new Dictionary<CellAddress, CellValueData>();
        if (addresses.Count == 0)
        {
            return result;
        }

        // Групування за періодом — не мікрооптимізація: кожна група це рівно
        // одна партиція, тож запит на групу читає один діапазон замість усіх.
        foreach (var group in addresses.GroupBy(a => a.PeriodKey.Value))
        {
            var periodKey = group.Key;
            var rowIds = group.Select(a => a.TableRowId).Distinct().ToArray();
            var columnIds = group.Select(a => a.ColumnDefId).Distinct().ToArray();

            var cells = await db.CellValues
                .AsNoTracking()
                .Where(c => c.PeriodKeyValue == periodKey
                            && rowIds.Contains(c.TableRowId)
                            && columnIds.Contains(c.ColumnDefId))
                .Select(c => new
                {
                    c.TableRowId,
                    c.ColumnDefId,
                    c.ValueString,
                    c.ValueNumeric,
                    c.ValueDate,
                    c.ValueBool,
                    c.ValueRegistryEntryId,
                    c.ValueUnitId,
                    c.IsCalculated,
                    c.IsEmpty,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // Декартів добуток rowIds × columnIds ширший за запитані адреси,
            // тому зайве відсіюється тут, а не в базі: звузити запит до точних
            // пар означало б або OR на кожну адресу, або тимчасову таблицю.
            var wanted = group.ToHashSet();

            foreach (var cell in cells)
            {
                var address = new CellAddress(new PeriodKey(periodKey), cell.TableRowId, cell.ColumnDefId);
                if (!wanted.Contains(address))
                {
                    continue;
                }

                result[address] = new CellValueData
                {
                    ValueString = cell.ValueString,
                    ValueNumeric = cell.ValueNumeric,
                    ValueDate = cell.ValueDate,
                    ValueBool = cell.ValueBool,
                    ValueRegistryEntryId = cell.ValueRegistryEntryId,
                    ValueUnitId = cell.ValueUnitId,
                    IsCalculated = cell.IsCalculated,
                    IsEmpty = cell.IsEmpty,
                };
            }
        }

        // Адреси, яких немає в базі, у словник не потрапляють: «немає комірки»
        // і «комірка явно порожня» — різні стани (R-B4), і склеювати їх тут
        // означало б втратити різницю назавжди.
        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Q-243. Раніше цей метод ЗАВЖДИ відкривав і комітив ВЛАСНУ
    /// транзакцію — навіть коли викликач (<c>PatchCellsHandler.PersistChangesAsync</c>)
    /// уже тримав ширшу транзакцію через <c>IUnitOfWork.BeginTransactionAsync</c>.
    /// Комірки комітились одразу, до того як «дотик» рядків, «дотик»
    /// документа й запис аудиту навіть почали виконуватись — збій між ними
    /// лишав змінені дані БЕЗ відповідного рядка аудиту. Тепер: якщо на
    /// <c>db</c> уже відкрита транзакція (ambient, <c>CurrentTransaction</c>),
    /// приєднуємось до НЕЇ і не комітимо — коміт/відкат належить тому, хто
    /// відкрив; якщо ні (виклик поза Q-243, наприклад
    /// <c>RecalculationService</c>, який власної транзакції не відкриває) —
    /// поведінка та сама, що й раніше: коротка власна транзакція, свій коміт.
    /// </remarks>
    public async Task ApplyAsync(CellChangeSet changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        LastUpsertRowsAffected = 0;

        var ambient = db.Database.CurrentTransaction;
        if (ambient is not null)
        {
            var joined = (SqlTransaction)ambient.GetDbTransaction();
            await ClaimRowsAsync(connection, joined, changes, ct).ConfigureAwait(false);
            await DeleteAsync(connection, joined, changes.Deletes, ct).ConfigureAwait(false);
            LastUpsertRowsAffected = await UpsertAsync(connection, joined, changes.Upserts, ct).ConfigureAwait(false);
            await TouchRowsAsync(connection, joined, changes, ct).ConfigureAwait(false);
            return;
        }

        // ⚠ Транзакція коротка навмисно. Під RCSI кожна відкрита транзакція
        // тримає версії рядків у tempdb, і довга транзакція роздуває version
        // store так, що страждає вся база, а не лише цей запит (D-29).
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await ClaimRowsAsync(connection, tx, changes, ct).ConfigureAwait(false);
        await DeleteAsync(connection, tx, changes.Deletes, ct).ConfigureAwait(false);
        LastUpsertRowsAffected = await UpsertAsync(connection, tx, changes.Upserts, ct).ConfigureAwait(false);
        await TouchRowsAsync(connection, tx, changes, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Захоплює рядки батчу під заявлену <c>RowVersion</c> — ОДНИМ запитом на
    /// чанк, ПЕРШОЮ дією транзакції.
    /// </summary>
    /// <remarks>
    /// ⛔ Тихе загублене оновлення (lost update) на сітці документа. До цього
    /// методу оптимістичне блокування трималося на порівнянні <b>в пам'яті
    /// C#</b> (<c>PatchCellsHandler.EnsureNoVersionConflicts</c>), яке
    /// виконувалось ПОЗА транзакцією запису, а самі записи не несли предиката
    /// на <c>RowVersion</c> взагалі: <c>MERGE doc.CellValue WITH (HOLDLOCK)</c>
    /// звіряв лише адресу комірки, <c>TouchRowsAsync</c> нижче — лише <c>Id</c>.
    /// Тобто між «прочитали версію» і «записали» лишалося вікно, у яке
    /// вміщався ВЕСЬ чужий батч: двоє аналітиків правлять ту саму комірку,
    /// обидва отримують <c>200</c>, другий мовчки затирає першого, а рядок
    /// аудиту стверджує перехід (<c>OldValue</c> → <c>NewValue</c>), якого
    /// ніколи не було. <c>HOLDLOCK</c> цього не закривав і закрити не міг: він
    /// стереже ДІАПАЗОН КЛЮЧІВ <c>doc.CellValue</c>, а змінюваний стан, за яким
    /// звіряються, лежить у <c>doc.TableRow.RowVersion</c>.
    ///
    /// ⚠ Звірка й запис — ОДИН <c>UPDATE</c>, а не «прочитати й порівняти»:
    /// повторне читання версії всередині транзакції дало б рівно те саме вікно,
    /// лише вужче. <c>UPDATE</c> бере блокування оновлення, читає ОСТАННЮ
    /// закомічену версію навіть під RCSI і в тій самій операції або захоплює
    /// рядок, або не знаходить його — третього стану немає.
    ///
    /// ⚠ Захоплення йде ПЕРШИМ, до <c>DELETE</c>/<c>MERGE</c>: на конфлікті
    /// транзакція відкочується, не написавши жодної комірки, і ексклюзивне
    /// блокування захоплених рядків тримається до кінця батчу — тож паралельний
    /// письменник у ті самі рядки чекає, а не проскакує повз.
    ///
    /// ⚠ Заразом це і є «дотик» рядка (<c>ModifiedAt</c>), тому
    /// <see cref="TouchRowsAsync"/> нижче захоплені рядки вже не чіпає: другий
    /// <c>UPDATE</c> по тих самих рядках коштував би зайвого проходу на шляху,
    /// чий бюджет — p95 150 мс на 100 комірок.
    ///
    /// ⚠ <c>OUTPUT … INTO</c>, а не голий <c>OUTPUT</c>: голий недоступний, щойно
    /// на таблиці з'явиться тригер або каскадний зовнішній ключ, і зламався б
    /// не тут, а в проді на першій такій зміні схеми.
    /// </remarks>
    /// <exception cref="ConcurrencyConflictException">
    /// Версія хоч одного рядка змінилася між читанням і записом —
    /// <c>ECR-CELL-0409</c>; <c>Details</c> несе
    /// <see cref="CellChangeSet.StaleRowIdsDetail"/> з переліком
    /// <c>TableRow.Id</c>.
    /// </exception>
    private static async Task ClaimRowsAsync(
        SqlConnection connection, SqlTransaction tx, CellChangeSet changes, CancellationToken ct)
    {
        var expected = changes.ExpectedRowVersions;
        if (expected is null || expected.Count == 0)
        {
            return;
        }

        var periodKey = PeriodKeyOf(changes);

        // ⚠ Порядок за Id — сталий порядок захоплення блокувань. Два батчі, що
        // перетинаються рядками, інакше беруть їх у порядку словника (тобто в
        // порядку ключів рядків клієнта) і складаються у взаємне блокування.
        var claims = expected.OrderBy(pair => pair.Key).ToArray();
        var stale = new List<long>();

        foreach (var chunk in claims.Chunk(ParameterChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;

            var values = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    values.Append(',');
                }

                values.Append(CultureInfo.InvariantCulture, $"(@i{i},@v{i})");
                AddInt64(command, $"@i{i}", chunk[i].Key);

                // ⚠ Саме binary(8): `rowversion` має рівно цю ширину, і без
                // явного типу провайдер вивів би `varbinary` іншої довжини —
                // порівняння тихо не збіглося б НІКОЛИ, тобто кожен запис
                // ставав би конфліктом.
                var version = command.Parameters.Add($"@v{i}", SqlDbType.Binary, 8);
                version.Value = Convert.FromBase64String(chunk[i].Value);
            }

            var periodFilter = periodKey is null ? string.Empty : "WHERE r.PeriodKey = @pk";
            if (periodKey is not null)
            {
                AddInt32(command, "@pk", periodKey.Value);
            }

            command.CommandText = $"""
                DECLARE @claimed TABLE (Id bigint PRIMARY KEY);
                UPDATE r SET ModifiedAt = SYSUTCDATETIME()
                OUTPUT inserted.Id INTO @claimed (Id)
                FROM doc.TableRow AS r
                INNER JOIN (VALUES {values}) AS source (Id, RowVersion)
                    ON r.Id = source.Id AND r.RowVersion = source.RowVersion
                {periodFilter};
                SELECT Id FROM @claimed;
                """;

            var claimed = new HashSet<long>();
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    claimed.Add(reader.GetInt64(0));
                }
            }

            // Не захопили — отже, версія вже інша (або рядка не стало). Обидва
            // випадки для автора батчу означають одне: те, від чого він
            // відштовхувався, більше не є правдою.
            stale.AddRange(chunk.Where(pair => !claimed.Contains(pair.Key)).Select(pair => pair.Key));
        }

        if (stale.Count > 0)
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.CellConflict,
                $"Версія рядка змінилася між читанням і записом: рядків — {stale.Count}.",
                new Dictionary<string, object?> { [CellChangeSet.StaleRowIdsDetail] = stale });
        }
    }

    /// <summary>
    /// Період батчу — ключ партиції; <c>null</c>, якщо батч не несе жодної
    /// адреси.
    /// </summary>
    /// <remarks>
    /// Усі комірки одного <c>TableInstance</c> лежать в одній партиції, і без
    /// <c>PeriodKey</c> у <c>WHERE</c> запит по <c>doc.TableRow</c> сканував би
    /// всі 25.
    /// </remarks>
    private static int? PeriodKeyOf(CellChangeSet changes)
        => changes.Upserts.Count > 0
            ? changes.Upserts[0].Address.PeriodKey.Value
            : changes.Deletes.Count > 0
                ? changes.Deletes[0].PeriodKey.Value
                : null;

    private static async Task DeleteAsync(
        SqlConnection connection, SqlTransaction tx, IReadOnlyList<CellAddress> deletes, CancellationToken ct)
    {
        if (deletes.Count == 0)
        {
            return;
        }

        // Видалення сутностей не завантажуємо: читати рядок, щоб його стерти,
        // означало б подвоїти кількість звернень на кожну комірку.
        foreach (var chunk in deletes.Chunk(ParameterChunkSize))
        {
            var sql = new StringBuilder("DELETE FROM doc.CellValue WHERE ");
            await using var command = connection.CreateCommand();
            command.Transaction = tx;

            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(" OR ");
                }

                sql.Append(CultureInfo.InvariantCulture,
                    $"(PeriodKey = @p{i} AND TableRowId = @r{i} AND ColumnDefId = @c{i})");

                AddInt32(command, $"@p{i}", chunk[i].PeriodKey.Value);
                AddInt64(command, $"@r{i}", chunk[i].TableRowId);
                AddInt32(command, $"@c{i}", chunk[i].ColumnDefId);
            }

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Вставляє нові комірки і оновлює наявні ОДНИМ <c>MERGE</c>.</summary>
    /// <returns>Скільки рядків <c>doc.CellValue</c> запис реально змінив.</returns>
    /// <remarks>
    /// ⛔ <c>WR-02</c>. Джерело — табличний параметр
    /// (<c>doc.CellValueTvp</c>), а не список <c>VALUES</c> із дванадцяти
    /// параметрів на комірку. Два наслідки, і обидва вимірювані:
    ///
    /// 1. <b>Одна команда на батч будь-якого розміру.</b> Було: чанк 100, тобто
    ///    вставка 500×60 = 30 000 комірок давала <b>300 послідовних</b>
    ///    <c>MERGE</c> в одній транзакції — 300 походів до сервера, і всі під
    ///    тими самими замками. Стало: один.
    /// 2. <b>Один план назавжди.</b> Текст запиту більше не містить списку
    ///    <c>VALUES</c>, тобто не залежить від КІЛЬКОСТІ рядків. Лінійка
    ///    <c>MS-01</c> §3.2 після <c>WR-01</c> бачила <b>сім</b> планів
    ///    <c>MERGE doc.CellValue</c> у кеші — рівно по одному на розмір батчу;
    ///    <c>WR-01</c> прибрав залежність від ЗНАЧЕНЬ, цей рядок — від
    ///    РОЗМІРУ.
    ///
    /// ⚠ Заразом зникає межа 2100 параметрів на запит: параметр тут рівно
    /// один, скільки б комірок у ньому не їхало. Саме вона й змушувала різати
    /// батч на чанки (<see cref="ParameterChunkSize"/>), а не бажання
    /// економити.
    ///
    /// ⚠ <c>HOLDLOCK</c> лишається НЕЗМІННИМ. Зняти його — окремий рядок плану
    /// (<c>WR-07</c>), і директива прямо вимагає спершу зняти фактичний план і
    /// поміряти очікування на замках, а потім написати тест гонки. Тиха зміна
    /// семантики конкурентного upsert «заразом, поки правимо файл» — рівно те,
    /// чого там заборонено робити.
    ///
    /// <c>MERGE</c>, а не «прочитати — порівняти — записати»: другий варіант
    /// дає гонку між читанням і записом, яку під RCSI не видно на тестах і
    /// добре видно в останній день періоду.
    ///
    /// ⛔ <c>WR-06</c>. <c>WHEN MATCHED</c> був БЕЗУМОВНИЙ, тобто рядок, у
    /// якому нічого не змінилося, однаково перезаписувався — разом із
    /// <c>TableDefId</c>, <c>ValueRegistryEntryId</c> і <c>ValueUnitId</c>, а
    /// на кожен із трьох висить зовнішній ключ. Ціна не в самому <c>UPDATE</c>,
    /// а в тому, що тягнеться за ним: три перевірки FK, запис у журнал
    /// транзакцій і оновлення сторінки на НАЙГАРЯЧІШІЙ таблиці системи — і все
    /// це заради того, щоб записати те саме, що вже лежить.
    ///
    /// ⚠ Це не рідкісний випадок: автозбереження сітки шле весь зріз, а не
    /// лише змінені комірки, тож більшість рядків батчу — саме такі.
    ///
    /// ⚠ Порівняння через <c>EXISTS (… EXCEPT …)</c>, а не через ланцюг
    /// <c>OR</c> із <c>IS NULL</c>: <c>EXCEPT</c> трактує <c>NULL = NULL</c> як
    /// збіг, і саме це тут потрібно (порожня комірка, яку переписують такою ж
    /// порожньою, не є зміною). Ланцюг на дев'ять стовпців із подвійною
    /// перевіркою на <c>NULL</c> був би втричі довшим і помилявся б у першій же
    /// правці.
    ///
    /// ⚠ Умова стоїть на <c>WHEN MATCHED</c>, а не на <c>USING</c>: рядок, що
    /// не збігся, має бути ВСТАВЛЕНИЙ, і відсікати його до <c>ON</c> означало б
    /// втратити вставки.
    /// </remarks>
    private static async Task<int> UpsertAsync(
        SqlConnection connection, SqlTransaction tx, IReadOnlyList<CellRecord> upserts, CancellationToken ct)
    {
        if (upserts.Count == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = tx;

        var cells = command.Parameters.Add("@cells", SqlDbType.Structured);
        cells.TypeName = CellTvpTypeName;
        cells.Value = ToCellRecords(upserts);

        command.CommandText = $"""
                MERGE doc.CellValue WITH (HOLDLOCK) AS target
                USING @cells AS source
                ON  target.PeriodKey   = source.PeriodKey
                AND target.TableRowId  = source.TableRowId
                AND target.ColumnDefId = source.ColumnDefId
                WHEN MATCHED AND EXISTS (
                        SELECT target.TableDefId,
                               target.ValueString COLLATE {ExactCollation}, DATALENGTH(target.ValueString),
                               target.ValueNumeric, target.ValueDate, target.ValueBool,
                               target.ValueRegistryEntryId, target.ValueUnitId,
                               target.IsCalculated, target.IsEmpty
                        EXCEPT
                        SELECT source.TableDefId,
                               source.ValueString COLLATE {ExactCollation}, DATALENGTH(source.ValueString),
                               source.ValueNumeric, source.ValueDate, source.ValueBool,
                               source.ValueRegistryEntryId, source.ValueUnitId,
                               source.IsCalculated, source.IsEmpty)
                THEN UPDATE SET
                    TableDefId = source.TableDefId,
                    ValueString = source.ValueString,
                    ValueNumeric = source.ValueNumeric,
                    ValueDate = source.ValueDate,
                    ValueBool = source.ValueBool,
                    ValueRegistryEntryId = source.ValueRegistryEntryId,
                    ValueUnitId = source.ValueUnitId,
                    IsCalculated = source.IsCalculated,
                    IsEmpty = source.IsEmpty
                WHEN NOT MATCHED THEN INSERT
                    (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
                     ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
                    VALUES (source.PeriodKey, source.TableRowId, source.ColumnDefId, source.TableDefId,
                            source.ValueString, source.ValueNumeric, source.ValueDate, source.ValueBool,
                            source.ValueRegistryEntryId, source.ValueUnitId, source.IsCalculated, source.IsEmpty);
                """;

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Перекладає батч у рядки табличного параметра.
    /// </summary>
    /// <param name="upserts">Комірки батчу; порожнім не буває.</param>
    /// <returns>Лінива послідовність рядків <c>doc.CellValueTvp</c>.</returns>
    /// <remarks>
    /// ⚠ Один <see cref="SqlDataRecord"/> на всю послідовність — це
    /// документована форма, а не економія на сірниках: провайдер читає рядок
    /// ПОВНІСТЮ, перш ніж попросити наступний, тож переповнювати той самий
    /// буфер безпечно, а нових об'єктів на 30 000 комірок не виникає взагалі.
    ///
    /// ⛔ Саме тому КОЖНА колонка присвоюється на КОЖНОМУ рядку, навіть коли
    /// значення <c>null</c>. Пропустити <c>SetDBNull</c> означало б лишити в
    /// буфері значення ПОПЕРЕДНЬОЇ комірки — тобто мовчки записати в порожню
    /// комірку чуже число. Найгірший різновид дефекту: тихий, залежний від
    /// порядку і невидимий у тесті на одну комірку.
    ///
    /// ⚠ Послідовність лінива, і <c>SqlClient</c> перебирає її вже під час
    /// відправки, всередині транзакції. Тому тут не можна ні читати з бази, ні
    /// кидати в середині: виняток застане команду напіввідправленою.
    /// Обчислень тут і немає — лише перекладання полів.
    /// </remarks>
    private static IEnumerable<SqlDataRecord> ToCellRecords(IReadOnlyList<CellRecord> upserts)
    {
        var row = new SqlDataRecord(CellTvpShape);

        foreach (var record in upserts)
        {
            var value = record.Value;

            row.SetInt32(0, record.Address.PeriodKey.Value);
            row.SetInt64(1, record.Address.TableRowId);
            row.SetInt32(2, record.Address.ColumnDefId);
            row.SetInt32(3, record.TableDefId);
            SetNullableString(row, 4, value.ValueString);
            SetNullable(row, 5, value.ValueNumeric, static (r, i, v) => r.SetDecimal(i, v));
            SetNullable(row, 6, value.ValueDate, static (r, i, v) => r.SetDateTime(i, v));
            SetNullable(row, 7, value.ValueBool, static (r, i, v) => r.SetBoolean(i, v));

            // ⛔ Звужено до int навмисно: фізична колонка лишається `int`
            // (RegistryEntry.Id — long у CLR, конвертований у int лише для
            // зберігання, RegistryEntryConfiguration.cs:41). Сирий шлях повз
            // конвеєр EF `HasConversion<int?>()` потребує того самого
            // звуження, що й BulkCellLoader.
            SetNullable(row, 8, (int?)value.ValueRegistryEntryId, static (r, i, v) => r.SetInt32(i, v));
            SetNullable(row, 9, value.ValueUnitId, static (r, i, v) => r.SetInt32(i, v));
            row.SetBoolean(10, value.IsCalculated);
            row.SetBoolean(11, value.IsEmpty);

            yield return row;
        }
    }

    /// <summary>Значеннєве поле або <c>NULL</c> — без забутого <c>SetDBNull</c>.</summary>
    /// <typeparam name="T">Тип значення.</typeparam>
    /// <param name="row">Рядок табличного параметра.</param>
    /// <param name="ordinal">Номер колонки.</param>
    /// <param name="value">Значення або <c>null</c>.</param>
    /// <param name="set">Як покласти НЕпорожнє значення.</param>
    private static void SetNullable<T>(SqlDataRecord row, int ordinal, T? value, Action<SqlDataRecord, int, T> set)
        where T : struct
    {
        if (value.HasValue)
        {
            set(row, ordinal, value.Value);
        }
        else
        {
            row.SetDBNull(ordinal);
        }
    }

    /// <summary>Рядкове поле або <c>NULL</c>.</summary>
    /// <param name="row">Рядок табличного параметра.</param>
    /// <param name="ordinal">Номер колонки.</param>
    /// <param name="value">Значення або <c>null</c>.</param>
    /// <remarks>
    /// ⚠ Довжина НЕ перевіряється і не обрізається: колонка типу оголошена
    /// <c>nvarchar(max)</c> (<see cref="CellTvpShape"/>), тож задовге значення
    /// доїжджає до СУБД цілим і впирається у <c>nvarchar(1000)</c> самого
    /// стовпця — помилкою, а не огризком. Це другий рубіж <c>DAT-03</c>;
    /// перший — <c>ColumnDef.ValidateValue</c>.
    /// </remarks>
    private static void SetNullableString(SqlDataRecord row, int ordinal, string? value)
    {
        if (value is null)
        {
            row.SetDBNull(ordinal);
        }
        else
        {
            row.SetString(ordinal, value);
        }
    }

    /// <summary>Піднімає <c>ModifiedAt</c> у зачеплених рядках.</summary>
    /// <remarks>
    /// ⚠ ОБОВ'ЯЗКОВО. <c>RowVersion</c> у <c>doc.TableRow</c> росте від
    /// <c>UPDATE</c> самого рядка, а не від запису в <c>doc.CellValue</c>.
    /// Без цього «дотику» оптимістичне блокування тихо не працює: два
    /// користувачі правлять одні й ті самі комірки, обидва бачать незмінений
    /// <c>RowVersion</c>, і другий перезаписує першого (B04 §2.4).
    ///
    /// ⚠ Рядки, вже захоплені <see cref="ClaimRowsAsync"/>, сюди не
    /// потрапляють: той <c>UPDATE</c> підняв їм <c>ModifiedAt</c> ЗАРАЗОМ зі
    /// звіркою версії, і повторити його означало б зайвий прохід по тих самих
    /// рядках на шляху з бюджетом p95 150 мс на 100 комірок. Лишаються ті, чиєї
    /// версії ніхто не заявляв — насамперед ЩОЙНО СТВОРЕНІ рядки батчу: у них
    /// версії від чого відштовхуватись не було, а «дотик» потрібен так само.
    /// </remarks>
    private static async Task TouchRowsAsync(
        SqlConnection connection, SqlTransaction tx, CellChangeSet changes, CancellationToken ct)
    {
        var claimed = changes.ExpectedRowVersions;
        IReadOnlyList<long> pending = claimed is null || claimed.Count == 0
            ? changes.TouchedRowIds
            : [.. changes.TouchedRowIds.Where(id => !claimed.ContainsKey(id))];

        if (pending.Count == 0)
        {
            return;
        }

        var periodKey = PeriodKeyOf(changes);

        foreach (var chunk in pending.Chunk(ParameterChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;

            var ids = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    ids.Append(',');
                }

                ids.Append(CultureInfo.InvariantCulture, $"@i{i}");
                AddInt64(command, $"@i{i}", chunk[i]);
            }

            var periodFilter = periodKey is null ? string.Empty : "PeriodKey = @pk AND ";
            if (periodKey is not null)
            {
                AddInt32(command, "@pk", periodKey.Value);
            }

            command.CommandText =
                $"UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE {periodFilter}Id IN ({ids});";

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary><c>int</c> зі сталою сигнатурою.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення.</param>
    /// <remarks>
    /// ⚠ <c>AddWithValue</c> для <c>int</c> дає <b>ту саму</b> сигнатуру, тож
    /// заміна тут нічого не прискорює. Вона потрібна для іншого: у цьому файлі
    /// не лишається жодного <c>AddWithValue</c>, і «додав параметр по-старому»
    /// стає видно очима, а не через замір кешу планів через півроку.
    /// </remarks>
    private static void AddInt32(SqlCommand command, string name, int value)
        => command.Parameters.Add(name, SqlDbType.Int).Value = value;

    /// <summary><c>bigint</c> зі сталою сигнатурою.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення.</param>
    private static void AddInt64(SqlCommand command, string name, long value)
        => command.Parameters.Add(name, SqlDbType.BigInt).Value = value;

}
