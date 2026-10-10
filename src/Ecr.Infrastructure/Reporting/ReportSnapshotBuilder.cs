using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Reporting;

/// <summary>
/// Будує незмінні зрізи <c>rpt.*</c> для регламентної звітності.
/// </summary>
/// <remarks>
/// Межа з SSRS проходить саме тут: звіти лишаються в SSRS (<c>D-52</c>), а ми
/// віддаємо стабільний контракт даних. <c>rpt.*</c> — зріз **без логіки**:
/// агрегації робить цей сервіс, вʼюха лише проєктує (ФВ-0.3).
/// <para>
/// ✎ <c>D-52a</c>: поруч із SSRS зрізи читає сам застосунок, а колонки зрізу
/// задає опис версії. Для <c>rpt.*</c> це адитивно (<c>D-53</c>): опис із тими
/// самими п'ятьма колонками дає той самий вміст і ту саму суму.
/// </para>
/// </remarks>
/// <param name="db">Контекст бази.</param>
/// <param name="clock">Годинник побудови.</param>
/// <param name="memory">
/// Спільний кеш процесу: у ньому лежить зріз, уже розкладений макетом
/// (<c>R8</c>), щоб сторінка не перечитувала весь зріз (P4).
/// </param>
public sealed class ReportSnapshotBuilder(EcrDbContext db, IClock clock, IMemoryCache memory) : IReportSnapshotBuilder
{
    /// <summary>Стеля рядків одного зрізу.</summary>
    /// <remarks>
    /// Річний звіт великого проєкту — десятки тисяч рядків. Межа існує не
    /// тому, що більше не буває, а тому, що без неї помилка в правилах відбору
    /// виглядала б як повільність, а не як помилка.
    /// <para>
    /// ⛔ AN-120 / L1-01: стеля — на ВИХОДІ правил відбору (<c>R5</c>), а не на
    /// джерелі, і перевищення — ВІДМОВА (<c>ECR-RPT-0422</c>), а не обрізання.
    /// Раніше джерело мовчки зрізалося <c>Take(MaxRows)</c> до правил: зріз
    /// отримував <c>Complete</c>, суму й <c>IsCurrent</c> на неповних даних, а
    /// звірка «збігалася», бо рахувала ті самі збережені рядки.
    /// </para>
    /// </remarks>
    private const int MaxRows = 200_000;

    /// <summary>Стеля рядків зрізу для ЦЬОГО будівника; за замовчуванням — <see cref="MaxRows"/>.</summary>
    /// <remarks>
    /// Окремою властивістю лише заради тестів (прийом <c>PointCeilingPerField</c>):
    /// довести «відмова, а не обрізання» на 200 001 рядку означало б годину
    /// наповнення бази. Контейнер її не задає.
    /// </remarks>
    public int RowCeiling { get; init; } = MaxRows;

    /// <summary>Бюджет кешу розкладених зрізів; за замовчуванням — <see cref="MaxCachedCells"/>.</summary>
    /// <remarks>Окремою властивістю лише заради тестів зрізу, що в бюджет не влазить.</remarks>
    public int LaidOutCacheBudget { get; init; } = MaxCachedCells;

