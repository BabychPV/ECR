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
    ICalculationTrigger? recalculation = null) : IMaterializeCollectedDataJob
{
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
                    $"Період у стані {state?.ToString() ?? "невідомо"}: пізній збір лишається сирим.", ct)
                .ConfigureAwait(false);

            await progress.ReportKeyAsync(100, "jobs.materializePeriodClosed", ct).ConfigureAwait(false);
            return;
        }

        await progress.ReportKeyAsync(30, "jobs.materializeFolding", ct).ConfigureAwait(false);

        var bounds = Domain.Entities.Documents.Period.UtcBounds(
            period.PeriodStart, period.PeriodEnd, SiteTimeZone.Create(period.TimeZoneId).ToTimeZoneInfo());

        var (aggregated, overCeiling, unitFailures) = await AggregateAsync(task, maps, bounds, ct).ConfigureAwait(false);

        // ⛔ Перевищена стеля — не мовчки: рядок у журнал покриття, як і решта
        // причин «зібрано, але не записано» (`D-118`).
        if (overCeiling.Count > 0)
        {
            await coverage
                .RecordManyAsync(
                    [.. overCeiling.Select(field => new CoverageEvent(
                        task.SourceEntityId, periodKey, PointCeilingStatus,
                        $"Поле {field}: понад {PointCeilingPerField.ToString(CultureInfo.InvariantCulture)} "
                        + "точок за період — значення не записано, бо згортка неповного ряду дала б хибне число."))],
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
            $"Комірка {kept} має правку людини: значення збору не застосовано.")));

        events.AddRange((written.WriteConflicts ?? []).Select(cell => new CoverageEvent(
            task.SourceEntityId, periodKey, CollectionCoverage.SkippedWriteConflict,
            $"Комірка {cell}: рядок змінювали під час запису, повтори вичерпано — "
            + "значення збору не записано; наступний прогін спробує знову.")));

        events.AddRange((written.AwaitingConfirmation ?? []).Select(cell => new CoverageEvent(
            task.SourceEntityId, periodKey, CollectionCoverage.SkippedNeedsConfirmation,
            $"Комірка {cell}: правило періоду вимагає підтвердження людини — "
            + "інтеграція не підтверджує, значення збору не записано.")));

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
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Значення для запису і поля, що перевищили стелю точок.</returns>
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
        MaterializeTask task, List<EntityFieldMap> maps, Domain.Entities.Documents.Period.UtcRange period, CancellationToken ct)
    {
        var result = new List<IntegrationCellValue>(maps.Count);
        var overCeiling = new List<string>();
        var unitFailures = new List<string>();
        var ceiling = PointCeilingPerField;
        UnitCatalogSnapshot? units = null;

        foreach (var field in maps.Select(m => m.SourceField).Distinct(StringComparer.Ordinal))
        {
            var fieldMaps = maps.Where(m => string.Equals(m.SourceField, field, StringComparison.Ordinal)).ToList();

            // ⚠ Згортка за часом бачить і точки без числа (погана якість, текст):
            // вони роблять прогалиною відрізки, що на них спираються. Згортки
            // точок їх не бачать — як і до F3, тож і стеля для них та сама.
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
                .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (inside.Count > ceiling)
            {
                overCeiling.Add(field);
                continue;
            }

            // Згортки точок — рівно ті самі числа, що й до F3: лише точки з
            // числом, без урахування якості (`PeriodFold`, згортки точок).
            var points = inside
                .Where(p => p.Value is not null)
                .Select(p => new TimedPoint(p.Timestamp, p.Value!.Value))
                .ToList();

            // ⛔ HSE301 §4.1: значення на межах періоду інтерполюються з останньої
            // точки ДО нього й першої НА чи ПІСЛЯ кінця. Без них інтеграл місяця,
            // де стиснення PI не лишило точок біля опівночі, недораховував би
            // краї — а подія без точок усередині дала б нуль замість об'єму.
            var timed = needsTime
                ? await TimeSeriesAsync(task.SourceEntityId, field, period, inside, ct).ConfigureAwait(false)
                : [];

            foreach (var map in fieldMaps)
            {
                // ⚠ `Aggregation` не може бути null: пара «рядок + агрегація»
                // нерозривна і на рівні домену, і обмеженням у базі.
                var kind = map.Aggregation
                    ?? throw new InvalidOperationException(
                        $"Мапінг {map.Id} не називає способу згортання: конфігурація неповна.");

                var series = IsTimeFold(kind) ? timed : points;
                if (series.Count == 0)
                {
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
                    continue;
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

                result.Add(new IntegrationCellValue(map.TargetRowKey!, map.TargetColumnDefId!.Value, boundary.Value));
            }
        }

        return new Aggregation(result, overCeiling, unitFailures);
    }

    /// <summary>Ряд для згортки за часом: точка до періоду, точки періоду, точка на чи після кінця.</summary>
    /// <remarks>
    /// ⚠ Межові точки беруться без фільтра якості: погана межова точка робить
    /// крайній відрізок прогалиною, і це правильніше, ніж перескочити через неї
    /// до ще давнішої.
    /// </remarks>
    private async Task<List<TimedPoint>> TimeSeriesAsync(
        int sourceEntityId, string field, Domain.Entities.Documents.Period.UtcRange period,
        List<PointRow> inside, CancellationToken ct)
    {
        var before = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId && p.SourcePath == field && p.Timestamp < period.StartUtc)
            .OrderByDescending(p => p.Timestamp)
            .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var after = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId && p.SourcePath == field && p.Timestamp >= period.EndUtc)
            .OrderBy(p => p.Timestamp)
            .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var series = new List<TimedPoint>(inside.Count + 2);
        if (before is not null)
        {
            series.Add(before.ToTimed());
        }

        series.AddRange(inside.Select(p => p.ToTimed()));

        if (after is not null)
        {
            series.Add(after.ToTimed());
        }

        return series;
    }

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
    private sealed record PointRow(DateTime Timestamp, decimal? Value, string? Quality)
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

    /// <summary>Результат згортки періоду.</summary>
    /// <param name="Values">Значення для запису.</param>
    /// <param name="OverCeiling">Поля, що перевищили стелю точок.</param>
    /// <param name="UnitFailures">Мапінги, чиє значення не переводиться в цільову одиницю.</param>
    private sealed record Aggregation(
        IReadOnlyList<IntegrationCellValue> Values,
        IReadOnlyList<string> OverCeiling,
        IReadOnlyList<string> UnitFailures);
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
