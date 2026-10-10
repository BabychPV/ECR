// src/Ecr.Infrastructure/Jobs/MaterializeCollectedDataJob.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Переносить зібрані точки <c>ext.RawDataPoint</c> у комірки документів
/// (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Запис іде через <b>той самий</b> <c>PatchCellsHandler</c>, що й правка
/// людини, під технічним записом <c>svc-integration</c>. Окремий «швидкий»
/// шлях запису не заводиться навмисно: другий шлях запису в комірки і є те,
/// що дало `A7-27` — там мапа колонок будувалася інакше, ніж на основному
/// шляху, і адресація розійшлася.
///
/// ⚠ Задача окрема і ставиться ПІСЛЯ успішного збору інтервалу — не при
/// відкритті документа (бюджет 400 мс) і не при поданні (запізно).
///
/// ⚠ Правила з `D-118`, і кожне з них про те, щоб не втратити дані мовчки:
/// <list type="table">
/// <item><term>період <c>Open</c>/<c>Grace</c></term><description>записати</description></item>
/// <item><term>період <c>Scheduled</c></term><description><b>не</b> писати і <b>не</b> журналювати: ще не відкритий, точки згорне перший прогін після відкриття</description></item>
/// <item><term>період закритий</term><description><b>не</b> писати; рядок у журналі покриття зі статусом <c>SkippedPeriodClosed</c></description></item>
/// <item><term>комірка правлена людиною</term><description><b>не</b> перезаписувати; <c>ConflictKeptManual</c></description></item>
/// <item><term>рядок змінювали під час запису, повтори вичерпано</term><description><c>SkippedWriteConflict</c></description></item>
/// <item><term>правило періоду вимагає підтвердження</term><description><b>не</b> писати; <c>SkippedNeedsConfirmation</c></description></item>
/// <item><term>комірка порожня або від інтеграції</term><description>записати</description></item>
/// <item><term>період скінчився, згортка за часом покрила менше <see cref="MinPercentGood"/></term><description>записати (HU-13 Q2, варіант A) і подія <c>PartialCoverage</c> з часткою покриття</description></item>
/// <item><term>період скінчився, придатних точок поля немає</term><description><b>не</b> писати і <b>не</b> очищати; подія <c>SkippedNoData</c></description></item>
/// </list>
///
/// ⚠ HSE301 A4: записане (<c>Applied &gt; 0</c>) ставить автоперерахунок документа
/// за період (<see cref="ICalculationTrigger"/>) — один раз на прогін, у кінці.
/// <c>recalculation</c> необов'язковий лише для прямого конструювання в тестах
/// запису; контейнер його завжди передає (<c>// HSE301:A4</c> у <c>DependencyInjection</c>).
/// </remarks>
public sealed class MaterializeCollectedDataJob(
    EcrDbContext db,
    ICellPatcher patcher,
    ICoverageJournal coverage,
    IntegrationActor actor,
    ICalculationTrigger? recalculation = null,
    IClock? clock = null) : IMaterializeCollectedDataJob
{
    /// <summary>
    /// Поріг частки покриття згортки за часом, нижче якого значення неповне
    /// (HSE301 §4.1/§4.4: «частка покриття… дає статус <c>Partial</c>»).
    /// </summary>
    /// <remarks>
    /// ⚠ Той самий дефолт, що <c>ext.RowWindowMap.MinPercentGood</c> (95): окремої
    /// колонки порогу в мапінгу поля немає, а міграцію заради неї ця правка не робить.
    /// </remarks>
    public const decimal MinPercentGood = 95m;

    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "materialize-collected";

    /// <summary>
    /// Стеля точок на одне поле за період.
    /// </summary>
    /// <remarks>
    /// Півмільйона на поле — це вже не «період збору», а наслідок помилки
    /// конфігурації; переносити їх усі означало б покласти запис документів на
    /// час, коли з ними працюють.
    /// <para>
    /// ⛔ D16-03: стеля — на ПОЛЕ, не на сутність, і досягнута стеля НЕ обрізає
    /// хвіст мовчки. Раніше спільний <c>Take</c> за зростанням часу відрізав
    /// останні точки: <c>Last</c> брав не останню, <c>Sum</c> був занижений, і
    /// ніде про це не лишалося сліду. Тепер поле, що перевищило стелю, у комірку
    /// не пишеться зовсім (часткова сума — хибне число, а не «приблизне»), а в
    /// журналі покриття з'являється рядок <see cref="PointCeilingStatus"/>.
    /// </para>
    /// </remarks>
    public const int MaxPoints = 500_000;

    /// <summary>Статус рядка журналу покриття, коли поле перевищило стелю точок.</summary>
    public const string PointCeilingStatus = CollectionCoverage.SkippedPointCeiling;

    /// <summary>Стеля точок на поле; змінюється лише тестами.</summary>
    /// <remarks>
    /// ⚠ Властивість, а не параметр конструктора: контейнер створює задачу за
    /// конструктором, і примітив у ньому або не розв'язався б, або вимагав би
    /// реєстрації заради тестів.
    /// </remarks>
    public int PointCeilingPerField { get; init; } = MaxPoints;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var task = MaterializePayload.Parse(payload);
        var periodKey = new PeriodKey(task.PeriodKey);

        // ⛔ P0: задача пише ВІД ІМЕНІ `svc-integration` (`IntegrationActor`).
        // Без цього входу `PatchCellsHandler` у фоні бачив анонімного
        // користувача (HTTP-запиту немає) і відмовляв `ECR-AUTH-0401` — у
        // проді матеріалізація не записувала жодної комірки.
        using var author = await actor.EnterAsync(ct).ConfigureAwait(false);

        await progress.ReportKeyAsync(10, "jobs.materializeReadingMappings", ct).ConfigureAwait(false);

        // ⚠ Беруться лише МАТЕРІАЛІЗОВАНІ мапінги: `TargetRowKey IS NULL`
        // означає «точки лишаються сирими для звірки», і це легальний стан.
        //
        // ⛔ Суміжне D16-03: лише мапінги ТАБЛИЦІ цього екземпляра. Сутність
        // може живити кілька таблиць, і кожна отримує власну задачу
        // (`CollectionJob`); без звуження задача таблиці A несла й колонки
        // таблиці B, патчер повертав їх у `KeptManual`, і журнал покриття
        // щопрогону отримував хибний `ConflictKeptManual` «правка людини».
        // Видалені колонки своєї таблиці НЕ відсіюються тут навмисно: це
        // справжня помилка конфігурації, і патчер про неї звітує.
        var instanceTable = db.TableInstances
            .Where(t => t.Id == task.TableInstanceId && t.PeriodKeyValue == task.PeriodKey)
            .Select(t => t.TableDefId);

        var maps = await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.SourceEntityId == task.SourceEntityId
                        && m.IsActive
                        && m.TargetRowKey != null
                        && m.TargetColumnDefId != null
                        && db.ColumnDefs.Any(c => c.Id == m.TargetColumnDefId
                                                  && instanceTable.Contains(c.TableDefId)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (maps.Count == 0)
        {
            await progress.ReportKeyAsync(100, "jobs.materializeNoMappings", ct).ConfigureAwait(false);
            return;
        }

        // ⛔ Стан періоду перевіряється ОДИН раз і до роботи. Пізній збір за
        // закритий період не втрачається тихо: він лишається сирим, а в
        // журналі покриття з'являється причина.
        //
        // ⛔ D16-03: разом зі станом читаються МЕЖІ періоду і пояс проєкту —
        // згортка йде за періодом екземпляра, а не за вікном збору.
        var period = await (
                from p in db.Periods.AsNoTracking()
                join project in db.Projects.AsNoTracking() on p.ProjectId equals project.Id
                where p.ProjectId == task.ProjectId && p.PeriodKeyValue == task.PeriodKey
                select new { p.State, p.PeriodStart, p.PeriodEnd, project.TimeZoneId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var state = period?.State;

        // ⛔ Суміжне D16-03: `Scheduled` — не «закритий». Період ще не відкрито
        // (`OpenOffsetDays > 0` або задача станів ще не пройшла), збір не
        // пізній, і нічого не втрачається: точки лежать у `ext.RawDataPoint`,
        // а згортка йде за межами ПЕРІОДУ, тож перший прогін після відкриття
        // згорне їх усі. Тут стояв `SkippedPeriodClosed` «пізній збір
        // лишається сирим» — хибний рядок у журналі покриття щопрогону.
        if (state is PeriodState.Scheduled)
        {
            await progress
                .ReportKeyAsync(
                    100,
                    "jobs.materializeDone",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["applied"] = "0",
                        ["keptManual"] = "0",
                        ["overCeiling"] = "0",
                    },
                    ct)
                .ConfigureAwait(false);
            return;
        }

        if (period is null || state is not (PeriodState.Open or PeriodState.Grace))
        {
            await coverage
                .RecordAsync(task.SourceEntityId, periodKey, CollectionCoverage.SkippedPeriodClosed,
                    CoverageDetails.PeriodNotOpen(state), ct)
                .ConfigureAwait(false);

            await progress.ReportKeyAsync(100, "jobs.materializePeriodClosed", ct).ConfigureAwait(false);
            return;
        }

        await progress.ReportKeyAsync(30, "jobs.materializeFolding", ct).ConfigureAwait(false);

        var bounds = Domain.Entities.Documents.Period.UtcBounds(
            period.PeriodStart, period.PeriodEnd, SiteTimeZone.Create(period.TimeZoneId).ToTimeZoneInfo());

        // ⛔ D2-02: неповне покриття й «немає даних» сигналізуються лише ПІСЛЯ
        // кінця періоду — у відкритому місяці хвіст без точок природний, і подія
        // щопрогону була б шумом. Без годинника (пряме конструювання в старих
        // тестах) кінець невідомий — подій немає; контейнер годинник завжди дає.
        var ended = clock is not null && clock.UtcNow >= bounds.EndUtc;

        var (aggregated, overCeiling, unitFailures, partial, noData) =
            await AggregateAsync(task, maps, bounds, ended, ct).ConfigureAwait(false);

        // ⛔ D2-02 (HU-13 Q2, варіант A): часткове число ЗАПИСАНО, але не мовчки —
        // подія з часткою покриття; поле без придатних точок НЕ записано й не
        // очищено (HSE301 §4.1: «даних не було» ≠ 0) — подія, що в комірці може
        // лишатися значення попереднього прогону.
        if (partial.Count > 0 || noData.Count > 0)
        {
            await coverage
                .RecordManyAsync(
                    [
                        .. partial.Select(p => new CoverageEvent(
                            task.SourceEntityId, periodKey, CollectionCoverage.PartialCoverage,
                            CoverageDetails.PartialCoverage(p.Field, p.MapId, p.PercentGood, MinPercentGood))),
                        .. noData.Select(n => new CoverageEvent(
                            task.SourceEntityId, periodKey, CollectionCoverage.SkippedNoData,
                            CoverageDetails.NoData(n.Field, n.MapId))),
                    ],
                    ct)
                .ConfigureAwait(false);
        }

        // ⛔ Перевищена стеля — не мовчки: рядок у журнал покриття, як і решта
        // причин «зібрано, але не записано» (`D-118`).
        if (overCeiling.Count > 0)
        {
            await coverage
                .RecordManyAsync(
                    [.. overCeiling.Select(field => new CoverageEvent(
                        task.SourceEntityId, periodKey, PointCeilingStatus,
                        CoverageDetails.PointCeiling(field, PointCeilingPerField)))],
                    ct)
                .ConfigureAwait(false);
        }

        if (aggregated.Count == 0)
        {
            ThrowIfUnitFailures(unitFailures);
            await progress.ReportKeyAsync(100, "jobs.materializeNoPoints", ct).ConfigureAwait(false);
            return;
        }

        await progress.ReportKeyAsync(60, "jobs.materializeWriting", ct).ConfigureAwait(false);

        var written = await patcher
            .ApplyIntegrationAsync(task.DocumentId, task.TableInstanceId, periodKey, aggregated, ct)
            .ConfigureAwait(false);

        // ⛔ Конфлікт із правкою людини НЕ мовчазний: людина виправила навмисно,
        // і інтеграція не має права це стерти — але й приховати факт теж.
        //
        // ⛔ Q-170 (аудит фази 2, продуктивність): ОДИН пакетний запис на всі
        // конфлікти замість `RecordAsync` (власний `SaveChangesAsync`) у
        // циклі на кожен.
        //
        // ⛔ Три причини «не записано» — три РІЗНІ статуси. Доти вичерпані
        // повтори і комірки під підтвердженням теж ішли як `ConflictKeptManual`
        // «має правку людини» — неправда в журналі, людина їх не правила; до
        // того ж `ConflictKeptManual` свідомо поза зведенням сповіщень, тож
        // незаписані дані губилися й там.
        // • `ConflictKeptManual` — лише справжня правка людини; дії не треба.
        // • `SkippedWriteConflict` — рядок змінювали під час запису, повтори
        //   вичерпано; наступний прогін спробує знову.
        // • `SkippedNeedsConfirmation` — правило періоду `AllowWithConfirmation`
        //   (`ФВ-2.16`): підтвердження — дія людини, інтеграція його не дає.
        var events = new List<CoverageEvent>();

        events.AddRange(written.KeptManual.Select(kept => new CoverageEvent(
            task.SourceEntityId, periodKey, CollectionCoverage.ConflictKeptManual,
            CoverageDetails.KeptManual(kept))));

        events.AddRange((written.WriteConflicts ?? []).Select(cell => new CoverageEvent(
            task.SourceEntityId, periodKey, CollectionCoverage.SkippedWriteConflict,
            CoverageDetails.WriteConflict(cell))));

        events.AddRange((written.AwaitingConfirmation ?? []).Select(cell => new CoverageEvent(
            task.SourceEntityId, periodKey, CollectionCoverage.SkippedNeedsConfirmation,
            CoverageDetails.NeedsConfirmation(cell))));

        if (events.Count > 0)
        {
            await coverage.RecordManyAsync(events, ct).ConfigureAwait(false);
        }

        // ⛔ HSE301 A4 (V-5): нові числа в комірках — перерахунок методологій.
        // ОДИН виклик на прогін, після всього запису, і ДО відмови за одиницями:
        // записані поля вже в документі, і відмова задачі за іншим мапінгом не
        // скасовує того, що їх треба перерахувати. Нуль записаного — нуль задач.
        // Закритий період сюди не доходить (гілка стану вище), а тригер
        // перевіряє правило вдруге (`RecalculationWritePolicy`).
        if (written.Applied > 0 && recalculation is not null)
        {
            await recalculation.RequestAsync(task.DocumentId, periodKey, ct).ConfigureAwait(false);
        }

        // ⛔ Після запису решти: несумісна одиниця одного мапінгу не зупиняє
        // перенесення інших полів, але й не губиться — задача завершується
        // відмовою з переліком (журнал задач, сповіщення `JobFailed`).
        ThrowIfUnitFailures(unitFailures);

        await progress
            .ReportKeyAsync(
                100,
                "jobs.materializeDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["applied"] = written.Applied.ToString(CultureInfo.InvariantCulture),
                    ["keptManual"] = written.KeptManual.Count.ToString(CultureInfo.InvariantCulture),
                    ["overCeiling"] = overCeiling.Count.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Відмова задачі, якщо значення хоч одного мапінгу не переводиться в цільову одиницю.</summary>
    /// <param name="unitFailures">Опис кожного такого мапінгу.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-UOM-0422</c> <c>boundaryConversionFailed</c>.</exception>
    private static void ThrowIfUnitFailures(IReadOnlyList<string> unitFailures)
    {
        if (unitFailures.Count == 0)
        {
            return;
        }

        var list = string.Join("; ", unitFailures);
        var details = list.Length > CollectionCoverage.MaxDetailsLength ? list[..CollectionCoverage.MaxDetailsLength] : list;

        throw new BusinessRuleException(
            ErrorCodes.UnitDimensionMismatch,
            $"Значення {unitFailures.Count.ToString(CultureInfo.InvariantCulture)} мапінгів не переведено "
            + $"в цільову одиницю й не записано: {details}",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-UOM-0422.boundaryConversionFailed",
                ["count"] = unitFailures.Count.ToString(CultureInfo.InvariantCulture),
                ["fields"] = details,
            });
    }

    /// <summary>Згортає сирі точки ПЕРІОДУ екземпляра в одне значення на мапінг.</summary>
    /// <param name="task">Завдання.</param>
    /// <param name="maps">Матеріалізовані мапінги сутності.</param>
    /// <param name="period">Межі періоду екземпляра в UTC, <c>[початок, кінець)</c>.</param>
    /// <param name="ended">Період уже скінчився: неповне покриття й відсутність даних — події (D2-02).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Значення для запису, поля понад стелю точок, неповне покриття і поля без даних.</returns>
    /// <remarks>
    /// ⛔ D16-03: точки беруться за межами ПЕРІОДУ — усі, що є в
    /// <c>ext.RawDataPoint</c>, — а не за вікном збору <c>task.FromUtc/ToUtc</c>.
    /// Вікно лише вирішує, які періоди зачеплено (<c>CollectionJob</c>). Згортка
    /// за вікном давала «Sum за місяць» = сума останніх 7 діб, одне й те саме
    /// число в кожен відкритий період і точки сусіднього місяця через межу.
    ///
    /// ⚠ Окремий запит на кожне поле: стеля — на поле, і спільний <c>Take</c>
    /// на всі поля віддав би один щільний тег за рахунок решти.
    /// </remarks>
    private async Task<Aggregation> AggregateAsync(
        MaterializeTask task, List<EntityFieldMap> maps, Domain.Entities.Documents.Period.UtcRange period, bool ended,
        CancellationToken ct)
    {
        var result = new List<IntegrationCellValue>(maps.Count);
        var partial = new List<PartialField>();
        var noData = new List<NoDataField>();
        var overCeiling = new List<string>();
        var unitFailures = new List<string>();
        var ceiling = PointCeilingPerField;
        UnitCatalogSnapshot? units = null;

        foreach (var field in maps.Select(m => m.SourceField).Distinct(StringComparer.Ordinal))
        {
            var fieldMaps = maps.Where(m => string.Equals(m.SourceField, field, StringComparison.Ordinal)).ToList();

            // ⚠ Згортка за часом бачить і точки без числа (погана якість, текст):
            // вони роблять прогалиною відрізки, що на них спираються. Згортки
            // точок бачать лише точки з числом (непридатні за якістю відсіює
            // `PeriodFold`), тож і стеля для них та сама.
            var needsTime = fieldMaps.Any(m => IsTimeFold(m.Aggregation));

            // ⚠ `Take(ceiling + 1)`: зайва точка — єдиний дешевий спосіб знати,
            // що ряд ДОВШИЙ за стелю, а не рівно такий.
            var inside = await db.RawDataPoints
                .AsNoTracking()
                .Where(p => p.SourceEntityId == task.SourceEntityId
                            && p.SourcePath == field
                            && p.Timestamp >= period.StartUtc
                            && p.Timestamp < period.EndUtc
                            && (needsTime || p.ValueNumeric != null))
                .OrderBy(p => p.Timestamp)
                .Take(ceiling + 1)
                .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality, p.UnitId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (inside.Count > ceiling)
            {
                overCeiling.Add(field);
                continue;
            }

            // Згортки точок — лише точки з числом. Якість несе `IsGood`, і
            // `PeriodFold` відсіює непридатні (HSE301 §4.6, C1-04) так само, як
            // вікно рядка (`WindowFold`); частка відсіяних — у `PartialCoverage`.
            var points = inside
                .Where(p => p.Value is not null)
                .Select(p => p.ToTimed())
                .ToList();

            // ⛔ HSE301 §4.1: значення на межах періоду інтерполюються з останньої
            // точки ДО нього й першої НА чи ПІСЛЯ кінця. Без них інтеграл місяця,
            // де стиснення PI не лишило точок біля опівночі, недораховував би
            // краї — а подія без точок усередині дала б нуль замість об'єму.
            var bounds = needsTime
                ? await BoundsAsync(task.SourceEntityId, field, period, ct).ConfigureAwait(false)
                : default;

            // ⛔ Y1-01: межова точка старої одиниці джерела переводиться в оголошену (нижче, `Edge`) — для
            // цього потрібен довідник, навіть коли самій згортці він не потрібен.
            if (fieldMaps.Any(m => IsTimeFold(m.Aggregation) && HasForeignEdge(bounds, m.SourceUnitId)))
            {
                units ??= await new UnitCatalog(db).GetAsync(ct).ConfigureAwait(false);
            }

            foreach (var map in fieldMaps)
            {
                // ⚠ `Aggregation` не може бути null: пара «рядок + агрегація»
                // нерозривна і на рівні домену, і обмеженням у базі.
                var kind = map.Aggregation
                    ?? throw new InvalidOperationException(
                        $"Мапінг {map.Id} не називає способу згортання: конфігурація неповна.");

                // ⛔ X3-02 / ФВ-16.10, D-79: точка несе СВОЮ одиницю джерела. Після прийняття нової одиниці
                // (ФВ-16.9, `AcceptSourceUnitChange`) у періоді можуть лежати точки старої — переведення
                // всього ряду за ПОТОЧНОЮ одиницею мапінгу дало б тихий ×24 (`Sm3/h` → `Sm3/d`). Змішаний
                // ряд не згортається: комірка не пишеться, задача відмовляє з переліком (той самий канал,
                // що й несумісна одиниця). Точка без одиниці (`UnitId = null`) — «нема з чим порівняти»,
                // як і в `BoundaryUnitConversion.IsDeclaredUnit`.
                //
                // ⛔ Y1-01: відмова — лише за точки ВСЕРЕДИНІ періоду. Межові точки (до початку й на/після
                // кінця) лежать у СУСІДНЬОМУ періоді: збір «з початку періоду», який радить відмова, їх не
                // перечитує (PI `recorded` без `boundaryType` — `Inside`), і відмова через них не лікувалась
                // ніколи. Тому межову точку в іншій одиниці переводимо в оголошену (`Edge`).
                if (HasForeignUnit(inside, map.SourceUnitId))
                {
                    unitFailures.Add(
                        $"{field} (мапінг {map.Id.ToString(CultureInfo.InvariantCulture)}): у періоді є точки в іншій "
                        + "одиниці джерела, ніж оголошує мапінг; зберіть період заново (ручний збір з початку періоду).");
                    continue;
                }

                var series = IsTimeFold(kind)
                    ? TimeSeries(bounds, inside, map.SourceUnitId, units)
                    : points;
                if (series.Count == 0)
                {
                    // ⛔ D2-02: не мовчки — після кінця періоду подія «немає даних».
                    if (ended)
                    {
                        noData.Add(new NoDataField(field, map.Id));
                    }

                    continue;
                }

                // ⛔ Згортка винесена в `PeriodFold` і НЕ дублюється: другий її
                // споживач — попередній перегляд мапінгу (`ФВ-13.14`), і власна
                // копія там показувала б число, якого ця задача не запише.
                //
                // ⚠ `maxGap: null` — порогу прогалини в конфігурації поки немає
                // (HSE301 §4.1: число обирає викликач із конфігурації, не з коду).
                var folded = PeriodFold.Fold(kind, series, period.StartUtc, period.EndUtc, map.IsStep, maxGap: null);
                if (folded.Value is not { } value)
                {
                    // `covered == 0` для згортки за часом / жодної придатної точки
                    // всередині для згортки точок: комірку НЕ чіпаємо (див. подію).
                    if (ended)
                    {
                        noData.Add(new NoDataField(field, map.Id));
                    }

                    continue;
                }

                // ⛔ D2-02: число ЗАПИСУЄТЬСЯ (HU-13 Q2, варіант A), але з подією
                // `PartialCoverage` і часткою покриття — інтеграл лише покритих
                // відрізків менший за справжній об'єм, і це має бути видно.
                // Для згорток точок частка — придатні серед точок періоду
                // (C1-04): відсіяні Bad/Questionable не зникають мовчки.
                if (ended && folded.PercentGood is { } good && good < MinPercentGood)
                {
                    partial.Add(new PartialField(field, map.Id, good));
                }

                // ⛔ ФВ-16.10, HSE301 §4.2: конверсія ПІСЛЯ згортки, одна арифметика
                // на всю межу (`BoundaryUnitConversion`). Сирі точки лишаються в
                // одиниці джерела (ФВ-11.7).
                BoundaryValue boundary;
                try
                {
                    units ??= NeedsCatalog(kind, map)
                        ? await new UnitCatalog(db).GetAsync(ct).ConfigureAwait(false)
                        : null;

                    boundary = units is null
                        ? BoundaryValue.Unchanged(value)
                        : BoundaryUnitConversion.ConvertFolded(kind, value, map.SourceUnitId, map.TargetUnitId, units);
                }
                catch (Exception ex) when (ex is DomainException or EcrException)
                {
                    // ⛔ Несумісні одиниці (`Sm3` ↔ `m3`) — НЕ тихе число: комірка
                    // не пишеться, а задача після запису решти полів відмовляє.
                    unitFailures.Add($"{field} (мапінг {map.Id.ToString(CultureInfo.InvariantCulture)}): {ex.Message}");
                    continue;
                }

                // ⛔ Z1-01: до масштабу сховища — хвіст ділення (Avg, integral/covered, ÷3600)
                // інакше відхиляє ВЕСЬ батч сутності (`ECR-CELL-0422 tooManyDecimals`).
                result.Add(new IntegrationCellValue(map.TargetRowKey!, map.TargetColumnDefId!.Value, boundary.Storable));
            }
        }

        return new Aggregation(result, overCeiling, unitFailures, partial, noData);
    }

    /// <summary>Межові точки для згортки за часом: остання ДО періоду й перша НА чи ПІСЛЯ кінця.</summary>
    /// <remarks>
    /// ⚠ Межові точки беруться без фільтра якості: погана межова точка робить
    /// крайній відрізок прогалиною, і це правильніше, ніж перескочити через неї
    /// до ще давнішої.
    /// </remarks>
    private async Task<Bounds> BoundsAsync(
        int sourceEntityId, string field, Domain.Entities.Documents.Period.UtcRange period, CancellationToken ct)
    {
        var before = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId && p.SourcePath == field && p.Timestamp < period.StartUtc)
            .OrderByDescending(p => p.Timestamp)
            .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality, p.UnitId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var after = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId && p.SourcePath == field && p.Timestamp >= period.EndUtc)
            .OrderBy(p => p.Timestamp)
            .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality, p.UnitId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new Bounds(before, after);
    }

    /// <summary>Ряд для згортки за часом: межа до періоду, точки періоду, межа на чи після кінця.</summary>
    private static List<TimedPoint> TimeSeries(
        Bounds bounds, List<PointRow> inside, int? declaredSourceUnitId, UnitCatalogSnapshot? units)
    {
        var series = new List<TimedPoint>(inside.Count + 2);
        if (bounds.Before is { } before)
        {
            series.Add(Edge(before, declaredSourceUnitId, units).ToTimed());
        }

        series.AddRange(inside.Select(p => p.ToTimed()));

        if (bounds.After is { } after)
        {
            series.Add(Edge(after, declaredSourceUnitId, units).ToTimed());
        }

        return series;
    }

    /// <summary>Межова точка в оголошеній одиниці джерела (Y1-01).</summary>
    /// <remarks>
    /// <para>
    /// ⛔ Точка несе СВОЮ одиницю (X3-02): межова точка сусіднього періоду, зібрана до прийняття нової
    /// одиниці, переводиться в оголошену мапінгом тією ж арифметикою межі (<see cref="BoundaryUnitConversion"/>) —
    /// це миттєве значення швидкості, а не згортка, тож переводиться як є (<c>Sm3/s</c> → <c>Sm3/h</c> = ×3600).
    /// </para>
    /// <para>
    /// ⛔ Не переводиться (інша розмірність, одиниці немає в довіднику) — точка стає БЕЗ числа: крайній
    /// відрізок — прогалина, а не тихе число в чужій одиниці; покриття (<c>PercentGood</c>,
    /// <c>PartialCoverage</c>) про це скаже.
    /// </para>
    /// </remarks>
    private static PointRow Edge(PointRow point, int? declaredSourceUnitId, UnitCatalogSnapshot? units)
    {
        if (point.UnitId is not { } actual || declaredSourceUnitId is not { } declared || actual == declared)
        {
            return point;
        }

        if (point.Value is not { } value || units is null)
        {
            return point with { Value = null };
        }

        try
        {
            return point with { Value = BoundaryUnitConversion.Convert(value, actual, declared, units) };
        }
        catch (Exception ex) when (ex is DomainException or EcrException)
        {
            return point with { Value = null };
        }
    }

    /// <summary>Чи є межова точка з одиницею джерела, іншою за оголошену (Y1-01).</summary>
    private static bool HasForeignEdge(Bounds bounds, int? declaredSourceUnitId)
        => declaredSourceUnitId is { } declared
           && ((bounds.Before?.UnitId is { } b && b != declared) || (bounds.After?.UnitId is { } a && a != declared));

    /// <summary>Чи є в ряді точка з одиницею джерела, іншою за оголошену мапінгом (X3-02).</summary>
    /// <param name="rows">Точки, які підуть у згортку.</param>
    /// <param name="declaredSourceUnitId">Одиниця джерела з мапінгу; <c>null</c> — не оголошена, звіряти нема з чим.</param>
    private static bool HasForeignUnit(IReadOnlyList<PointRow> rows, int? declaredSourceUnitId)
        => declaredSourceUnitId is { } declared
           && rows.Any(p => p.UnitId is { } unit && unit != declared);

    /// <summary>Чи згортка за часом (потребує міток часу й меж періоду).</summary>
    private static bool IsTimeFold(AggregationKind? kind)
        => kind is AggregationKind.TimeWeightedAvg or AggregationKind.TimeIntegral;

    /// <summary>Чи потрібен довідник одиниць: інтеграл або дві різні оголошені одиниці.</summary>
    /// <remarks>
    /// ⚠ Без цього кожен прогін читав би довідник, навіть коли одиниць у
    /// мапінгах немає, — а таких мапінгів сьогодні більшість.
    /// </remarks>
    private static bool NeedsCatalog(AggregationKind kind, EntityFieldMap map)
        => kind == AggregationKind.TimeIntegral
           || (map.SourceUnitId is { } from && map.TargetUnitId is { } to && from != to);

    /// <summary>Сира точка періоду, як її бачить згортка.</summary>
    /// <remarks>
    /// Названий тип, а не анонімний: архітектурне правило «<c>ToListAsync</c>
    /// без <c>Take</c>» читає інструкцію цілком (`D1-08`).
    /// </remarks>
    private sealed record PointRow(DateTime Timestamp, decimal? Value, string? Quality, int? UnitId)
    {
        /// <summary>
        /// Точка для згортки за часом: придатна, лише якщо має число і якість
        /// <see cref="WindowFold.GoodQuality"/> (або не вказана) — те саме
        /// тлумачення, що й у вікна рядка (HSE301 §4.6).
        /// </summary>
        public TimedPoint ToTimed()
            => new(
                Timestamp,
                Value ?? 0m,
                Value is not null
                && (Quality is null || string.Equals(Quality, WindowFold.GoodQuality, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Межові точки періоду для згортки за часом; <c>null</c> — точки за межею немає.</summary>
    /// <param name="Before">Остання точка до початку періоду.</param>
    /// <param name="After">Перша точка на чи після кінця періоду.</param>
    private readonly record struct Bounds(PointRow? Before, PointRow? After);

    /// <summary>Результат згортки періоду.</summary>
    /// <param name="Values">Значення для запису.</param>
    /// <param name="OverCeiling">Поля, що перевищили стелю точок.</param>
    /// <param name="UnitFailures">Мапінги, чиє значення не переводиться в цільову одиницю.</param>
    /// <param name="Partial">Записані значення з покриттям нижче <see cref="MinPercentGood"/>.</param>
    /// <param name="NoData">Мапінги без придатних точок у періоді — не записано.</param>
    private sealed record Aggregation(
        IReadOnlyList<IntegrationCellValue> Values,
        IReadOnlyList<string> OverCeiling,
        IReadOnlyList<string> UnitFailures,
        IReadOnlyList<PartialField> Partial,
        IReadOnlyList<NoDataField> NoData);

    /// <summary>Мапінг, записаний із неповним покриттям.</summary>
    private sealed record PartialField(string Field, int MapId, decimal PercentGood);

    /// <summary>Мапінг без придатних точок у періоді.</summary>
    private sealed record NoDataField(string Field, int MapId);
}

/// <summary>Розбір завдання матеріалізації.</summary>
internal static class MaterializePayload
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги.</summary>
    public static MaterializeTask Parse(object? payload)
    {
        if (payload is MaterializeTask typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        return System.Text.Json.JsonSerializer.Deserialize<MaterializeTask>(json, Options)
               ?? throw new InvalidOperationException(
                   "Завдання матеріалізації не розбирається: невідома форма payload.");
    }
}

/// <summary>Завдання перенесення точок у комірки.</summary>
/// <param name="SourceEntityId">Сутність джерела, з якої зібрано.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="FromUtc">Початок інтервалу збору (довідково: згортка йде за межами періоду, D16-03).</param>
/// <param name="ToUtc">Кінець інтервалу збору, виключно (довідково, як і <c>FromUtc</c>).</param>
public sealed record MaterializeTask(
    int SourceEntityId,
    int ProjectId,
    long DocumentId,
    long TableInstanceId,
    int PeriodKey,
    DateTime FromUtc,
    DateTime ToUtc);
