// src/Ecr.Application/Integration/RawData/RawDataHandler.cs
using System.Globalization;
using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration.RawData;

/// <summary>
/// «Дані з PI» (<c>D-463</c>): перегляд уже зібраних точок сутності збору — теги, сторінки точок,
/// згорнутий графік, покриття вікна збором і потоковий CSV. Право <c>Integration.View</c> або
/// <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Межа читання та сама, що в журналу прогонів і покриття (<see cref="ListCollectionRunsHandler"/>):
/// хто бачить, ЩО і КОЛИ збір приніс, бачить і самі значення — окреме право розщепило б одну роботу
/// адміністратора інтеграції на два екрани з різним доступом. Ширшого права тут немає:
/// <c>Integration.Manage</c> включає перегляд, як і всюди в інтеграції.
/// <para>
/// ⚠ Тільки читання <c>ext.RawDataPoint</c>: сюди не ходить джерело наживо (недоступний PI не ламає
/// перегляд) і нічого не пишеться, крім події безпеки експорту.
/// </para>
/// </remarks>
/// <param name="reader">Сховище зібраних точок.</param>
/// <param name="units">Довідник одиниць.</param>
/// <param name="access">Рішення доступу.</param>
/// <param name="currentUser">Поточний користувач.</param>
/// <param name="clock">Годинник.</param>
/// <param name="audit">Журнал безпеки — подія експорту.</param>
public sealed class RawDataHandler(
    IRawDataReader reader,
    IUnitCatalog units,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    IAuditWriter audit)
{
    /// <summary>Право перегляду інтеграції.</summary>
    public const string ViewPermission = "Integration.View";

    /// <summary>Право керування інтеграцією — включає перегляд.</summary>
    public const string ManagePermission = "Integration.Manage";

    /// <summary>Тип події журналу безпеки для експорту.</summary>
    public const string ExportedEventType = "PiRawDataExported";

    /// <summary>Стеля тегів однієї сутності — як стеля мапінгів у перегляді мапінгу.</summary>
    public const int MaxTags = 2000;

    /// <summary>Стеля покритих інтервалів у відповіді.</summary>
    public const int MaxIntervals = 1000;

    /// <summary>Типова стеля рядків експорту, якщо конфіг не задає іншої.</summary>
    public const int RawExportMaxRowsDefault = 100_000;

    /// <summary>Сутність і її теги.</summary>
    /// <exception cref="NotFoundException">Сутності немає.</exception>
    public async Task<RawDataTagsView> TagsAsync(int sourceEntityId, CancellationToken ct)
    {
        await PermissionCheck.RequireAnyAsync(access, currentUser, [ViewPermission, ManagePermission], ct).ConfigureAwait(false);

        var entity = await EntityAsync(sourceEntityId, ct).ConfigureAwait(false);
        var tags = await reader.ListTagsAsync(sourceEntityId, MaxTags + 1, ct).ConfigureAwait(false);
        var eventMaps = await reader.CountEventMapsAsync(sourceEntityId, ct).ConfigureAwait(false);

        // ⚠ Без мапінгів збір читає саму сутність (`CollectionRunner.Paths`) — тег той самий.
        IReadOnlyList<RawDataTag> shown = tags.Count == 0
            ? [new RawDataTag(entity.EntityPath ?? entity.Code, null, null, null, null, null, entity.IsActive)]
            : [.. tags.Take(MaxTags)];

        return new RawDataTagsView(
            entity.Id, entity.Code, entity.DisplayName, entity.IsActive, shown, tags.Count > MaxTags, eventMaps);
    }

    /// <summary>Сторінка точок тега, новіші першими.</summary>
    /// <exception cref="BusinessRuleException">Шлях, вікно чи розмір сторінки поза межами.</exception>
    /// <exception cref="NotFoundException">Сутності немає.</exception>
    public async Task<PagedResult<RawPointView>> PointsAsync(
        int sourceEntityId, string? path, DateTime? fromUtc, DateTime? toUtc, string? cursor, int limit,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAnyAsync(access, currentUser, [ViewPermission, ManagePermission], ct).ConfigureAwait(false);

        var key = await KeyAsync(sourceEntityId, path, fromUtc, toUtc, ct).ConfigureAwait(false);
        var size = RawDataQuery.PageSize(limit);

        return await RawDataQuery.PageAsync(reader, units, key, cursor, size, ct).ConfigureAwait(false);
    }

    /// <summary>Згорнутий ряд тега для графіка.</summary>
    /// <exception cref="BusinessRuleException">Шлях, вікно чи число кошиків поза межами.</exception>
    /// <exception cref="NotFoundException">Сутності немає.</exception>
    public async Task<RawSeriesView> SeriesAsync(
        int sourceEntityId, string? path, DateTime? fromUtc, DateTime? toUtc, int buckets, CancellationToken ct)
    {
        await PermissionCheck.RequireAnyAsync(access, currentUser, [ViewPermission, ManagePermission], ct).ConfigureAwait(false);

        var count = RawDataQuery.BucketCount(buckets);
        var key = await KeyAsync(sourceEntityId, path, fromUtc, toUtc, ct).ConfigureAwait(false);

        return await RawDataQuery.SeriesAsync(reader, key, count, ct).ConfigureAwait(false);
    }

    /// <summary>Покриття вікна збором і стан останнього прогону.</summary>
    /// <exception cref="BusinessRuleException">Вікно поза межами.</exception>
    /// <exception cref="NotFoundException">Сутності немає.</exception>
    public async Task<RawCoverageView> CoverageAsync(
        int sourceEntityId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        await PermissionCheck.RequireAnyAsync(access, currentUser, [ViewPermission, ManagePermission], ct).ConfigureAwait(false);

        var (from, to) = RawDataQuery.Window(fromUtc, toUtc, clock.UtcNow);
        await EntityAsync(sourceEntityId, ct).ConfigureAwait(false);

        var rows = await reader.CoveredAsync(sourceEntityId, from, to, MaxIntervals + 1, ct).ConfigureAwait(false);
        var last = await reader.LastCollectionAsync(sourceEntityId, ct).ConfigureAwait(false);

        var truncated = rows.Count > MaxIntervals;
        var (covered, gaps) = Islands([.. rows.Take(MaxIntervals)], from, to, truncated);

        return new RawCoverageView(from, to, covered, gaps, truncated, last);
    }

    /// <summary>
    /// Перевіряє право, межі й стелю, пише подію безпеки — і лише тоді віддає лінивий потік рядків.
    /// </summary>
    /// <param name="sourceEntityId">Сутність.</param>
    /// <param name="path">Шлях тега.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна.</param>
    /// <param name="maxRows">Жорстка стеля рядків; понад неї — <c>422 auditExportTooLarge</c> до першого байта.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">Межі або стеля.</exception>
    /// <exception cref="NotFoundException">Сутності немає.</exception>
    public async Task<AuditExport<RawPointView>> PrepareExportAsync(
        int sourceEntityId, string? path, DateTime? fromUtc, DateTime? toUtc, int maxRows, CancellationToken ct)
    {
        await PermissionCheck.RequireAnyAsync(access, currentUser, [ViewPermission, ManagePermission], ct).ConfigureAwait(false);

        var key = await KeyAsync(sourceEntityId, path, fromUtc, toUtc, ct).ConfigureAwait(false);

        // ⛔ Подія безпеки — ДО потоку і без значень: хто, яка сутність/тег/вікно і скільки рядків
        // (`AuditExportRunner`, той самий хід, що в експортах журналу аудиту).
        return await AuditExportRunner.PrepareAsync(
            next => RawDataQuery.AscendingPageAsync(reader, units, key, next, ct),
            async _ => (int)Math.Min(int.MaxValue, await reader.CountAsync(key, ct).ConfigureAwait(false)),
            maxRows,
            ExportedEventType,
            total => new
            {
                sourceEntityId = key.SourceEntityId,
                path = key.SourcePath,
                from = key.FromUtc,
                to = key.ToUtc,
                rows = total,
            },
            string.Create(
                CultureInfo.InvariantCulture,
                $"pi-{key.SourceEntityId}-{key.FromUtc:yyyyMMdd'T'HHmmss'Z'}-{key.ToUtc:yyyyMMdd'T'HHmmss'Z'}.csv"),
            audit,
            clock,
            currentUser,
            ct).ConfigureAwait(false);
    }

    /// <summary>Злиття покритих інтервалів і прогалини вікна.</summary>
    /// <param name="rows">Інтервали в будь-якому порядку.</param>
    /// <param name="from">Початок вікна.</param>
    /// <param name="to">Кінець вікна.</param>
    /// <param name="truncated">Інтервали урізано стелею — прогалини після останнього не відомі.</param>
    public static (IReadOnlyList<RawInterval> Covered, IReadOnlyList<RawInterval> Gaps) Islands(
        IReadOnlyList<RawInterval> rows, DateTime from, DateTime to, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var merged = new List<RawInterval>();
        foreach (var row in rows.OrderBy(r => r.FromUtc))
        {
            var start = row.FromUtc < from ? from : row.FromUtc;
            var end = row.ToUtc > to ? to : row.ToUtc;
            if (end <= start)
            {
                continue;
            }

            if (merged.Count > 0 && start <= merged[^1].ToUtc)
            {
                if (end > merged[^1].ToUtc)
                {
                    merged[^1] = merged[^1] with { ToUtc = end };
                }

                continue;
            }

            merged.Add(new RawInterval(Utc(start), Utc(end)));
        }

        // ⚠ Урізаний перелік: прогалини рахуються лише до кінця останнього відомого інтервалу,
        // інакше хвіст вікна оголосився б прогалиною, якої може й не бути.
        var horizon = truncated && merged.Count > 0 ? merged[^1].ToUtc : to;
        var gaps = new List<RawInterval>();
        var cursor = from;
        foreach (var island in merged)
        {
            if (island.FromUtc > cursor)
            {
                gaps.Add(new RawInterval(Utc(cursor), island.FromUtc));
            }

            cursor = island.ToUtc;
        }

        if (cursor < horizon)
        {
            gaps.Add(new RawInterval(Utc(cursor), Utc(horizon)));
        }

        return (merged, gaps);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private async Task<RawSeriesKey> KeyAsync(
        int sourceEntityId, string? path, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var tag = RawDataQuery.Path(path);
        var (from, to) = RawDataQuery.Window(fromUtc, toUtc, clock.UtcNow);
        await EntityAsync(sourceEntityId, ct).ConfigureAwait(false);

        return new RawSeriesKey(sourceEntityId, tag, from, to);
    }

    private async Task<RawSourceEntity> EntityAsync(int sourceEntityId, CancellationToken ct)
        => await reader.FindEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.SourceEntityNotFound,
               $"Сутності джерела {sourceEntityId} немає.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                   ["id"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
               });
}
