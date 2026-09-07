// src/Ecr.Application/Sources/MappingPreviewHandlers.cs
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
public sealed class PreviewMappingHandler(
    IMappingPreviewStore preview,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
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
                new Dictionary<string, object?> { ["fromUtc"] = from, ["toUtc"] = to });
        }

        var data = await preview
            .LoadAsync(sourceEntityId, from, to, MaxPoints, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {sourceEntityId} немає або вона вимкнена.");

        return Compose(data, from, to);
    }

    /// <summary>Складає відповідь із сирого матеріалу.</summary>
    /// <param name="data">Сутність, мапінги, реальні точки і колонки цілей.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна.</param>
    /// <remarks>
    /// Метод відкритий навмисно: уся логіка розривів перевіряється тут,
    /// без бази і без HTTP.
    /// </remarks>
    public static MappingPreview Compose(MappingPreviewData data, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Одне поле джерела може мати кілька мапінгів — те саме число лягає в
        // дві колонки. Списком, а не словником «поле → мапінг»: другий мапінг
        // мовчки зникав би з перегляду.
        var byField = new Dictionary<string, List<FieldMapRef>>(StringComparer.Ordinal);
        foreach (var map in data.Maps)
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
            Fields(data),
            Rows(data, byField),
            Unmapped(data, byField),
            Uncovered(data));
    }

    /// <summary>Підсумок на кожен мапінг: що саме він поклав би в комірку.</summary>
    private static List<MappedFieldPreview> Fields(MappingPreviewData data)
    {
        var result = new List<MappedFieldPreview>(data.Maps.Count);

        foreach (var map in data.Maps)
        {
            var series = new List<decimal>();
            foreach (var point in data.Points)
            {
                if (point.ValueNumeric is { } value
                    && string.Equals(point.SourcePath, map.SourceField, StringComparison.Ordinal))
                {
                    series.Add(value);
                }
            }

            var kind = Parse(map.Aggregation);

            // ⚠ Значення рахується, лише коли є ЧИМ згортати і ВІДОМО як.
            // Мапінг без агрегації число не дає — і не має давати: домен його
            // з рядком-адресатом не приймає, а без адресата воно нікуди не
            // лягає.
            var folded = series.Count > 0 && kind is { } known
                ? PeriodFold.Fold(known, series)
                : (decimal?)null;

            result.Add(new MappedFieldPreview(
                map.Id,
                map.SourceField,
                FieldOutcome(map, series.Count),
                map.TargetRowKey,
                map.TargetColumnDefId,
                map.TargetColumnCode,
                map.Aggregation,
                map.SourceUnitCode,
                map.TargetUnitCode,
                series.Count,
                folded));
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
    decimal? FoldedValue);

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