    /// <inheritdoc />
    public async Task<long> BuildAsync(
        int reportVersionId,
        int projectId,
        PeriodKey? periodKey,
        string? parametersJson,
        CancellationToken ct)
    {
        var version = await db.ReportVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == reportVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Версії звіту {reportVersionId} не існує.");

        // ⛔ D-52a: колонки зрізу задає ОПИС. Розбирається ДО створення зрізу:
        // відмова після нього лишила б у `rpt.ReportSnapshot` порожній рядок.
        var layout = LayoutOf(version);

        // ⛔ R5: правила застосовуються до рядка джерела ДО запису й до суми — і ДО
        // створення зрізу: помилка правила на рядку не лишає порожнього зрізу.
        var rowRules = ReportRowRules.Parse(version.RulesJson, [.. layout.Select(c => c.Code)]);

        // ⛔ R6: значення параметрів зводяться з оголошеннями ВДРУГЕ. Перший раз
        // це зробив обробник запиту (щоб відмовити 422 одразу), але задача може
        // прийти й не звідти — з розкладу або з черги, пережившої переїзд.
        var parameters = ReportParameters.Bind(rowRules.Parameters, parametersJson);

        // ⛔ R6-X7 / X7-04: «побудовано станом на» і прогін походження — моменти ДО
        // читання джерела, а не після. Агрегація читає джерело потоком (AN-120) і на
        // великому проєкті триває десятки секунд; прогін, що став актуальним за цей
        // час (`FinishedAt` усередині вікна), мусить старити зріз (ФВ-10.5): його чисел
        // у зрізі може не бути. Раніше `BuiltAt` ставився ПІСЛЯ агрегації — такий
        // прогін зріз не старив, а `CalculationRunId` вказував саме на нього, тобто
        // походження чисел було хибним. Хибна застарілість (прогін, що закінчився під
        // час агрегації й таки потрапив у зріз) — безпечний бік: зріз перебудують.
        var builtAt = clock.UtcNow;
        var calculationRunId = await CurrentRunAsync(projectId, periodKey, ct).ConfigureAwait(false);

        // ⛔ R6-X7 / X7-03: поданий (заморожений) поточний зріз не витісняється, доки
        // його дані не повернуто в роботу. Перевірка тут — лише щоб не читати джерело
        // задарма; вирішальна — повторна, під замком слоту (нижче).
        var frozenBefore = await FrozenCurrentIdsAsync(version.ReportDefId, projectId, periodKey, ct)
            .ConfigureAwait(false);
        if (await FreshFrozenAsync(frozenBefore, projectId, periodKey, dataStatus: null, ct).ConfigureAwait(false)
            is { } frozenEarly)
        {
            throw FrozenRefusal(frozenEarly, projectId, periodKey);
        }

        var cells = await AggregateAsync(layout, rowRules, parameters.Values, projectId, periodKey, ct)
            .ConfigureAwait(false);

        // ⚠ Статус УСПАДКОВУЄТЬСЯ від даних (D-65). Окреме поле «статус звіту»
        // стало б другим джерелом істини і рано чи пізно показало б регулятору
        // Approved на чернетці.
        //
        // ⛔ R6-X1 / X1-01: тут лише заглушка `Draft`. Справжній статус
        // рахується НАПРИКІНЦІ, під замком слоту і безпосередньо перед
        // перемиканням `IsCurrent` (див. нижче). Порахований тут, він
        // застарівав на весь час запису рядків: перехід, що закомітився в цьому
        // вікні, оновлював лише СТАРИЙ поточний зріз, а новий ставав поточним
        // зі статусом до переходу.
        var snapshot = new ReportSnapshot(
            reportVersionId, projectId, periodKey?.Value, SnapshotStatus.Draft, builtAt, builtByUserId: null);

        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Y1-04 (аудит R11): зріз уже вставлено (непоточний, з рядками). Будь-яка відмова ДО того, як
        // його зроблено поточним (замок слоту не взято за `SlotLockTimeout` — 409 «зайнято»; збій запису
        // рядків; скасування), лишала б «зріз-сироту» з до 200 000 рядків до нічної ретенції (вона обходить
        // зрізи молодші за `BuildGrace`, 6 год). Тепер сирота прибирається одразу, а відмова йде далі як є.
        long? refusedBy = null;
        try
        {
            // Агрегації виконуються ТУТ, одним набором запитів. Вʼюха rpt.v_*
            // нічого не рахує (ФВ-0.3): індексована вʼюха з обчисленнями не
            // перебудовується інкрементно і зупиняє запис у джерело.
            var rows = cells.ConvertAll(c => Cell(snapshot.Id, c.RowNo, c.Code, c.Text, c.Number));

            // ⛔ Z5-03 / L1-05 (аудит R11): рядки — ПОРЦІЯМИ з відчепленням від трекера, а не одним
            // `AddRange` на весь зріз. До 200 000 × C відстежуваних сутностей (≈ 1 КБ трекера кожна) в
            // одному `SaveChanges` — гігабайти в процесі, де працює задача. Порція зберігається й
            // відчіплюється одразу: трекер тримає не більше `RowSaveChunkSize` комірок. Зріз іще не поточний
            // (`IsCurrent = 0`), тож проміжний стан порцій регуляторній вʼюсі не видно, а збій посеред
            // запису прибирає зріз (`DiscardAsync` нижче, Y1-04).
            foreach (var chunk in rows.Chunk(Math.Max(1, RowSaveChunkSize)))
            {
                db.ReportRows.AddRange(chunk);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                foreach (var entry in db.ChangeTracker.Entries<ReportRow>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }

            snapshot.Complete(
                rows.Count,
                ComputeHash(rows),

                // Прогін, з якого взято числа: без нього неможливо сказати, на
                // чому стоїть значення у звіті. ⚠ Прочитаний ДО агрегації (X7-04).
                calculationRunId,

                // ⚠ Записуються ВИКОРИСТАНІ значення, а не надіслані: замовчування
                // вже підставлені. Інакше зріз, побудований без жодного параметра,
                // не давав би відповіді на питання «з чим його рахували».
                parameters.Json ?? parametersJson);

            // Сума щойно порахована `ComputeHash`, тобто поточним форматом: формат
            // зберігається одразу, а не визначається потім перерахунком.
            snapshot.RecordHashFormat(VerifyReportSnapshotHandler.FormatCurrent);

            // ⚠ Рядки — окремим збереженням (порціями, вище), ПОЗА замком слоту: запис до
            // 200 000 рядків триває секунди, і тримати весь цей час робочий процес
            // проєкту за період означало б відмови «зайнято» на поданні. Зріз ще
            // не поточний, тож регуляторна вʼюха його не бачить. Тут — підсумок зрізу.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // ⛔ R6-X1 / X1-01: статус і перемикання — ОДНІЄЮ короткою транзакцією
            // під тим самим замком слоту, який бере робочий процес перед пошуком
            // поточних зрізів (`ReportSnapshotSync`). Перехід або закомітився
            // раніше — і тоді запит статусу його бачить, — або чекає замка і вже
            // знаходить НОВИЙ зріз поточним. Третього варіанта, у якому перехід
            // оновлює лише старий зріз, а новий стає поточним із застарілим
            // статусом, більше немає. `UPDLOCK` на рядку зрізу (W1-01) цього не
            // закривав: нового зрізу серед заблокованих ще не було.
            await new UnitOfWork(db, clock).ExecuteInTransactionAsync(
                async innerCt =>
                {
                    refusedBy = null;
                    await LockSlotAsync(projectId, periodKey?.Value, innerCt).ConfigureAwait(false);

                    var status = await StatusOfDataAsync(projectId, periodKey, innerCt).ConfigureAwait(false);

                    // ⛔ R6-X7 / X7-03: повторна перевірка «поточний зріз ключа поданий» —
                    // під замком слоту, який бере й робочий процес перед заморожуванням
                    // (`ReportSnapshotSync`). Побудова, що почалася до подання останнього
                    // аркуша, тут уже бачить заморожений зріз і відмовляє, а не знімає з
                    // нього поточність. Новий зріз при відмові ВИДАЛЯЄТЬСЯ разом із рядками
                    // (вони збережені вище, поза замком): інакше лишився б непоточний
                    // «зріз-сирота», якого ніхто не просив.
                    var frozen = await FrozenCurrentIdsAsync(version.ReportDefId, projectId, periodKey, innerCt)
                        .ConfigureAwait(false);
                    if (await FreshFrozenAsync(frozen, projectId, periodKey, status, innerCt).ConfigureAwait(false)
                        is { } frozenNow)
                    {
                        await DiscardAsync(snapshot.Id, innerCt).ConfigureAwait(false);
                        refusedBy = frozenNow;
                        return;
                    }

                    // ⛔ R6-X7 / X7-01: зріз, застарілий уже від народження (прогін перемкнувся,
                    // поки читалося джерело, X7-04), статусу даних не успадковує — те саме
                    // правило, що в `RefreshStatusAsync`: старі числа не йдуть у `rpt.v_*` як
                    // затверджені чи подані.
                    if (status != SnapshotStatus.Draft
                        && await ReportSnapshotStaleness.IsStaleAsync(db, projectId, periodKey?.Value, builtAt, innerCt)
                            .ConfigureAwait(false))
                    {
                        status = SnapshotStatus.Draft;
                    }

                    snapshot.RefreshStatus(status);

                    await SwitchCurrentAsync(snapshot, version.ReportDefId, innerCt).ConfigureAwait(false);
                    await db.SaveChangesAsync(innerCt).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await DiscardOrphanAsync(snapshot.Id).ConfigureAwait(false);
            throw;
        }

        if (refusedBy is { } refused)
        {
            // Видалене в базі не має лишатися в трекері: той самий контекст може
            // зберігати далі, і EF спробував би оновити рядки, яких уже немає.
            foreach (var entry in db.ChangeTracker.Entries<ReportRow>()
                         .Where(e => e.Entity.SnapshotId == snapshot.Id)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }

            db.Entry(snapshot).State = EntityState.Detached;
            throw FrozenRefusal(refused, projectId, periodKey);
        }

        return snapshot.Id;
    }

    /// <summary>
    /// Поточні ПОДАНІ зрізи опису звіту за проєкт і період.
    /// </summary>
    private Task<List<long>> FrozenCurrentIdsAsync(
        int reportDefId, int projectId, PeriodKey? periodKey, CancellationToken ct)
    {
        int? key = periodKey?.Value;

        return db.ReportSnapshots
            .AsNoTracking()
            .Where(s => s.IsCurrent
                        && s.Status == SnapshotStatus.Submitted
                        && s.ProjectId == projectId
                        && s.PeriodKey == key
                        && db.ReportVersions.Any(v => v.Id == s.ReportVersionId && v.ReportDefId == reportDefId))
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .Take(MaxCurrentSnapshots)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Поданий поточний зріз, який нова побудова не має права витіснити; <c>null</c> — такого немає.
    /// </summary>
    /// <param name="frozenIds">Поточні подані зрізи ключа.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="dataStatus">Статус даних, якщо вже пораховано; <c>null</c> — порахувати.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ R6-X7 / X7-03. Поданий зріз — доказ того, що бачив регулятор (ФВ-9.17), і
    /// <c>rpt.v_*</c> віддає саме поточний. Нова побудова з іншими параметрами чи за
    /// іншою версією опису (X7-02) підмінила б у вʼюсі подані числа іншими з тією
    /// самою позначкою «Submitted» (новий зріз за поданим періодом народжується
    /// <c>Submitted</c> зі стану даних). Із тими самими параметрами числа збіглися б
    /// (після подання перерахунок заборонено), тож відмова нічого законного не відбирає.
    /// <para>
    /// ⚠ Дозволено, коли (а) дані повернуто в роботу (<c>Reopen</c>/відкликання → статус
    /// даних <c>Draft</c>) — повторне подання дає НОВИЙ зріз; (б) поданий зріз
    /// застарілий (ФВ-10.5: після повернення в роботу був перерахунок) — його числа вже
    /// не ті, що в системі, і нова побудова — саме те, що потрібно.
    /// </para>
    /// </remarks>
    private async Task<long?> FreshFrozenAsync(
        List<long> frozenIds, int projectId, PeriodKey? periodKey, SnapshotStatus? dataStatus, CancellationToken ct)
    {
        if (frozenIds.Count == 0)
        {
            return null;
        }

        var stale = await ReportSnapshotStaleness.StaleSnapshotIds(db)
            .Where(id => frozenIds.Contains(id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var fresh = frozenIds.Except(stale).ToList();
        if (fresh.Count == 0)
        {
            return null;
        }

        var status = dataStatus ?? await StatusOfDataAsync(projectId, periodKey, ct).ConfigureAwait(false);
        return status == SnapshotStatus.Draft ? null : fresh[0];
    }

    /// <summary>Видаляє щойно записаний (ще не поточний) зріз разом із рядками.</summary>
    /// <remarks>⚠ Рядки ПЕРШИМИ: <c>FK_RepRow_Snap</c> не каскадний (як у <c>ReportRetentionJob</c>).</remarks>
    private async Task DiscardAsync(long snapshotId, CancellationToken ct)
    {
        await db.ReportRows.Where(r => r.SnapshotId == snapshotId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ReportSnapshots.Where(s => s.Id == snapshotId && !s.IsCurrent).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private const int DefaultRowSaveChunk = 10_000;

    /// <summary>Скільки комірок зрізу зберігається одним <c>SaveChanges</c> (трекер тримає не більше).</summary>
    /// <remarks>Окремою властивістю лише заради тестів (прийом <c>RowCeiling</c>): довести порціювання на 10 000+ комірок — зайве наповнення бази.</remarks>
    public int RowSaveChunkSize { get; init; } = DefaultRowSaveChunk;

    /// <summary>
    /// Прибирає недобудований зріз після відмови: рядки, потім сам зріз (лише НЕ поточний).
    /// </summary>
    /// <remarks>
    /// ⚠ Найкраща спроба: власний збій прибирання не маскує справжню причину відмови (її кидає
    /// викликач далі), а недоприбране дібере <c>ReportRetentionJob</c>. Токен — <c>None</c>:
    /// скасування запиту не має лишати сироту. Стан трекера не чіпається — контекст після відмови
    /// далі не використовується (задача завершується).
    /// </remarks>
    private async Task DiscardOrphanAsync(long snapshotId)
    {
        try
        {
            await DiscardAsync(snapshotId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Див. ремарку: причину відмови не підміняємо; недоприбране дібере ретенція.
        }
    }

    /// <summary>Відмова побудови: поточний зріз ключа поданий (ФВ-9.17).</summary>
    /// <remarks>
    /// ⛔ R7-Y8 / Y8-01: <see cref="DomainException"/> з <see cref="ErrorCodes.ReportImmutable"/>
    /// (<c>ECR-RPT-0409</c>, «зріз подано»), а НЕ <see cref="InvalidOperationException"/>.
    /// Побудова йде у фоновій задачі, а це вердикт про вже збережений стан: з
    /// <see cref="InvalidOperationException"/> <c>JobRetryPolicy.IsWorthRetrying</c> ішов у
    /// гілку «збій дороги» — три повтори (210 с «виконується»), а потім <c>ECR-SYS-0500</c>
    /// («Internal error») на клієнті замість причини й шляху (Reopen).
    /// <para>
    /// ⛔ R7-Y7 / Y7-01: та сама відмова, що й синхронна в обробнику запиту
    /// (<see cref="BuildReportSnapshotHandler.FrozenRefusal"/>) — одне джерело тексту й ключа.
    /// </para>
    /// </remarks>
    internal static DomainException FrozenRefusal(long frozenId, int projectId, PeriodKey? periodKey)
        => BuildReportSnapshotHandler.FrozenRefusal(frozenId, projectId, periodKey?.Value);

    /// <inheritdoc />
    public async Task<long?> FindFreshFrozenCurrentAsync(
        int reportVersionId, int projectId, PeriodKey? periodKey, CancellationToken ct)
    {
        var reportDefId = await db.ReportVersions
            .AsNoTracking()
            .Where(v => v.Id == reportVersionId)
            .Select(v => (int?)v.ReportDefId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (reportDefId is not { } defId)
        {
            return null;
        }

        var frozen = await FrozenCurrentIdsAsync(defId, projectId, periodKey, ct).ConfigureAwait(false);
        return await FreshFrozenAsync(frozen, projectId, periodKey, dataStatus: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Замок — на проєкт × період, без версії звіту: робочий процес
    /// проводить перехід у зрізи ВСІХ версій одним викликом. Річний зріз
    /// (<paramref name="periodKey"/> <c>null</c>) має власний ресурс.
    /// <para>
    /// ⚠ Власник — транзакція: замок знімає коміт або відкат, забутого
    /// <c>sp_releaseapplock</c> бути не може. Поза транзакцією
    /// <c>sp_getapplock</c> відмовляє, тож викликач мусить її відкрити.
    /// </para>
    /// </remarks>
    public async Task LockSlotAsync(int projectId, int? periodKey, CancellationToken ct)
    {
        var resource = string.Create(
            CultureInfo.InvariantCulture,
            $"ecr.rpt-slot.{projectId}.{(periodKey is { } key ? key.ToString(CultureInfo.InvariantCulture) : "year")}");

        var result = new Microsoft.Data.SqlClient.SqlParameter("@rc", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.Output,
        };

        await db.Database.ExecuteSqlRawAsync(
            "EXEC @rc = sp_getapplock @Resource = @res, @LockMode = N'Exclusive', "
            + "@LockOwner = N'Transaction', @LockTimeout = @timeout;",
            [
                result,
                new Microsoft.Data.SqlClient.SqlParameter("@res", resource),
                new Microsoft.Data.SqlClient.SqlParameter("@timeout", SlotLockTimeoutMs),
            ],
            ct).ConfigureAwait(false);

        // ⚠ Не дочекалися — та сама відмова 409, що й на вичерпаному
        // очікуванні блокування рядка: дія не виконана, її можна повторити.
        if (result.Value is not int rc || rc < 0)
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.SheetBusy,
                "Зрізи звітності за цей період саме перебудовуються. Нічого не збережено; повторіть дію за мить.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = LockWaitGuard.MessageKey,
                });
        }
    }

    /// <summary>Скільки чекати замка слоту зрізів, мс.</summary>
    /// <remarks>
    /// Обидві сторони тримають замок коротко: побудова — на запит статусу й
    /// перемикання, робочий процес — на перерахунок статусу зрізів. Пів хвилини —
    /// із запасом на навантажений день дедлайну.
    /// </remarks>
    private const int DefaultSlotLockTimeoutMs = 30_000;

    /// <summary>Скільки чекати замка слоту, мс; за замовчуванням — <see cref="DefaultSlotLockTimeoutMs"/>.</summary>
    /// <remarks>Окремою властивістю лише заради тестів відмови «слот зайнятий» (прийом <c>RowCeiling</c>): 30 с у тесті — забагато.</remarks>
    public int SlotLockTimeoutMs { get; init; } = DefaultSlotLockTimeoutMs;

    /// <inheritdoc />
    public async Task MarkSubmittedAsync(long snapshotId, int userId, CancellationToken ct)
    {
        var snapshot = await db.ReportSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Зрізу {snapshotId} не існує.");

        // ⛔ Після подання зріз ІММУТАБЕЛЬНИЙ. Повторна побудова створює НОВИЙ
        // зріз, а не переписує цей: інакше звіт, роздрукований учора, і той
        // самий звіт сьогодні дали б різні числа без жодного сліду.
        snapshot.MarkSubmitted(userId);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SnapshotStatus> RefreshStatusAsync(long snapshotId, CancellationToken ct)
    {
        // ⛔ R5-W1 / W1-01: рядок зрізу — під `UPDLOCK` до кінця транзакції
        // робочого процесу, і статус даних рахується ПІСЛЯ блокування. Маркера
        // конкуренції в `rpt.ReportSnapshot` немає: два затвердження різних
        // аркушів одного проєкту рахували статус кожне зі свого знімка, і
        // виграв би останній записаний — застарілий. Під блокуванням другий
        // чекає коміту першого й рахує вже з його переходом (запит під RCSI
        // бачить закомічене на момент СВОГО початку).
        var snapshot = await db.ReportSnapshots
            .FromSql($"SELECT * FROM rpt.ReportSnapshot WITH (UPDLOCK, ROWLOCK) WHERE Id = {snapshotId}")
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Зрізу {snapshotId} не існує.");

        // Поданий зріз статусу не міняє — це відхиляє сама сутність.
        if (snapshot.Status == SnapshotStatus.Submitted)
        {
            return snapshot.Status;
        }

        var status = await StatusOfDataAsync(
            snapshot.ProjectId,
            snapshot.PeriodKey is { } key ? new PeriodKey(key) : null,
            ct).ConfigureAwait(false);

        // ⛔ R6-X7 / X7-01: ЗАСТАРІЛИЙ зріз (ФВ-10.5 — після його побудови актуальним
        // став прогін його проєкту й періоду) статусу даних НЕ успадковує: він `Draft`.
        // Статус зрізу — це «що бачить регулятор» (`rpt.v_*` бере лише `Approved`/
        // `Submitted`), а числа застарілого зрізу старші за затверджені дані. Раніше
        // подання останнього аркуша давало такому зрізу `Submitted` і морозило його
        // (`ReportSnapshotSync.MarkSubmittedAsync` морозить лише на `Approved`/
        // `Submitted`) — держава отримувала старі числа з позначкою «подано», а
        // поданий зріз уже не виправити. `Draft` прибирає його з `rpt.v_*` і не дає
        // заморозити; правильні числа дає НОВА побудова — її статус успадковується від
        // даних, і вона не застаріла.
        if (status != SnapshotStatus.Draft
            && await ReportSnapshotStaleness.StaleSnapshotIds(db)
                .AnyAsync(id => id == snapshot.Id, ct)
                .ConfigureAwait(false))
        {
            status = SnapshotStatus.Draft;
        }

        snapshot.RefreshStatus(status);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return status;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Найновіші першими і зі стелею. Зрізів за рік накопичуються тисячі:
    /// перелік «усіх» довелося б гортати саме тоді, коли потрібен останній.
    /// </remarks>
    public async Task<IReadOnlyList<ReportSnapshotSummary>> ListAsync(
        int? projectId, int? periodKey, IReadOnlyCollection<int>? visibleProjectIds, CancellationToken ct)
    {
        // ⛔ Порожній перелік видимих проєктів — це «жодного», а не «усі»
        // (Q-239). Різниця тут і є вся різниця між фільтром і його
        // відсутністю: користувач без жодного гранта на проєкт мусить бачити
        // порожньо, а не всю базу.
        if (visibleProjectIds is { Count: 0 })
        {
            return [];
        }

        var query = db.ReportSnapshots
            .AsNoTracking()
            .Where(s => projectId == null || s.ProjectId == projectId)
            .Where(s => periodKey == null || s.PeriodKey == periodKey);

        if (visibleProjectIds is not null)
        {
            // ⚠ Матеріалізований масив, а не сам інтерфейс: EF перекладає
            // `Contains` по параметру-колекції, і форма з `null`-перевіркою
            // всередині виразу («visible == null || visible.Contains(…)») не
            // транслювалася б — умова будується поза виразом.
            var visible = visibleProjectIds as int[] ?? [.. visibleProjectIds];
            query = query.Where(s => visible.Contains(s.ProjectId));
        }

        // ⚠ Застарілість (ФВ-10.5) — підзапит у тому самому SELECT, а не другий
        // прохід: визначення одне для переліку й журналу (`ReportSnapshotStaleness`).
        var stale = ReportSnapshotStaleness.StaleSnapshotIds(db);

        return await query
            .OrderByDescending(s => s.BuiltAt)
            .Take(MaxSnapshots)
            .Select(s => new ReportSnapshotSummary(
                s.Id,
                s.ReportVersionId,
                s.ProjectId,
                s.PeriodKey,
                s.Status.ToString(),
                s.IsCurrent,
                s.RowCount,

                // ⚠ Сума віддається рядком. Байти в JSON перетворюються на
                // base64, який неможливо звірити очима з тим, що показує
                // SSRS, — а звіряють їх саме очима.
                s.ContentHash == null ? null : Convert.ToHexString(s.ContentHash),
                s.BuiltAt)
            {
                HashFormat = s.HashFormat ?? VerifyReportSnapshotHandler.FormatUnknown,
                IsStale = stale.Contains(s.Id),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int?> FindProjectIdAsync(long snapshotId, CancellationToken ct)
        => await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => s.Id == snapshotId)
            .Select(s => (int?)s.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SnapshotHashes?> VerifyAsync(long snapshotId, CancellationToken ct)
    {
        var stored = await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => s.Id == snapshotId)
            .Select(s => new StoredHash(s.Id, s.ContentHash))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        // ⚠ Порядок — той самий, у якому рядки хешувалися при побудові:
        // `RowNo` зростає, а комірки рядка йдуть у порядку колонок ОПИСУ.
        // Первинний ключ (SnapshotId, RowNo, ColumnCode) дав би АЛФАВІТНИЙ
        // порядок колонок, тому комірки одного рядка впорядковує опис, а не база.
        var described = await DescribedColumnsAsync(snapshotId, ct).ConfigureAwait(false) ?? [];

        var rows = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId)
            .OrderBy(r => r.RowNo)
            .Take(MaxRows * Math.Max(described.Count, LegacyColumns.Length))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var order = StoredLayout(described, rows).Select(c => c.Code).ToList();

        var ordered = rows
            .OrderBy(r => r.RowNo)
            .ThenBy(r => ColumnOrder(order, r.ColumnCode))
            .ThenBy(r => r.ColumnCode, StringComparer.Ordinal)
            .ToList();

        var storedHex = stored.Hash is null ? string.Empty : Convert.ToHexString(stored.Hash);
        var actualHex = Convert.ToHexString(ComputeHash(ordered));

        // Стара сума рахується лише тоді, коли нова не збіглася: для зрізів,
        // побудованих після BE-17, вона не потрібна взагалі.
        var legacyHex = string.Equals(storedHex, actualHex, StringComparison.Ordinal)
            ? null
            : Convert.ToHexString(ComputeLegacyHash(ordered));

        return new SnapshotHashes(storedHex, actualHex, legacyHex);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Умова <c>HashFormat IS NULL</c> стоїть у самому UPDATE, а не в перевірці
    /// перед ним: звірка й нічна задача можуть писати той самий зріз одночасно.
    /// Один рядок, автокоміт — жодної довгої транзакції.
    /// </remarks>
    public async Task<bool> RecordHashFormatAsync(long snapshotId, string format, CancellationToken ct)
        => await db.ReportSnapshots
            .Where(s => s.Id == snapshotId && s.HashFormat == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.HashFormat, format), ct)
            .ConfigureAwait(false) > 0;

    /// <inheritdoc />
    public async Task<SnapshotRowsPage?> RowsAsync(
        long snapshotId, int afterRowNo, int limit, string language, CancellationToken ct)
    {
        var version = await VersionOfAsync(snapshotId, ct).ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        var described = ReportColumnSpec.Parse(version.ColumnsJson);

        // ⛔ R8: макет застосовується ТУТ, на видачі, а не при побудові — у
        // `rpt.ReportRow` і в `ContentHash` його немає (D-53: зміна адитивна).
        var layout = ReportLayout.Of(
            version.RulesJson, [.. described.Select(c => new ReportColumnCommand(c.Code, c.Kind))]);

        if (!layout.IsEmpty)
        {
            return await LaidOutRowsAsync(snapshotId, version, described, layout, afterRowNo, limit, language, ct)
                .ConfigureAwait(false);
        }

        // Номери рядків окремим запитом: сторінка рахується в РЯДКАХ звіту, а
        // `rpt.ReportRow` зберігає комірки, і їх у рядку стільки, скільки колонок.
        var rowNos = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && r.RowNo > afterRowNo)
            .Select(r => r.RowNo)
            .Distinct()
            .OrderBy(n => n)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var last = rowNos.Take(limit).LastOrDefault();

        var cells = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && r.RowNo > afterRowNo && r.RowNo <= last)
            .OrderBy(r => r.RowNo)
            .Take(limit * MaxColumnsPerRow)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stored = StoredLayout(described, cells);

        return new SnapshotRowsPage(
            Titled(stored, language),
            WideRows(cells, stored),
            rowNos.Count > limit ? last : null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Рядки, а не комірки: <c>rpt.ReportRow</c> зберігає комірку на колонку.
    /// Підрахунок іде індексом первинного ключа (<c>SnapshotId, RowNo, …</c>)
    /// і зупиняється на <paramref name="atMost"/>.
    /// </remarks>
    public async Task<int> CountRowsAsync(long snapshotId, int atMost, CancellationToken ct)
        => await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId)
            .Select(r => r.RowNo)
            .Distinct()
            .OrderBy(n => n)
            .Take(Math.Max(atMost, 0))
            .CountAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Сторінка зрізу, розкладеного макетом (<c>R8</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Розкладається ВЕСЬ зріз, а сторінка нарізається вже з упорядкованого:
    /// група й підсумок — властивості ЗРІЗУ, і порахувати їх по сторінці
    /// означало б «суму», яка на кожній сторінці інша. Дешевший порядок у SQL
    /// вимагав би рахувати підсумки другим кодом, а саме цього <c>R8</c> і не
    /// робить.
    /// <para>
    /// ⚠ P4: повне читання — ОДНЕ на зріз, а не на кожну сторінку: розкладений
    /// зріз лежить у кеші (<see cref="LaidOutAsync"/>). Вивантаження книги
    /// гортає зріз сторінками по 500, і до цього кроку кожна з них перечитувала
    /// до <see cref="MaxRows"/> рядків.
    /// </para>
    /// <para>
    /// ⚠ Курсор тут означає, СКІЛЬКИ рядків уже віддано, а не останній
    /// <c>RowNo</c>: у порядку груп <c>RowNo</c> не зростає. Без макета обидва
    /// числа збігаються, тож клієнт випадків не розрізняє.
    /// </para>
    /// <para>
    /// ⛔ AN-120 / L1-02: зріз, що не влазить у бюджет кешу цілком, кешується
    /// БЕЗ комірок — лише порядок <c>RowNo</c>, групи й підсумки
    /// (<see cref="LaidOutSnapshot.Rows"/> = <c>null</c>), а сторінка дочитує
    /// комірки лише своїх рядків (<c>WHERE RowNo IN (…)</c>). До цього зріз понад
    /// бюджет читався ЦІЛКОМ на кожну сторінку, а вивантаження книги — до 101
    /// разу поспіль.
    /// </para>
    /// </remarks>
    private async Task<SnapshotRowsPage> LaidOutRowsAsync(
        long snapshotId, StoredVersion version, IReadOnlyList<ReportColumnSpec> described, ReportLayout layout,
        int delivered, int limit, string language, CancellationToken ct)
    {
        var laidOut = await LaidOutAsync(snapshotId, version, described, layout, ct).ConfigureAwait(false);
        var total = laidOut.Order.Length;
        var from = Math.Clamp(delivered, 0, total);
        var taken = Math.Min(Math.Max(limit, 0), total - from);

        // ⛔ Сторінка й мова — на КОЖЕН запит, поза кешем: у кеші лише вміст
        // зрізу, однаковий для кожного, хто його читає.
        var page = laidOut.Rows is { } rows
            ? rows.Skip(from).Take(taken).ToList()
            : await PageByRowNoAsync(snapshotId, laidOut, from, taken, ct).ConfigureAwait(false);

        return new SnapshotRowsPage(
            Titled(laidOut.Stored, language),
            page,
            from + taken < total ? from + taken : null,
            laidOut.Groups,
            laidOut.Totals,
            layout.ShowGroupHeader);
    }

    /// <summary>Рядки сторінки зрізу, закешованого без комірок: лише свої <c>RowNo</c>, у порядку макета.</summary>
    private async Task<List<SnapshotRow>> PageByRowNoAsync(
        long snapshotId, LaidOutSnapshot laidOut, int from, int taken, CancellationToken ct)
    {
        if (taken <= 0)
        {
            return [];
        }

        var slice = laidOut.Order.AsSpan(from, taken).ToArray();

        var cells = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && slice.Contains(r.RowNo))
            .OrderBy(r => r.RowNo)
            .Take(slice.Length * MaxColumnsPerRow)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byRowNo = WideRows(cells, laidOut.Stored).ToDictionary(r => r.RowNo);

        // Порядок — макета (групи), а не бази: `RowNo` у порядку груп не зростає.
        return [.. slice.Where(byRowNo.ContainsKey).Select(n => byRowNo[n])];
    }

    /// <summary>Зріз, розкладений макетом: з кешу або прочитаний і розкладений зараз.</summary>
    /// <remarks>
    /// ⛔ <b>Безпека.</b> Сюди доходить лише той, кого вже пропустив обробник
    /// (право <c>Report.ViewRegulatory</c>/<c>Report.Export</c>, право на вміст
    /// <c>Report.ViewSnapshot</c> і грант на проєкт зрізу —
    /// <c>GetSnapshotRowsHandler</c>, <c>ExportSnapshotHandler</c>),
    /// а існування зрізу щойно перевірив <see cref="VersionOfAsync"/>: видалений
    /// зріз дає <c>null</c> ДО кешу, хоч би що в ньому лишалося. Доступу нижче
    /// рівня проєкту (колонки, рядки) у зрізу немає — тож кешований вміст від
    /// користувача не залежить, і ключ користувача не містить.
    /// <para>
    /// ⛔ <b>Ключ.</b> Зріз незмінний (<see cref="MarkSubmittedAsync"/>), але
    /// ключ однаково несе <c>ContentHash</c> і сам опис версії
    /// (<c>ColumnsJson</c>, <c>RulesJson</c>), від якого залежить розклад:
    /// будь-яка зміна вмісту чи макета дає ІНШИЙ ключ, а не застарілу відповідь.
    /// Зріз без суми ще будується (рядки пишуться другим збереженням
    /// <see cref="BuildAsync"/>) — такий не кешується взагалі.
    /// </para>
    /// <para>
    /// ⚠ <b>Межа пам'яті.</b> <c>SizeLimit</c> у спільного кешу немає і не буде
    /// (<c>RD-05</c>, коментар біля <c>AddMemoryCache</c>), тож стелю тримає
    /// власний бюджет на кожен екземпляр кешу (<see cref="MaxCachedCells"/>)
    /// плюс строк: ковзний <see cref="LaidOutSliding"/> і абсолютний
    /// <see cref="LaidOutLifetime"/>. Зріз, що не влазить цілком, кешується без
    /// комірок (AN-120 / L1-02); не влазить і так — не кешується.
    /// </para>
    /// <para>
    /// ⛔ <b>Одночасні промахи</b> (AN-120 / L1-02) — одне читання на всіх
    /// (<see cref="SingleFlight{T}"/>, <c>RD-05</c>): після побудови нового зрізу
    /// його відкривають кілька людей одразу, і кожен читав би його повністю.
    /// </para>
    /// </remarks>
    private async Task<LaidOutSnapshot> LaidOutAsync(
        long snapshotId, StoredVersion version, IReadOnlyList<ReportColumnSpec> described, ReportLayout layout,
        CancellationToken ct)
    {
        var key = version.ContentHash is { Length: > 0 } hash
            ? new LaidOutKey(snapshotId, Convert.ToHexString(hash), version.ColumnsJson, version.RulesJson)
            : null;

        if (key is null)
        {
            return await ReadAndLayOutAsync(snapshotId, described, layout, ct).ConfigureAwait(false);
        }

        if (memory.TryGetValue(key, out LaidOutSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        var flight = Flights.GetValue(memory, static _ => new SingleFlight<LaidOutSnapshot>());

        return await flight
            .RunAsync(
                key.FlightKey(),
                async token =>
                {
                    // Попередній політ міг уже покласти зріз у кеш між перевіркою
                    // вище і цим місцем — тоді читати вдруге нема чого.
                    if (memory.TryGetValue(key, out LaidOutSnapshot? warmed) && warmed is not null)
                    {
                        return warmed;
                    }

                    var built = await ReadAndLayOutAsync(snapshotId, described, layout, token).ConfigureAwait(false);
                    Remember(key, built);
                    return built;
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Читає весь зріз і розкладає його макетом — повний вигляд, з комірками.</summary>
    private async Task<LaidOutSnapshot> ReadAndLayOutAsync(
        long snapshotId, IReadOnlyList<ReportColumnSpec> described, ReportLayout layout, CancellationToken ct)
    {
        var cells = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId)
            .OrderBy(r => r.RowNo)
            .Take(MaxRows * Math.Max(described.Count, LegacyColumns.Length))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stored = StoredLayout(described, cells);
        var view = layout.Apply(WideRows(cells, stored));

        return new LaidOutSnapshot(
            stored, [.. view.Rows.Select(r => r.RowNo)], view.Groups, view.Totals, view.Rows, cells.Count);
    }

    /// <summary>Кладе розкладений зріз у кеш: цілком, якщо влазить, інакше — без комірок.</summary>
    private void Remember(LaidOutKey key, LaidOutSnapshot built)
    {
        var budget = Budgets.GetValue(memory, static _ => new CacheBudget());

        // Порядок і групи коштують і в повному записі, і в стислому.
        var skeleton = built.Order.Length + built.Groups.Count;
        var full = built.CellCount + skeleton;

        LaidOutSnapshot entry;
        int cost;

        if (budget.TryReserve(full, LaidOutCacheBudget))
        {
            (entry, cost) = (built, full);
        }
        else if (budget.TryReserve(skeleton, LaidOutCacheBudget))
        {
            (entry, cost) = (built with { Rows = null }, skeleton);
        }
        else
        {
            return;
        }

        var options = new MemoryCacheEntryOptions
        {
            SlidingExpiration = LaidOutSliding,
            AbsoluteExpirationRelativeToNow = LaidOutLifetime,
        };

        // Бюджет повертається, коли запис іде з кешу з БУДЬ-якої причини:
        // строк, заміна тим самим ключем, тиск пам'яті.
        options.RegisterPostEvictionCallback((_, _, _, _) => budget.Release(cost));

        memory.Set(key, entry, options);
    }

    /// <summary>
    /// Скільки одиниць (комірок <c>rpt.ReportRow</c> плюс рядків порядку й груп)
    /// усі розкладені зрізи разом тримають в одному кеші.
    /// </summary>
    /// <remarks>
    /// Найбільший зріз на п'ять колонок — <see cref="MaxRows"/> рядків, тобто
    /// приблизно ця стеля: він влазить сам, а решта чекає, поки він вийде за
    /// строком. ⚠ AN-120 / L1-02: колонок буває й десять, і тоді зріз цілком не
    /// влазить — але влазить без комірок (лише <c>RowNo</c> і групи).
    /// </remarks>
    private const int MaxCachedCells = 1_000_000;

    /// <summary>Ковзний строк розкладеного зрізу: сесія гортання й вивантаження.</summary>
    private static readonly TimeSpan LaidOutSliding = TimeSpan.FromMinutes(10);

    /// <summary>Абсолютна стеля життя розкладеного зрізу.</summary>
    private static readonly TimeSpan LaidOutLifetime = TimeSpan.FromHours(1);

    /// <summary>Бюджет на кожен екземпляр кешу; кеш, що зник, забирає й бюджет.</summary>
    private static readonly ConditionalWeakTable<IMemoryCache, CacheBudget> Budgets = [];

    /// <summary>Політ розкладу на кожен екземпляр кешу: будівник Scoped, а промахи — спільні на процес.</summary>
    private static readonly ConditionalWeakTable<IMemoryCache, SingleFlight<LaidOutSnapshot>> Flights = [];

    /// <summary>Ключ розкладеного зрізу: зріз, його вміст і опис, що задає розклад.</summary>
    private sealed record LaidOutKey(long SnapshotId, string ContentHash, string ColumnsJson, string RulesJson)
    {
        /// <summary>Рядковий ключ польоту: та сама тотожність, що й у записі кешу.</summary>
        public string FlightKey()
        {
            var layout = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(ColumnsJson + "\u0000" + RulesJson)));

            return string.Create(CultureInfo.InvariantCulture, $"{SnapshotId}|{ContentHash}|{layout}");
        }
    }

    /// <summary>Розкладений зріз: колонки, порядок рядків, групи й підсумки — без мови, сторінки й користувача.</summary>
    /// <param name="Stored">Колонки збереженого зрізу.</param>
    /// <param name="Order"><c>RowNo</c> у порядку макета: групи одна за одною.</param>
    /// <param name="Groups">Групи по всьому зрізу.</param>
    /// <param name="Totals">Підсумки по всьому зрізу.</param>
    /// <param name="Rows">Рядки в порядку <paramref name="Order"/>; <c>null</c> — закешовано без комірок.</param>
    /// <param name="CellCount">Скільки комірок прочитано при розкладі.</param>
    private sealed record LaidOutSnapshot(
        IReadOnlyList<ReportColumnSpec> Stored,
        int[] Order,
        IReadOnlyList<SnapshotRowGroup> Groups,
        IReadOnlyList<SnapshotTotal> Totals,
        IReadOnlyList<SnapshotRow>? Rows,
        int CellCount);

    /// <summary>Лічильник одиниць у кеші; потокобезпечний.</summary>
    private sealed class CacheBudget
    {
        private long _used;

        public bool TryReserve(int units, int capacity)
        {
            if (Interlocked.Add(ref _used, units) <= capacity)
            {
                return true;
            }

            Interlocked.Add(ref _used, -units);
            return false;
        }

        public void Release(int units) => Interlocked.Add(ref _used, -units);
    }

    /// <summary>Колонки зрізу, підписані мовою запиту (<c>R9</c>).</summary>
    /// <remarks>
    /// ⚠ Фолбек лежить в <see cref="ReportColumnNames"/>, а не тут: книга бере
    /// вже підписані колонки з цієї самої сторінки, і друга копія ланцюга
    /// розійшлася б із першою мовчки.
    /// </remarks>
    private static IReadOnlyList<SnapshotColumn> Titled(
        IReadOnlyList<ReportColumnSpec> columns, string language)
        => [.. columns.Select(c => new SnapshotColumn(
            c.Code, c.Kind, ReportColumnNames.Of(c.Code, c.NameL10n, language)))];

    /// <summary>Комірки зрізу, зведені в рядки: значення за кодом колонки в порядку опису.</summary>
    /// <param name="cells">Комірки зрізу.</param>
    /// <param name="columns">Колонки в порядку опису.</param>
    /// <returns>Рядки за зростанням <c>RowNo</c>.</returns>
    /// <remarks>
    /// ⚠ P4 (перф-аудит): комірки групи складаються в словник за кодом ОДИН раз,
    /// а не шукаються лінійно на кожну колонку — інакше O(R·C²) на зрізі в
    /// 200 000 рядків. Код колонки, що трапився в групі двічі, дає ПЕРШУ комірку
    /// (<c>TryAdd</c>) — рівно те, що давав <c>FirstOrDefault</c>. У базі такого
    /// не буває (ключ <c>SnapshotId, RowNo, ColumnCode</c>), але семантика
    /// тримається й без бази.
    /// <para>
    /// Публічний лише заради прямого тесту еквівалентності: дублікат коду
    /// через базу не відтворити.
    /// </para>
    /// </remarks>
    public static List<SnapshotRow> WideRows(
        IReadOnlyList<ReportRow> cells, IReadOnlyList<ReportColumnSpec> columns)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(columns);

        return [.. cells
            .GroupBy(c => c.RowNo)
            .OrderBy(g => g.Key)
            .Select(g => new SnapshotRow(g.Key, RowCells(g, columns)))];
    }

    /// <summary>Значення одного рядка за кодом колонки; перша комірка коду виграє.</summary>
    private static Dictionary<string, object?> RowCells(
        IEnumerable<ReportRow> group, IReadOnlyList<ReportColumnSpec> columns)
    {
        var byCode = new Dictionary<string, ReportRow>(StringComparer.Ordinal);

        foreach (var cell in group)
        {
            byCode.TryAdd(cell.ColumnCode, cell);
        }

        return columns.ToDictionary(
            c => c.Code,
            c => ValueOf(byCode.GetValueOrDefault(c.Code), c.Kind),
            StringComparer.Ordinal);
    }

    /// <summary>Стеля комірок одного рядка у сторінці рядків.</summary>
    private const int MaxColumnsPerRow = 64;

    /// <summary>Значення комірки для відповіді: число без хвостових нулів масштабу бази.</summary>
    private static object? ValueOf(ReportRow? cell, string kind)
        => kind switch
        {
            _ when cell is null => null,
            ReportSourceColumns.Number => cell.ValueNumeric is { } number
                ? decimal.Parse(Canonical(number), CultureInfo.InvariantCulture)
                : null,
            "date" => cell.ValueDate?.ToString("O", CultureInfo.InvariantCulture),
            _ => cell.ValueString,
        };

    /// <summary>
    /// Колонки, які зрізи мали ДО <c>D-52a</c>: будівник писав їх завжди, хоч би що стояло в описі.
    /// </summary>
    private static readonly ReportColumnSpec[] LegacyColumns =
    [
        new("DocumentId", ReportSourceColumns.Number),
        new("RowKey", ReportSourceColumns.Text),
        new("OutputCode", ReportSourceColumns.Text),
        new("Value", ReportSourceColumns.Number),
        new("SubstanceEntryId", ReportSourceColumns.Number),
    ];

    /// <summary>Колонки опису версії, за якою побудовано зріз; <c>null</c> — зрізу немає.</summary>
    private async Task<IReadOnlyList<ReportColumnSpec>?> DescribedColumnsAsync(long snapshotId, CancellationToken ct)
        => await VersionOfAsync(snapshotId, ct).ConfigureAwait(false) is { } version
            ? ReportColumnSpec.Parse(version.ColumnsJson)
            : null;

    /// <summary>Опис версії, за якою побудовано зріз; <c>null</c> — зрізу немає.</summary>
    /// <remarks>
    /// ⚠ Колонки й правила беруться ОДНИМ запитом: читати їх окремо означало б
    /// два звернення на кожну сторінку рядків, бо макет (<c>R8</c>) лежить у
    /// <c>RulesJson</c>, а колонки — в <c>ColumnsJson</c>.
    /// </remarks>
    private Task<StoredVersion?> VersionOfAsync(long snapshotId, CancellationToken ct)
        => (from snapshot in db.ReportSnapshots.AsNoTracking()
            join version in db.ReportVersions.AsNoTracking() on snapshot.ReportVersionId equals version.Id
            where snapshot.Id == snapshotId
            select new StoredVersion(version.ColumnsJson, version.RulesJson, snapshot.ContentHash))
            .FirstOrDefaultAsync(ct);

    /// <summary>Опис версії зрізу так, як він збережений, і сума вмісту зрізу.</summary>
    /// <remarks>Сума — частина ключа кешу розкладеного зрізу (P4); окремого запиту вона не коштує.</remarks>
    private sealed record StoredVersion(string ColumnsJson, string RulesJson, byte[]? ContentHash);

    /// <summary>Колонки ЗБЕРЕЖЕНОГО зрізу в порядку, у якому їх складала побудова.</summary>
    /// <remarks>
    /// ⛔ Зріз, побудований до <c>D-52a</c>, має п'ять колонок незалежно від
    /// опису. Ознака — у рядках є код, якого опис не знає; тоді порядок старий,
    /// інакше сума такого зрізу перестала б збігатися (BE-17).
    /// </remarks>
    private static IReadOnlyList<ReportColumnSpec> StoredLayout(
        IReadOnlyList<ReportColumnSpec> described, IReadOnlyList<ReportRow> cells)
    {
        var known = described.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);

        return known.Count > 0 && cells.All(c => known.Contains(c.ColumnCode)) ? described : LegacyColumns;
    }

    private static int ColumnOrder(List<string> order, string columnCode)
    {
        var index = order.IndexOf(columnCode);
        return index < 0 ? order.Count : index;
    }

    /// <summary>Збережена сума зрізу.</summary>
    private sealed record StoredHash(long Id, byte[]? Hash);

    /// <summary>Стеля переліку РІЗНИХ статусів аркушів: значень <see cref="DocumentStatus"/> менше.</summary>
    private const int MaxStatuses = 32;

    /// <summary>Стеля переліку зрізів.</summary>
    private const int MaxSnapshots = 500;

    /// <summary>
    /// Статус, виведений зі стану аркушів проєкту.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Approved</c> лише тоді, коли затверджені **всі** аркуші. Один
    /// незатверджений аркуш робить увесь зріз чернетковим — і це правильно:
    /// звіт, у якому половина даних погоджена, а половина ні, не є погодженим
    /// звітом (D-65).
    /// </remarks>
    private async Task<SnapshotStatus> StatusOfDataAsync(
        int projectId, PeriodKey? periodKey, CancellationToken ct)
    {
        // ⛔ R5-W1 / W1-02: джерело рядків — СКЛАД документів проєкту
        // (`doc.DocumentSheet`, `IsIncluded`) × періоди, а рядок
        // `wf.ApprovalState` лише ДОповнює його; аркуш без рядка стану — `Draft`.
        // Раніше джерелом були самі рядки стану, а вони з'являються лише з
        // першою дією робочого процесу (`WorkflowStore.GetOrCreateAsync`): один
        // поданий аркуш із 48 давав зрізу `Submitted`, і звіт ішов у `rpt.v_*`
        // як поданий. Те саме правило, що в `DocumentStore.SheetStatesQuery` і
        // `CampaignSummaryStore` (U-03): аркуш поза складом не враховується.
        //
        // ⚠ Річний зріз (`periodKey == null`) — УСІ періоди проєкту: зріз за рік
        // поданий лише тоді, коли подано кожен період. Це найсуворіше
        // прочитання D-65 — ранній «поданий» річний звіт гірший за чернетковий.
        //
        // ⚠ Корельований підзапит (`OUTER APPLY`), не цикл; `PeriodKey` — у
        // предикаті партиційованої `wf.ApprovalState` (урок `WR-05`).
        int? key = periodKey?.Value;

        var query =
            from document in db.Documents.AsNoTracking()
            where document.ProjectId == projectId
            join sheet in db.DocumentSheets.AsNoTracking()
                on document.Id equals sheet.DocumentId
            where sheet.IsIncluded
            from period in db.Periods.AsNoTracking()
            where period.ProjectId == projectId
                  && (key == null || period.PeriodKeyValue == key)
            select db.ApprovalStates
                       .Where(a => a.DocumentId == document.Id
                                   && a.SheetDefId == sheet.SheetDefId
                                   && a.PeriodKey == period.PeriodKeyValue)
                       .Select(a => (DocumentStatus?)a.Status)
                       .FirstOrDefault()
                   ?? DocumentStatus.Draft;

        // ⛔ AN-120 / L1-01: які статуси є — агрегатом у SQL, а не першими
        // `MaxRows` станами за `Id`. Підмножина могла не містити саме того
        // чернеткового аркуша, через який зріз не `Approved`. Різних статусів
        // лише кілька, тож `Take` тут — межа переліку значень enum, а не даних.
        var statuses = await query
            .Distinct()
            .OrderBy(s => s)
            .Take(MaxStatuses)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Аркушів немає — зріз чернетковий. «Нічого не подано» і «все
        // затверджено» не можна плутати: перше означає порожній звіт.
        if (statuses.Count == 0)
        {
            return SnapshotStatus.Draft;
        }

        if (statuses.TrueForAll(s => s == DocumentStatus.Approved))
        {
            return SnapshotStatus.Approved;
        }

        return statuses.Exists(s => s is DocumentStatus.Submitted or DocumentStatus.Approved)
               && !statuses.Exists(s => s is DocumentStatus.Draft or DocumentStatus.Rejected)
            ? SnapshotStatus.Submitted
            : SnapshotStatus.Draft;
    }

    /// <summary>Мітка (<c>TagWith</c>) запиту, що читає джерело зрізу.</summary>
    public const string AggregateTag = "ReportSnapshotBuilder.Aggregate";

    /// <summary>Агрегує результати розрахунку в рядки зрізу.</summary>
    private async Task<List<CellValue>> AggregateAsync(
        IReadOnlyList<ReportColumnSpec> layout, ReportRowRules rowRules,
        IReadOnlyDictionary<string, object?> parameters, int projectId, PeriodKey? periodKey,
        CancellationToken ct)
    {
        var query =
            from result in db.CalculationResults.AsNoTracking()
            join document in db.Documents.AsNoTracking()
                on result.DocumentId equals document.Id
            join run in db.CalculationRuns.AsNoTracking()
                on result.CalculationRunId equals run.Id
            join unit in db.Units.AsNoTracking()
                on result.UnitId equals unit.Id
            join project in db.Projects.AsNoTracking()
                on document.ProjectId equals project.Id
            where document.ProjectId == projectId
                  && (periodKey == null || result.PeriodKey == periodKey.Value.Value)

                  // ⛔ HSE301 A3a (D-175, V-6): лише ВИХОДИ. Проміжні значення видимих
                  // формул лежать у тій самій таблиці, але в зріз не йдуть: інакше
                  // поява прапорця «видима» в методології змінила б вміст і
                  // `ContentHash` уже поданих зрізів (D-53).
                  && result.Kind == CalculationResultKind.Output

                  // ⚠ Лише АКТУАЛЬНИЙ прогін. Без цієї умови зріз склав би
                  // результати всіх прогонів разом — числа виросли б кратно
                  // кількості перерахунків і лишилися б правдоподібними.
                  && run.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus

                  // ⛔ Той самий пріоритет, що й `CalculationResultStore.ReadCurrentAsync`
                  // (третя хвиля UX-PASS R4, «CalculationRun ховає результати
                  // сусідніх документів»): документний прогін (`DocumentId`
                  // заданий) і проєктний прогін (`DocumentId == null`) можуть
                  // бути `Current` ОДНОЧАСНО для того самого документа —
                  // документний НЕ знімає актуальність із проєктного (той рахує
                  // й ІНШІ документи). Без цієї умови зріз склав би результати
                  // ОБОХ прогонів для документа з власним прогоном — те саме
                  // подвоєння, що вище коментар уже застерігає, лише з іншого
                  // джерела. Рядок проєктного прогону береться ЛИШЕ якщо для
                  // цього документа й періоду НЕМАЄ власного актуального прогону.
                  && (run.DocumentId == result.DocumentId
                      || (run.DocumentId == null
                          && !db.CalculationRuns.Any(dedicated =>
                              dedicated.ProjectId == projectId
                              && dedicated.DocumentId == result.DocumentId
                              && dedicated.PeriodKey == result.PeriodKey
                              && dedicated.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus)))

            // ⛔ Сортування стоїть ДО проєкції, і це не косметика. Поки
            // `OrderBy` висів на вже спроєктованому `ResultRow`, EF не міг
            // перекласти запит узагалі: `ResultRow` — тип застосунку, і
            // впорядкувати за його властивістю в SQL нема як. Побудова зрізу
            // від цього не «була повільною» — вона падала
            // `InvalidOperationException` («could not be translated») на
            // КОЖНОМУ виклику, тобто не завершилася успіхом жодного разу за
            // весь час існування `rpt.*`. Не бачив цього ніхто: `ReportDef`
            // не створювало ніщо, тож до цього рядка виконання не доходило —
            // побудова відмовляла раніше, `ECR-RPT-0404` (директива №09
            // `W7`, сценарій `S-27`).
            orderby result.DocumentId, result.SourceRowKey, result.OutputCode
            select new ResultRow(
                result.DocumentId, result.SourceRowKey, result.OutputCode,
                result.Value, result.SubstanceEntryId,
                result.PeriodKey, result.UnitId, unit.Code, result.MethodologyVersionId, project.Code);

        // ⛔ AN-120 / L1-01: джерело читається ПОВНІСТЮ, потоком, без `Take`.
        // Правила відбору (`R5`) бачать кожен рядок джерела, а стеля стоїть на
        // їхньому ВИХОДІ: звіт, якому з 600 000 результатів потрібні 3 000,
        // будується, а не втрачає документи з більшими `Id`. Потік, а не
        // `ToListAsync`: у пам'яті лише рядки, що пройшли правила.
        var rows = new List<CellValue>();
        var rowNo = 0;

        // ⚠ Мітка в тексті запиту — для діагностики (плани, Query Store) і для тесту
        // X7-04, який перемикає прогін рівно в мить читання джерела.
        await foreach (var result in query.TagWith(AggregateTag).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            // Без правил (схема 1) рядок читається прямо з джерела, як до R5.
            var ruled = rowRules.IsEmpty
                ? null
                : Readers.ToDictionary(r => r.Key, r => r.Value(result), StringComparer.Ordinal);

            if (ruled is not null && !rowRules.Apply(ruled, parameters))
            {
                // Прихований рядок номера не займає: `RowNo` лишається суцільним.
                continue;
            }

            // ⛔ Понад стелю — відмова ДО створення зрізу: задача стає Failed,
            // попередній зріз лишається поточним (`SwitchCurrentAsync` не
            // викликався), і неповна форма з чинною сумою не з'являється.
            if (++rowNo > RowCeiling)
            {
                throw TooLarge(projectId, periodKey, RowCeiling);
            }

            // ⛔ Лише описані колонки і в порядку опису (D-52a): за цим порядком
            // рахується сума, і за ним її перераховує `VerifyAsync`.
            foreach (var column in layout)
            {
                var value = ruled is null ? Readers[column.Code](result) : ruled[column.Code];

                rows.Add(column.Kind == ReportSourceColumns.Number
                    ? new CellValue(rowNo, column.Code, null, (decimal?)value)
                    : new CellValue(rowNo, column.Code, (string?)value, null));
            }
        }

        return rows;
    }

    /// <summary>Відмова побудови: рядків зрізу (після правил) більше за стелю.</summary>
    private static BusinessRuleException TooLarge(int projectId, PeriodKey? periodKey, int limit)
        => new(
            ErrorCodes.ReportInvalid,
            $"Зріз проєкту {projectId} за період {periodKey?.Value.ToString(CultureInfo.InvariantCulture) ?? "рік"} "
            + $"має понад {limit} рядків: побудову відмовлено, попередній зріз лишається чинним.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-RPT-0422.snapshotTooLarge",
                ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                ["periodKey"] = periodKey?.Value.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>Значення комірки до появи зрізу: ідентифікатор зрізу додається після правил.</summary>
    private readonly record struct CellValue(int RowNo, string Code, string? Text, decimal? Number);

    /// <summary>Як прочитати кожне поле джерела <c>CalculationResults</c>.</summary>
    /// <remarks>
    /// Перелік кодів і їхні типи — у <see cref="ReportSourceColumns"/>; рівність
    /// двох таблиць стереже тест. Ідентифікатори йдуть як <c>decimal</c> з
    /// масштабом 0 — рівно так їх писав код до <c>D-52a</c> (формат <c>legacy</c>).
    /// </remarks>
    private static readonly Dictionary<string, Func<ResultRow, object?>> Readers = new(StringComparer.Ordinal)
    {
        ["DocumentId"] = r => (decimal)r.DocumentId,
        ["RowKey"] = r => r.SourceRowKey,
        ["OutputCode"] = r => r.OutputCode,
        ["Value"] = r => r.Value,
        ["SubstanceEntryId"] = r => (decimal?)r.SubstanceEntryId,
        ["PeriodKey"] = r => (decimal)r.PeriodKey,
        ["UnitId"] = r => (decimal)r.UnitId,
        ["UnitCode"] = r => r.UnitCode,
        ["MethodologyVersionId"] = r => (decimal)r.MethodologyVersionId,
        ["ProjectCode"] = r => r.ProjectCode,
    };

    /// <summary>Коди колонок, які будівник уміє прочитати з джерела.</summary>
    public static IReadOnlyCollection<string> ReadableColumns => Readers.Keys;

    /// <summary>Колонки зрізу за описом версії; відмовляє, якщо побудувати за ним не можна.</summary>
    /// <remarks>
    /// Створення версії таке відсіює (<see cref="ReportSourceColumns.Require"/>);
    /// сюди доходить лише опис, заведений до <c>D-52a</c> або повз застосунок.
    /// Мовчки пропустити колонку означало б зріз, у якому менше, ніж обіцяє опис.
    /// </remarks>
    private static IReadOnlyList<ReportColumnSpec> LayoutOf(ReportVersion version)
    {
        var rules = ReportRules.Parse(version.RulesJson);
        var columns = ReportColumnSpec.Parse(version.ColumnsJson);

        var broken = columns.FirstOrDefault(c =>
            !Readers.ContainsKey(c.Code)
            || !string.Equals(ReportSourceColumns.KindOf(rules.RowSource, c.Code), c.Kind, StringComparison.Ordinal));

        if (columns.Count == 0 || !ReportRowRules.IsSupported(rules.Schema) || broken is not null)
        {
            throw new InvalidOperationException(
                $"Версія звіту {version.Id}: за описом зріз не будується "
                + $"(схема {rules.Schema}, колонок {columns.Count}, непридатна колонка «{broken?.Code}»).");
        }

        return columns;
    }

    /// <summary>Актуальний прогін проєкту й періоду — лише для поля походження зрізу.</summary>
    /// <remarks>
    /// ⚠ Із документним виміром `CalculationRun.DocumentId` актуальних прогонів
    /// області може бути КІЛЬКА одночасно (проєктний + документні), а поле
    /// зрізу (<c>ReportSnapshot.CalculationRunId</c>) — одне. `FirstOrDefaultAsync`
    /// бере довільний із них: це прийнятно для одного посилального поля «звідки
    /// приблизно взято числа» (діагностика), АЛЕ самі ЧИСЛА зрізу рахує
    /// <see cref="AggregateAsync"/> окремим, повним по документах запитом — і
    /// саме він, а не це поле, визначає, що потрапляє в звіт.
    /// </remarks>
    private Task<long?> CurrentRunAsync(int projectId, PeriodKey? periodKey, CancellationToken ct)
        => db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId
                        && (periodKey == null || r.PeriodKey == periodKey.Value.Value)
                        && r.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Робить зріз поточним, знімаючи поточність із попереднього.
    /// </summary>
    /// <remarks>
    /// ⚠ Обидві половини — в одному наборі змін, який коміт застосує разом.
    /// Між ними існує стан із двома поточними зрізами, і регуляторна вʼюха в
    /// цю мить повернула б подвоєні рядки — не помилку, а просто вдвічі більше
    /// число. Фільтрований унікальний індекс не дав би це зберегти, але вже
    /// після того, як транзакція впала б посеред побудови.
    /// <para>
    /// ⛔ R6-X7 / X7-02: поточний зріз — один на ОПИС звіту (<c>ReportDefId</c>) ×
    /// проєкт × період, а не на версію. Вʼюха <c>rpt.v_&lt;Звіт&gt;</c> фільтрує за
    /// <c>d.Code</c> і версії не бачить (<c>D-53</c>: ім'я вʼюхи — довгоживучий
    /// контракт усіх версій опису), а побудова завжди бере найновішу опубліковану
    /// версію (<c>FindCurrentVersionAsync</c>). Доки поточність знімалася лише в межах
    /// версії, перша ж побудова після публікації нової версії лишала ДВА поточні зрізи
    /// одного періоду — і вʼюха мовчки подвоювала рядки й суми. Фільтрований індекс
    /// <c>UX_ReportSnapshot_Current</c> стоїть на версії й цього не ловить.
    /// </para>
    /// </remarks>
    private async Task SwitchCurrentAsync(ReportSnapshot snapshot, int reportDefId, CancellationToken ct)
    {
        var previous = await db.ReportSnapshots
            .Where(s => db.ReportVersions.Any(v => v.Id == s.ReportVersionId && v.ReportDefId == reportDefId)
                        && s.ProjectId == snapshot.ProjectId
                        && s.PeriodKey == snapshot.PeriodKey
                        && s.Id != snapshot.Id
                        && s.IsCurrent)
            .OrderBy(s => s.Id)
            .Take(MaxCurrentSnapshots)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var stale in previous)
        {
            stale.Supersede();
        }

        snapshot.MakeCurrent();
    }

    /// <summary>Стеля на кількість зрізів, з яких знімається поточність.</summary>
    /// <remarks>
    /// Поточний зріз мусить бути рівно один — це тримає фільтрований
    /// унікальний індекс. Більший список означає зіпсовані дані, і межа не дає
    /// такій зіпсованості перетворитися на довгу транзакцію.
    /// </remarks>
    private const int MaxCurrentSnapshots = 100;

    /// <summary>Комірка зрізу.</summary>
    private static ReportRow Cell(
        long snapshotId, int rowNo, string columnCode, string? text, decimal? number)
    {
        var row = new ReportRow(snapshotId, rowNo, columnCode);
        row.SetValue(text, number, null);
        return row;
    }

    /// <summary>
    /// Контрольна сума вмісту зрізу.
    /// </summary>
    /// <remarks>
    /// Рахується за ДАНИМИ у стабільному порядку, а не за часом побудови: два
    /// зрізи з однаковими числами мусять мати однакову суму, інакше нею
    /// неможливо довести, що звіт не змінився.
    /// </remarks>
    /// <param name="rows">Рядки зрізу в порядку побудови.</param>
    public static byte[] ComputeHash(IReadOnlyList<ReportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return HashOf(rows, r => Canonical(r.ValueNumeric));
    }

    /// <summary>
    /// Контрольна сума за форматом ДО BE-17, відтворена зі збережених рядків.
    /// </summary>
    /// <remarks>
    /// ⚠ ТИМЧАСОВО прийнятний формат: приймається для зрізів, побудованих до
    /// BE-17; прибрати, коли таких не лишиться (жоден <c>rpt.ReportSnapshot</c>
    /// не збігається за ним — або всі старі перебудовано).
    /// <para>
    /// Стара сума писала число як <c>decimal.ToString()</c>, тобто з масштабом,
    /// який число мало В МИТЬ ПОБУДОВИ. Масштаб у базі втрачено (усе має
    /// масштаб стовпця: до <c>D-148</c> — 10, тепер — 16), але для рядків, які
    /// складає <c>AggregateAsync</c>, він відновлюється з коду колонки
    /// однозначно: <c>DocumentId</c> і <c>SubstanceEntryId</c> — це
    /// <c>long</c> (масштаб 0, «4217»), а <c>Value</c> читалось із
    /// <c>calc.CalculationResult.Value</c>, ТОДІ колонки <c>decimal(28,10)</c>,
    /// і SqlClient віддавав його з масштабом 10 («12.5000000000»).
    ///
    /// ⚠ Саме тому <see cref="LegacyNumber"/> друкує <c>F10</c> ЛІТЕРАЛОМ і
    /// переходу на 16 знаків не помічає: формат відтворює те, як число
    /// виглядало ДО BE-17, а не те, як воно лежить у стовпці сьогодні.
    /// Порядок комірок той самий, що й тепер.
    /// </para>
    /// <para>
    /// ⛔ Межа: зріз, рядки якого складено НЕ побудовою (інші колонки, число
    /// з іншим масштабом), за старим форматом не відтворюється — і тоді
    /// відповідь лишається «не збігається», бо довести протилежне нема чим.
    /// </para>
    /// </remarks>
    private static byte[] ComputeLegacyHash(IReadOnlyList<ReportRow> rows)
        => HashOf(rows, LegacyNumber);

    /// <summary>Число так, як його друкував код до BE-17 у мить побудови.</summary>
    private static string LegacyNumber(ReportRow row)
    {
        if (row.ValueNumeric is not { } number)
        {
            return string.Empty;
        }

        // ⛔ Ціла форма — лише для справді цілого ідентифікатора. Відкинути
        // дріб беззастережно означало б сховати підміну `4217` → `4217.5`.
        var isId = row.ColumnCode is "DocumentId" or "SubstanceEntryId";

        return isId && number == decimal.Truncate(number)
            ? decimal.Truncate(number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("F10", CultureInfo.InvariantCulture);
    }

    private static byte[] HashOf(IReadOnlyList<ReportRow> rows, Func<ReportRow, string> number)
    {
        var text = string.Join(
            '\n',
            rows.Select(r => string.Create(
                CultureInfo.InvariantCulture,
                $"{r.RowNo}|{r.ColumnCode}|{r.ValueString}|{number(r)}")));

        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Число без хвостових нулів, із точністю колонки значень.</summary>
    /// <remarks>
    /// ⛔ BE-17. <c>decimal</c> у .NET несе МАСШТАБ: <c>5m</c> друкується «5», а
    /// те саме число, прочитане з <c>decimal(34,16)</c>, — «5.0000000000000000». Поки
    /// суму рахували лише при побудові, цього не було видно; перерахунок за
    /// збереженими рядками давав би іншу суму на КОЖНОМУ зрізі з числами, тобто
    /// перевірка завжди казала б «вміст змінено».
    /// <para>
    /// ⚠ Знаків **16**, а не 10. Формат розширено ОКРЕМИМ комітом ПЕРЕД
    /// переходом <c>rpt.ReportRow.ValueNumeric</c> на <c>decimal(34,16)</c>
    /// (міграція <c>D148ReportingAndSourceScale16</c>), і порядок тут — не
    /// смак. Формат
    /// обрізає хвостові нулі, тому для значень із ≤ 10 знаками рядок
    /// ПОБАЙТНО той самий, що й до розширення, і вже збережені суми лишаються
    /// чинними. Розширити ПІСЛЯ колонки означало б вікно, у якому два різні
    /// числа (різниця на 11–16 знаку) дають одну суму — тобто «вміст не
    /// змінювався» там, де він змінився.
    /// </para>
    /// </remarks>
    private static string Canonical(decimal? value)
        => value is { } number
            ? number.ToString("0.################", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Результат розрахунку для агрегації.</summary>
    /// <remarks>
    /// Названий тип, а не анонімний: анонімний розриває вираз фігурною дужкою,
    /// і архітектурне правило «<c>ToListAsync</c> без <c>Take</c>» бачить
    /// половину інструкції без межі (`D1-08`).
    /// </remarks>
    private sealed record ResultRow(
        long DocumentId, string? SourceRowKey, string OutputCode, decimal Value, long? SubstanceEntryId,
        int PeriodKey, int UnitId, string UnitCode, int MethodologyVersionId, string ProjectCode);
}

/// <summary>Опис колонок звіту, що зберігається у <c>ReportVersion.ColumnsJson</c>.</summary>
/// <param name="Code">Код колонки — він же ключ у рядку зрізу.</param>
/// <param name="Kind">Тип значення: <c>text</c>, <c>number</c>, <c>date</c>.</param>
/// <param name="NameL10n">
/// Підписи колонки мовами каталогу (<c>R9</c>); <c>null</c> — опис назв не має,
/// і колонка підписується КОДОМ, як до <c>R9</c>. Читається тим самим іменем
/// поля, яким його пише <see cref="ReportColumnCommand"/>.
/// </param>
public sealed record ReportColumnSpec(
    string Code, string Kind, IReadOnlyDictionary<string, string>? NameL10n = null)
{
    /// <summary>Налаштування розбору; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Читає опис колонок із JSON версії звіту.</summary>
    /// <param name="columnsJson">Вміст <c>ColumnsJson</c>.</param>
    /// <returns>Колонки; порожній перелік, якщо опис зламаний.</returns>
    /// <remarks>
    /// Сам розбір не кидає; що робити з порожнім переліком, вирішує споживач.
    /// Побудова (<c>LayoutOf</c>) за ним відмовляє, перевірка й перегляд рядків
    /// читають зріз як побудований до <c>D-52a</c>.
    /// </remarks>
    public static IReadOnlyList<ReportColumnSpec> Parse(string columnsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ReportColumnSpec>>(columnsJson, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
