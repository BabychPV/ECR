// src/Ecr.Application/Sources/MappingPreviewHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

/// <summary>
/// Попередній перегляд мапінгу на реальних рядках джерела (<c>ФВ-13.14</c>).
/// Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Цінність перегляду — не в списку успішних зв'язків. Екран, який показує
/// лише те, що зійшлося, відповідає на питання, якого ніхто не ставить: коли
/// мапінг справний, у нього не заглядають. Тому відповідь несе **три
/// розриви** нарівні з результатом:
/// <list type="number">
/// <item><description>поле джерела, яке нікуди не лягає
/// (<see cref="MappingOutcome.Unmapped"/>);</description></item>
/// <item><description>мапінг, під який у джерелі немає жодного рядка
/// (<see cref="MappingOutcome.NoData"/>) — це і є друкарська помилка в шляху
/// AF, яку інакше знаходять через місяць порожнім збором;</description></item>
/// <item><description>колонка документа, за якою не стоїть нічого —
/// <see cref="MappingPreview.UncoveredColumns"/>.</description></item>
/// </list>
/// <para>
/// ⚠ Згортання точок робить <see cref="PeriodFold"/> — та сама функція, якою
/// переносить значення нічна задача. Власна копія тут показувала б число,
/// якого перенос не запише.
/// </para>
/// </remarks>
/// <param name="preview">Сховище сирого матеріалу перегляду.</param>
/// <param name="access">Рішення доступу.</param>
/// <param name="currentUser">Поточний користувач.</param>
/// <param name="clock">Годинник.</param>
/// <param name="units">
/// Довідник одиниць для конверсії на межі (HSE301 F3, ФВ-16.10); <c>null</c> —
/// значення показуються в одиниці джерела, а інтеграл за часом не показується.
/// ⚠ Необов'язковий, щоб не ламати наявні конструктори; контейнер його
/// передає (<c>IUnitCatalog</c> зареєстровано в інфраструктурі).
/// </param>
public sealed class PreviewMappingHandler(
    IMappingPreviewStore preview,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    Ports.IUnitCatalog? units = null)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>
    /// Скільки реальних рядків показати.
    /// </summary>
    /// <remarks>
    /// ⚠ Не параметр запиту. Перегляд відповідає на питання «куди лягає те, що
    /// прийшло», а не «покажи все»: сторінкування тут означало б, що людина
    /// гортає тисячі однакових точок замість дивитися на розриви, які й так
    /// зведені окремими переліками.
    /// </remarks>
    public const int RowLimit = 200;

    /// <summary>
    /// Стеля точок, які беруться у згортання.
    /// </summary>
    /// <remarks>
    /// ⛔ Перевищення стелі **позначається** (<see cref="MappingPreview.IsTruncated"/>),
    /// а не мовчить. Урізана серія дає правильне на вигляд число для
    /// <c>Last</c> і <c>Sum</c> — тобто перегляд збрехав би саме там, де на
    /// нього дивляться.
    /// </remarks>
    public const int MaxPoints = 50_000;

    /// <summary>Вікно за замовчуванням, якщо його не назвали.</summary>
    public const int DefaultWindowDays = 7;

    /// <summary>Будує перегляд мапінгу.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="fromUtc">Початок вікна; <c>null</c> — <see cref="DefaultWindowDays"/> назад.</param>
    /// <param name="toUtc">Кінець вікна; <c>null</c> — «зараз».</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">Вікно порожнє або перевернуте.</exception>
    /// <exception cref="NotFoundException">Сутності джерела немає або вона вимкнена.</exception>
    public async Task<MappingPreview> HandleAsync(
        int sourceEntityId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var to = toUtc ?? clock.UtcNow;
        var from = fromUtc ?? to.AddDays(-DefaultWindowDays);

        // ⛔ Перевернуте вікно не «виправляється» обміном меж. Той, хто його
        // надіслав, помилився в одному з двох полів, і мовчазна перестановка
        // дала б правдоподібний перегляд ЗОВСІМ іншого проміжку.
        if (from >= to)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вікно перегляду порожнє: початок {from:O} не раніший за кінець {to:O}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.mappingPreviewWindow",
                    ["fromUtc"] = from.ToString("O", CultureInfo.InvariantCulture),
                    ["toUtc"] = to.ToString("O", CultureInfo.InvariantCulture),
                });
        }

        var data = await preview
            .LoadAsync(sourceEntityId, from, to, MaxPoints, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {sourceEntityId} немає або вона вимкнена.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                    ["id"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
                });

        var snapshot = units is not null && data.Maps.Any(NeedsCatalog)
            ? await units.GetAsync(ct).ConfigureAwait(false)
            : null;

        return Compose(data, from, to, snapshot);
    }

    /// <summary>Складає відповідь із сирого матеріалу.</summary>
    /// <param name="data">Сутність, мапінги, реальні точки і колонки цілей.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна.</param>
    /// <param name="unitCatalog">
    /// Знімок довідника для конверсії на межі; <c>null</c> — без конверсії
    /// (значення в одиниці джерела, інтеграл за часом — <c>null</c>).
    /// </param>
    /// <remarks>
    /// Метод відкритий навмисно: уся логіка розривів перевіряється тут,
    /// без бази і без HTTP.
    /// </remarks>
    public static MappingPreview Compose(
        MappingPreviewData data, DateTime fromUtc, DateTime toUtc, Ports.UnitCatalogSnapshot? unitCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Одне поле джерела може мати кілька мапінгів — те саме число лягає в
        // дві колонки. Списком, а не словником «поле → мапінг»: другий мапінг
        // мовчки зникав би з перегляду.
        // ⚠ Призупинений мапінг (BE-27) точок нікуди не кладе, тож адреси
        // рядків і «поле нікуди не лягає» рахуються лише за діючими; у
        // `Fields` він лишається — з `IsActive = false`.
        var byField = new Dictionary<string, List<FieldMapRef>>(StringComparer.Ordinal);
        foreach (var map in data.Maps.Where(m => m.IsActive))
        {
            if (!byField.TryGetValue(map.SourceField, out var list))
            {
                list = [];
                byField[map.SourceField] = list;
            }

            list.Add(map);
        }

        return new MappingPreview(
            data.Entity.Id,
            data.Entity.Code,
            data.Entity.DisplayName,
            fromUtc,
            toUtc,
            data.Points.Count,
            data.IsTruncated,
            Fields(data, fromUtc, toUtc, unitCatalog),
            Rows(data, byField),
            Unmapped(data, byField),
            Uncovered(data));
    }

    /// <summary>Підсумок на кожен мапінг: що саме він поклав би в комірку.</summary>
    private static List<MappedFieldPreview> Fields(
        MappingPreviewData data, DateTime fromUtc, DateTime toUtc, Ports.UnitCatalogSnapshot? unitCatalog)
    {
        var result = new List<MappedFieldPreview>(data.Maps.Count);

        foreach (var map in data.Maps)
        {
            // Згортки точок — лише точки з числом (як і до F3); згортки за
            // часом бачать і точки без числа: ті роблять відрізки прогалиною.
            var numeric = new List<TimedPoint>();
            var timed = new List<TimedPoint>();
            foreach (var point in data.Points)
            {
                if (!string.Equals(point.SourcePath, map.SourceField, StringComparison.Ordinal))
                {
                    continue;
                }

                if (point.ValueNumeric is { } value)
                {
                    numeric.Add(new TimedPoint(point.Timestamp, value));
                }

                timed.Add(new TimedPoint(
                    point.Timestamp,
                    point.ValueNumeric ?? 0m,
                    point.ValueNumeric is not null
                    && (point.Quality is null
                        || string.Equals(point.Quality, WindowFold.GoodQuality, StringComparison.OrdinalIgnoreCase))));
            }

            var kind = Parse(map.Aggregation);

            // ⚠ Значення рахується, лише коли є ЧИМ згортати і ВІДОМО як.
            // Мапінг без агрегації число не дає — і не має давати: домен його
            // з рядком-адресатом не приймає, а без адресата воно нікуди не
            // лягає.
            var conversionFailed = false;
            var folded = kind is { } known
                ? Fold(known, IsTimeFold(known) ? timed : numeric, fromUtc, toUtc, map, unitCatalog, out conversionFailed)
                : null;

            // ⛔ D-4: порожнє значення має названу причину, а не мовчить. Несумісні розмірності видно й без
            // точок (це властивість налаштування), інша відмова конверсії - лише коли було що конвертувати.
            var unitIssue = unitCatalog is not null
                            && FieldMapUnitCompatibility.IsIncompatible(
                                unitCatalog, UnitId(unitCatalog, map.SourceUnitCode), UnitId(unitCatalog, map.TargetUnitCode), kind)
                ? FieldMapUnitCompatibility.MismatchKey
                : conversionFailed ? ConversionFailedKey : null;

            result.Add(new MappedFieldPreview(
                map.Id,
                map.SourceField,
                FieldOutcome(map, numeric.Count),
                map.TargetRowKey,
                map.TargetColumnDefId,
                map.TargetColumnCode,
                map.Aggregation,
                map.SourceUnitCode,
                map.TargetUnitCode,
                numeric.Count,
                folded,
                map.IsActive,
                map.PendingSourceUnitChange,
                unitIssue));
        }

        return result;
    }

    /// <summary>Реальні рядки джерела з адресою, куди кожен із них лягає.</summary>
    /// <remarks>
    /// ⚠ Точка з двома мапінгами дає ДВА рядки. Один рядок із приміткою
    /// «і ще кудись» приховав би саме те, заради чого перегляд існує: адресу.
    /// </remarks>
    private static List<MappingPreviewRow> Rows(
        MappingPreviewData data, Dictionary<string, List<FieldMapRef>> byField)
    {
        var rows = new List<MappingPreviewRow>(Math.Min(RowLimit, data.Points.Count));

        foreach (var point in data.Points)
        {
            if (rows.Count >= RowLimit)
            {
                break;
            }

            if (!byField.TryGetValue(point.SourcePath, out var maps))
            {
                rows.Add(new MappingPreviewRow(
                    point.SourcePath, point.Timestamp, point.ValueNumeric, point.ValueString,
                    point.Quality, MappingOutcome.Unmapped, null, null, null));
                continue;
            }

            foreach (var map in maps)
            {
                if (rows.Count >= RowLimit)
                {
                    break;
                }

                rows.Add(new MappingPreviewRow(
                    point.SourcePath, point.Timestamp, point.ValueNumeric, point.ValueString,
                    point.Quality, RowOutcome(map), map.TargetRowKey, map.TargetColumnCode,
                    map.Aggregation));
            }
        }

        return rows;
    }

    /// <summary>Поля джерела, під які немає жодного мапінгу.</summary>
    private static List<UnmappedSourceField> Unmapped(
        MappingPreviewData data, Dictionary<string, List<FieldMapRef>> byField)
    {
        var seen = new Dictionary<string, UnmappedSourceField>(StringComparer.Ordinal);

        foreach (var point in data.Points)
        {
            if (byField.ContainsKey(point.SourcePath))
            {
                continue;
            }

            if (seen.TryGetValue(point.SourcePath, out var known))
            {
                seen[point.SourcePath] = known with
                {
                    PointCount = known.PointCount + 1,
                    LastSeenUtc = point.Timestamp > known.LastSeenUtc ? point.Timestamp : known.LastSeenUtc,
                };
                continue;
            }

            seen[point.SourcePath] = new UnmappedSourceField(point.SourcePath, 1, point.Timestamp);
        }

        return [.. seen.Values.OrderByDescending(f => f.PointCount).ThenBy(f => f.SourcePath, StringComparer.Ordinal)];
    }

    /// <summary>Колонки цільових таблиць, за якими не стоїть нічого.</summary>
    private static List<UncoveredColumn> Uncovered(MappingPreviewData data)
    {
        var result = new List<UncoveredColumn>();

        foreach (var column in data.Columns)
        {
            if (column.HasFieldMap || column.HasCalculationBinding || column.HasFormula)
            {
                continue;
            }

            // ⛔ Колонка, яку не можна ані заповнити руками, ані порахувати, —
            // це діра, а не налаштування: у ній не буде значення НІКОЛИ.
            // Колонка для ручного вводу теж потрапляє в перелік, але окремою
            // ознакою: «її заповнює людина» — законна відповідь, і плутати ці
            // два стани означало б знецінити перший.
            var blocking = column.IsReadOnly || column.IsComputed;

            result.Add(new UncoveredColumn(
                column.TableDefId,
                column.ColumnDefId,
                column.Code,
                column.Header,
                column.IsRequired,
                blocking));
        }

        return [.. result.OrderByDescending(c => c.IsUnfillable).ThenBy(c => c.Code, StringComparer.Ordinal)];
    }

    /// <summary>Стан мапінгу з огляду на ціль і на наявність реальних точок.</summary>
    /// <remarks>
    /// ⛔ Порядок перевірок — від невиправного до законного. «Немає точок»
    /// стоїть ПЕРЕД «не матеріалізується»: другий стан люди обирають
    /// свідомо, перший вибирає за них помилка в шляху.
    /// </remarks>
    private static MappingOutcome FieldOutcome(FieldMapRef map, int pointCount)
    {
        if (!map.TargetColumnExists)
        {
            return MappingOutcome.TargetMissing;
        }

        if (pointCount == 0)
        {
            return MappingOutcome.NoData;
        }

        return map.TargetRowKey is null ? MappingOutcome.RawOnly : MappingOutcome.Materialized;
    }

    /// <summary>Стан окремого рядка: тут точка вже є, тому <c>NoData</c> неможливий.</summary>
    private static MappingOutcome RowOutcome(FieldMapRef map)
    {
        if (!map.TargetColumnExists)
        {
            return MappingOutcome.TargetMissing;
        }

        return map.TargetRowKey is null ? MappingOutcome.RawOnly : MappingOutcome.Materialized;
    }

    /// <summary>Спосіб згортання з мапінгу; <c>null</c> — не заданий.</summary>
    private static AggregationKind? Parse(string? aggregation)
        => Enum.TryParse<AggregationKind>(aggregation, out var kind) ? kind : null;

    /// <summary>
    /// Згортка вікна й конверсія на межі — ті самі <see cref="PeriodFold"/> і
    /// <see cref="BoundaryUnitConversion"/>, що в нічному перенесенні.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ Межі вікна перегляду — без точок до й після нього: сховище перегляду
    /// читає лише <c>[from, to)</c>, тож краї вікна для згорток за часом —
    /// прогалина (без екстраполяції), а ряд вважається лінійним (<c>IsStep</c>
    /// до перегляду не доходить). Число може бути меншим за матеріалізоване на
    /// ширину країв — але не іншим за природою.
    /// </para>
    /// <para>
    /// ⛔ Несумісні одиниці або інтеграл без довідника — <c>null</c>, а не число
    /// в одиниці джерела: перегляд показує те, що ляже в комірку, і
    /// «Sm3/h × с» у колонці Sm3 було б саме тим хибним числом, якого він
    /// має не допустити.
    /// </para>
    /// </remarks>
    private static decimal? Fold(
        AggregationKind kind,
        List<TimedPoint> series,
        DateTime fromUtc,
        DateTime toUtc,
        FieldMapRef map,
        Ports.UnitCatalogSnapshot? unitCatalog,
        out bool conversionFailed)
    {
        conversionFailed = false;
        if (series.Count == 0 || toUtc <= fromUtc)
        {
            return null;
        }

        if (PeriodFold.Fold(kind, series, fromUtc, toUtc, isStep: false).Value is not { } folded)
        {
            return null;
        }

        if (unitCatalog is null)
        {
            return kind == AggregationKind.TimeIntegral ? null : folded;
        }

        try
        {
            return BoundaryUnitConversion.ConvertFolded(
                kind, folded, UnitId(unitCatalog, map.SourceUnitCode), UnitId(unitCatalog, map.TargetUnitCode), unitCatalog)
                .Value;
        }
        catch (Exception ex) when (ex is DomainException or EcrException)
        {
            conversionFailed = true;
            return null;
        }
    }

    /// <summary>Ключ каталогу: значення не переведено в цільову одиницю (причина - у журналі задачі перенесення).</summary>
    private const string ConversionFailedKey = "err.ECR-UOM-0422.boundaryConversionFailed";

    /// <summary>Ідентифікатор одиниці за кодом; <c>null</c> — код не заданий.</summary>
    /// <remarks>Код, якого немає в довіднику, дає <c>-1</c>: конверсія відмовить, а не пропустить.</remarks>
    private static int? UnitId(Ports.UnitCatalogSnapshot unitCatalog, string? code)
        => code is null ? null : unitCatalog.Units.TryGetValue(code, out var unit) ? unit.Id : -1;

    /// <summary>Чи згортка за часом.</summary>
    private static bool IsTimeFold(AggregationKind kind)
        => kind is AggregationKind.TimeWeightedAvg or AggregationKind.TimeIntegral;

    /// <summary>Чи потрібен довідник одиниць: інтеграл або дві різні оголошені одиниці.</summary>
    private static bool NeedsCatalog(FieldMapRef map)
        => Parse(map.Aggregation) == AggregationKind.TimeIntegral
           || (map.SourceUnitCode is { } from && map.TargetUnitCode is { } to
               && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Що станеться з рядком джерела або з мапінгом.</summary>
public enum MappingOutcome : byte
{
    /// <summary>Значення лягає в комірку документа.</summary>
    Materialized = 0,

    /// <summary>Мапінг є, рядка-адресата немає: точка лишається сирою (<c>D-118</c>).</summary>
    RawOnly = 1,

    /// <summary>Поле джерела не має жодного мапінгу — воно не лягає нікуди.</summary>
    Unmapped = 2,

    /// <summary>Мапінг веде на колонку, якої вже немає.</summary>
    TargetMissing = 3,

    /// <summary>Мапінг є, але жодного реального рядка під нього в джерелі немає.</summary>
    NoData = 4,
}

/// <summary>Перегляд мапінгу на реальних рядках (<c>ФВ-13.14</c>).</summary>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="Code">Код сутності в джерелі.</param>
/// <param name="DisplayName">Підпис із каталогу джерела.</param>
/// <param name="FromUtc">Початок вікна, включно.</param>
/// <param name="ToUtc">Кінець вікна, виключно.</param>
/// <param name="PointsSeen">Скільки реальних точок узято у згортання.</param>
/// <param name="IsTruncated">Точок більше за стелю: згорнуті значення неповні.</param>
/// <param name="Fields">Підсумок на кожен мапінг.</param>
/// <param name="Rows">Реальні рядки джерела з адресою призначення.</param>
/// <param name="UnmappedSourceFields">Поля джерела, які не лягають нікуди.</param>
/// <param name="UncoveredColumns">Колонки документа, за якими не стоїть нічого.</param>
public sealed record MappingPreview(
    int SourceEntityId,
    string Code,
    string? DisplayName,
    DateTime FromUtc,
    DateTime ToUtc,
    int PointsSeen,
    bool IsTruncated,
    IReadOnlyList<MappedFieldPreview> Fields,
    IReadOnlyList<MappingPreviewRow> Rows,
    IReadOnlyList<UnmappedSourceField> UnmappedSourceFields,
    IReadOnlyList<UncoveredColumn> UncoveredColumns);

/// <summary>Підсумок одного мапінгу.</summary>
/// <param name="FieldMapId">Ідентифікатор мапінгу.</param>
/// <param name="SourceField">Поле в джерелі.</param>
/// <param name="Outcome">Стан мапінгу.</param>
/// <param name="TargetRowKey">Рядок-адресат; <c>null</c> — не матеріалізується.</param>
/// <param name="TargetColumnDefId">Колонка-адресат.</param>
/// <param name="TargetColumnCode">Код колонки; <c>null</c> — колонки немає.</param>
/// <param name="Aggregation">Спосіб згортання точок періоду.</param>
/// <param name="SourceUnitCode">Одиниця джерела, оголошена в мапінгу (<c>ФВ-16.9</c>).</param>
/// <param name="TargetUnitCode">Одиниця, в якій значення лягає в ECR.</param>
/// <param name="PointCount">Скільки реальних точок вікна під цей мапінг.</param>
/// <param name="FoldedValue">Число, яке лягло б у комірку; <c>null</c> — нічого згортати.</param>
/// <param name="IsActive">Мапінг діє; <c>false</c> — призупинений (<c>BE-27</c>), значень не пише.</param>
/// <param name="PendingSourceUnitChange">Пауза через зміну одиниці джерела; <c>null</c> — її немає (ФВ-16.9).</param>
/// <param name="UnitIssue">
/// Ключ каталогу причини, чому значення порожнє через одиниці (D-4): розмірності різні
/// (<c>err.ECR-UOM-0422.fieldMapUnitDimensions</c>) або конверсію не виконано
/// (<c>err.ECR-UOM-0422.boundaryConversionFailed</c>); <c>null</c> — одиниці не заважають.
/// </param>
public sealed record MappedFieldPreview(
    int FieldMapId,
    string SourceField,
    MappingOutcome Outcome,
    string? TargetRowKey,
    int? TargetColumnDefId,
    string? TargetColumnCode,
    string? Aggregation,
    string? SourceUnitCode,
    string? TargetUnitCode,
    int PointCount,
    decimal? FoldedValue,
    bool IsActive,
    PendingSourceUnitChange? PendingSourceUnitChange,
    string? UnitIssue = null);

/// <summary>Реальний рядок джерела разом із адресою, куди він лягає.</summary>
/// <param name="SourcePath">Шлях атрибута в джерелі.</param>
/// <param name="Timestamp">Мітка часу точки.</param>
/// <param name="ValueNumeric">Число в одиниці ДЖЕРЕЛА.</param>
/// <param name="ValueString">Текст для нечислових атрибутів.</param>
/// <param name="Quality">Якість за класифікацією джерела.</param>
/// <param name="Outcome">Куди лягає цей рядок.</param>
/// <param name="TargetRowKey">Рядок-адресат; <c>null</c> — адреси немає.</param>
/// <param name="TargetColumnCode">Колонка-адресат; <c>null</c> — адреси немає.</param>
/// <param name="Aggregation">Спосіб згортання, оголошений мапінгом.</param>
public sealed record MappingPreviewRow(
    string SourcePath,
    DateTime Timestamp,
    decimal? ValueNumeric,
    string? ValueString,
    string? Quality,
    MappingOutcome Outcome,
    string? TargetRowKey,
    string? TargetColumnCode,
    string? Aggregation);

/// <summary>Поле джерела, яке не лягає нікуди.</summary>
/// <param name="SourcePath">Шлях атрибута в джерелі.</param>
/// <param name="PointCount">Скільки його точок у вікні.</param>
/// <param name="LastSeenUtc">Остання мітка часу.</param>
public sealed record UnmappedSourceField(string SourcePath, int PointCount, DateTime LastSeenUtc);

/// <summary>Колонка документа, за якою не стоїть нічого.</summary>
/// <param name="TableDefId">Таблиця колонки.</param>
/// <param name="ColumnDefId">Ідентифікатор колонки.</param>
/// <param name="Code">Код колонки.</param>
/// <param name="Header">Заголовок колонки.</param>
/// <param name="IsRequired">Колонка обов'язкова до заповнення.</param>
/// <param name="IsUnfillable">Заповнити її не може ніхто: ані людина, ані рушій.</param>
public sealed record UncoveredColumn(
    int TableDefId,
    int ColumnDefId,
    string Code,
    string? Header,
    bool IsRequired,
    bool IsUnfillable);
