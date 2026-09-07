// src/Ecr.Infrastructure/Persistence/MappingPreviewStore.cs
using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IMappingPreviewStore"/> над <see cref="EcrDbContext"/>.
/// </summary>
/// <remarks>
/// ⛔ Тут немає жодного рішення про те, що вважати розривом: сховище лише
/// приносить факти. Класифікація живе в застосунку і перевіряється без бази —
/// інакше правило «за колонкою нічого не стоїть» існувало б у вигляді
/// <c>LEFT JOIN</c>, який неможливо покрити тестом інакше ніж прогоном на
/// живій схемі.
/// <para>
/// ⚠ Кожен запит має стелю. Перегляд відкривають на сутності, яку щойно
/// налаштували неправильно, — саме там точок буває мільйони.
/// </para>
/// </remarks>
public sealed class MappingPreviewStore(EcrDbContext db) : IMappingPreviewStore
{
    /// <summary>Стеля мапінгів однієї сутності.</summary>
    private const int MaxMaps = 2_000;

    /// <summary>Стеля колонок цільових таблиць.</summary>
    private const int MaxColumns = 5_000;

    /// <inheritdoc />
    public async Task<MappingPreviewData?> LoadAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, int maxPoints, CancellationToken ct)
    {
        var entity = await db.SourceEntities
            .AsNoTracking()
            .Where(e => e.Id == sourceEntityId && e.IsActive)
            .Select(e => new { e.Id, e.Code, e.DisplayName })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        var maps = await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.SourceEntityId == sourceEntityId && m.IsActive)
            .OrderBy(m => m.SourceField)
            .Take(MaxMaps)
            .Select(m => new
            {
                m.Id,
                m.SourceField,
                m.TargetRowKey,
                m.TargetColumnDefId,
                m.TransformCode,
                m.SourceUnitId,
                m.TargetUnitId,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var targetIds = maps
            .Where(m => m.TargetColumnDefId is not null)
            .Select(m => m.TargetColumnDefId!.Value)
            .Distinct()
            .ToList();

        // ⚠ Видалена колонка з вибірки НЕ зникає мовчки: її просто тут не
        // буде, і застосунок назве мапінг таким, що веде в нікуди.
        var targets = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => targetIds.Contains(c.Id) && !c.IsDeleted)
            .Select(c => new { c.Id, c.TableDefId, c.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var targetById = targets.ToDictionary(c => c.Id);

        var units = await UnitCodesAsync(maps.ConvertAll(m => (m.SourceUnitId, m.TargetUnitId)), ct)
            .ConfigureAwait(false);

        var points = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId
                        && p.Timestamp >= fromUtc
                        && p.Timestamp < toUtc)
            .OrderBy(p => p.Timestamp)
            .ThenBy(p => p.SourcePath)

            // ⛔ Береться на одну точку БІЛЬШЕ за стелю. Рівно стеля і
            // «стеля, за якою є ще» — різні стани, і без цієї одиниці їх
            // неможливо розрізнити: перегляд оголосив би урізану суму повною.
            .Take(maxPoints + 1)
            .Select(p => new RawPointRef(
                p.SourcePath, p.Timestamp, p.ValueNumeric, p.ValueString, p.Quality))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var isTruncated = points.Count > maxPoints;
        if (isTruncated)
        {
            points.RemoveAt(points.Count - 1);
        }

        var columns = await ColumnsAsync(
                targets.Select(c => c.TableDefId).Distinct().ToList(), ct)
            .ConfigureAwait(false);

        var refs = maps.ConvertAll(m =>
        {
            var target = m.TargetColumnDefId is { } id && targetById.TryGetValue(id, out var found)
                ? found
                : null;

            return new FieldMapRef(
                m.Id,
                m.SourceField,
                m.TargetRowKey,
                m.TargetColumnDefId,
                target?.Code,
                target is not null,
                Aggregation(m.TransformCode),
                Code(units, m.SourceUnitId),
                Code(units, m.TargetUnitId));
        });

        return new MappingPreviewData(
            new SourceEntityRef(entity.Id, entity.Code, entity.DisplayName),
            refs,
            points,
            isTruncated,
            columns);
    }

    /// <summary>
    /// Колонки цільових таблиць разом із тим, чи стоїть за ними хоч щось.
    /// </summary>
    /// <remarks>
    /// ⛔ Мапінги беруться від <b>усіх</b> сутностей джерела, а не лише від
    /// тієї, яку дивляться. Колонку може наповнювати сусіднє джерело, і
    /// перегляд, що цього не бачить, оголосив би розривом справний зв'язок —
    /// після третього такого рядка перелік розривів перестають читати.
    /// </remarks>
    private async Task<List<TargetColumnRef>> ColumnsAsync(List<int> tableDefIds, CancellationToken ct)
    {
        if (tableDefIds.Count == 0)
        {
            return [];
        }

        var columns = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => tableDefIds.Contains(c.TableDefId) && !c.IsDeleted)
            .OrderBy(c => c.TableDefId)
            .ThenBy(c => c.Ordinal)
            .Take(MaxColumns)
            .Select(c => new
            {
                c.Id,
                c.TableDefId,
                c.Code,
                c.HeaderL10n,
                c.IsReadOnly,
                c.IsRequired,
                c.DataType,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ids = columns.ConvertAll(c => c.Id);

        var mapped = await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.IsActive
                        && m.TargetRowKey != null
                        && m.TargetColumnDefId != null
                        && ids.Contains(m.TargetColumnDefId!.Value))
            .Select(m => m.TargetColumnDefId!.Value)
            .Distinct()
            .Take(MaxColumns)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var bound = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && ids.Contains(b.ColumnDefId))
            .Select(b => b.ColumnDefId)
            .Distinct()
            .Take(MaxColumns)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var computed = await db.FormulaDefs
            .AsNoTracking()
            .Where(f => !f.IsDeleted && f.ColumnDefId != null && ids.Contains(f.ColumnDefId!.Value))
            .Select(f => f.ColumnDefId!.Value)
            .Distinct()
            .Take(MaxColumns)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var mappedSet = mapped.ToHashSet();
        var boundSet = bound.ToHashSet();
        var computedSet = computed.ToHashSet();

        return columns.ConvertAll(c => new TargetColumnRef(
            c.TableDefId,
            c.Id,
            c.Code,

            // ⚠ Заголовок береться англійською: перегляд читає той, хто
            // налаштовує інтеграцію, а коди колонок і шляхи AF англійські в
            // будь-якому разі. Мову відповіді сюди не заводимо, доки її не
            // просять, — інакше довелося б тягнути її крізь порт заради поля,
            // яке стоїть поруч із `Code`.
            c.HeaderL10n.Get("en"),
            c.IsReadOnly,
            IsComputed(c.DataType),
            c.IsRequired,
            mappedSet.Contains(c.Id),
            boundSet.Contains(c.Id),
            computedSet.Contains(c.Id)));
    }

    /// <summary>Коди одиниць, названих мапінгами.</summary>
    private async Task<Dictionary<int, string>> UnitCodesAsync(
        List<(int? Source, int? Target)> pairs, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var (source, target) in pairs)
        {
            if (source is { } s)
            {
                ids.Add(s);
            }

            if (target is { } t)
            {
                ids.Add(t);
            }
        }

        if (ids.Count == 0)
        {
            return [];
        }

        var found = await db.Units
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Take(MaxMaps)
            .Select(u => new { u.Id, u.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found.ToDictionary(u => u.Id, u => u.Code);
    }

    /// <summary>Код одиниці; <c>null</c> — одиниця не названа або невідома.</summary>
    private static string? Code(Dictionary<int, string> units, int? unitId)
        => unitId is { } id && units.TryGetValue(id, out var code) ? code : null;

    /// <summary>
    /// Спосіб згортання, якщо <c>TransformCode</c> ним і є.
    /// </summary>
    /// <remarks>
    /// ⚠ Та сама колонка несе і іменоване перетворення, і спосіб згортання —
    /// так її задає домен (<c>EntityFieldMap.SetMaterialization</c>). Тому
    /// правило розбору тут дослівно повторює доменне: збіг із
    /// <see cref="AggregationKind"/> — це агрегація, будь-що інше — ні.
    /// </remarks>
    private static string? Aggregation(string? transformCode)
        => Enum.TryParse<AggregationKind>(transformCode, out var kind) ? kind.ToString() : null;

    /// <summary>Чи значення колонки рахує система (дублює <c>ColumnDef.IsComputed</c>).</summary>
    /// <remarks>
    /// ⚠ Властивість домену <c>Ignore</c>-нута для EF, тому в проєкції її
    /// немає; тип колонки при цьому проєктується, і правило лишається одне.
    /// </remarks>
    private static bool IsComputed(Domain.Enums.CellDataType dataType)
        => dataType is Domain.Enums.CellDataType.Formula or Domain.Enums.CellDataType.Calculated;
}
