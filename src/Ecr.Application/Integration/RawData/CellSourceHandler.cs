// src/Ecr.Application/Integration/RawData/CellSourceHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Integration.RawData;

/// <summary>
/// «Звідки це значення» (<c>D-463</c>): для комірки, яку живить мапінг PI, — тег, вікно періоду, правило
/// згортки, сирі точки періоду (сторінками й згорнутим графіком), останній збір і події покриття.
/// Право <c>Document.View</c> у проєкті документа.
/// </summary>
/// <remarks>
/// ⛔ Доступ — РІВНО доступ до комірки, не ширше: видимість документа й право в його проєкті
/// (<see cref="DocumentVisibility.RequireVisibleAsync(IAccessDecisionService, AccessProfile, long, string, CancellationToken)"/>),
/// належність екземпляра документу, Deny на аркуш/таблицю/колонку в періоді екземпляра
/// (<see cref="IAccessDecisionService.ReadScopeAsync"/>), прихована/видалена колонка, маска Q-17 для
/// колонки іншого власника і існування рядка. Будь-яка відмова нижче видимості — ТА САМА
/// <c>404 err.ECR-DOC-0404.document</c>, що й у зрізу (<see cref="DocumentVisibility.DocumentNotFound"/>):
/// окрема відповідь була б оракулом схованих колонок і чужих тегів.
/// <para>
/// ⛔ Сирі точки — лише ВІКНА ПЕРІОДУ цієї комірки і лише тега її мапінгу: діапазон не параметр запиту,
/// тож комірковий вхід не відкриває історії тега поза тим, що лягло в цю комірку.
/// </para>
/// <para>
/// ⚠ Події покриття — лише ВЛАСТИВОСТІ ДАНИХ цього тега за період (<see cref="DataEventStatuses"/>):
/// журнал покриття не знає документа, тож подія «у комірці ручне значення» з чужого документа того самого
/// шаблону читалася б як подія цієї комірки. Стан самої комірки показує її журнал змін.
/// </para>
/// </remarks>
/// <param name="rows">Сховище рядків — належність екземпляра і рядка.</param>
/// <param name="metadata">Знімок структури версії — колонка без звернення до БД.</param>
/// <param name="store">Мапінги, період, події.</param>
/// <param name="reader">Зібрані точки.</param>
/// <param name="units">Довідник одиниць.</param>
/// <param name="access">Рішення доступу.</param>
/// <param name="currentUser">Поточний користувач.</param>
/// <param name="monthRouter">Q-17: власність місячних колонок; <c>null</c> — лише тести.</param>
public sealed class CellSourceHandler(
    IRowStore rows,
    IMetadataCache metadata,
    ICellSourceStore store,
    IRawDataReader reader,
    IUnitCatalog units,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IMonthColumnRouter? monthRouter = null)
{
    /// <summary>Проєктне право читання комірки.</summary>
    public const string Permission = "Document.View";

    /// <summary>Стеля мапінгів на комірку (у нормі один).</summary>
    public const int MaxMaps = 10;

    /// <summary>Стеля подій покриття на мапінг.</summary>
    public const int MaxEvents = 50;

    /// <summary>Скільки останніх подій сутності за період читати перед фільтром за тегом.</summary>
    private const int EventScan = 200;

    /// <summary>Статуси подій, що є властивістю даних тега за період, а не стану чужої комірки.</summary>
    public static readonly IReadOnlyList<string> DataEventStatuses =
    [
        CollectionCoverage.PartialCoverage,
        CollectionCoverage.SkippedNoData,
        CollectionCoverage.SkippedPointCeiling,
    ];

    /// <summary>Мапінги комірки, межі періоду, останній збір, кількість точок і події.</summary>
    /// <exception cref="Errors.NotFoundException">Комірку не видно (уніфіковано з документом).</exception>
    public async Task<CellSourceView> SummaryAsync(
        long documentId, long tableInstanceId, string? rowKey, int columnDefId, CancellationToken ct)
    {
        var cell = await ResolveAsync(documentId, tableInstanceId, rowKey, columnDefId, ct).ConfigureAwait(false);

        var items = new List<CellSourceItem>(cell.Maps.Count);
        foreach (var map in cell.Maps)
        {
            var key = new RawSeriesKey(map.SourceEntityId, map.SourcePath, cell.FromUtc, cell.ToUtc);
            var count = await reader.CountAsync(key, ct).ConfigureAwait(false);
            var last = await reader.LastCollectionAsync(map.SourceEntityId, ct).ConfigureAwait(false);
            var events = await store
                .PeriodEventsAsync(map.SourceEntityId, cell.PeriodKey, EventScan, ct)
                .ConfigureAwait(false);

            items.Add(new CellSourceItem(
                map.MapId, map.SourceEntityId, map.SourceEntityCode, map.SourceEntityName, map.SourcePath,
                map.Aggregation, map.SourceUnitCode, map.TargetUnitCode, map.IsActive, count, last,
                [.. events.Where(e => Concerns(e, map)).Take(MaxEvents)]));
        }

        return new CellSourceView(cell.PeriodKey, cell.FromUtc, cell.ToUtc, items);
    }

    /// <summary>Сторінка сирих точок тега мапінгу в межах періоду комірки, новіші першими.</summary>
    /// <exception cref="Errors.NotFoundException">Комірку не видно або мапінг не її.</exception>
    /// <exception cref="Errors.BusinessRuleException">Розмір сторінки поза межами.</exception>
    public async Task<PagedResult<RawPointView>> PointsAsync(
        long documentId, long tableInstanceId, string? rowKey, int columnDefId, int mapId, string? cursor, int limit,
        CancellationToken ct)
    {
        var size = RawDataQuery.PageSize(limit);
        var key = await KeyAsync(documentId, tableInstanceId, rowKey, columnDefId, mapId, ct).ConfigureAwait(false);

        return await RawDataQuery.PageAsync(reader, units, key, cursor, size, ct).ConfigureAwait(false);
    }

    /// <summary>Згорнутий ряд тега мапінгу за період комірки.</summary>
    /// <exception cref="Errors.NotFoundException">Комірку не видно або мапінг не її.</exception>
    /// <exception cref="Errors.BusinessRuleException">Кількість кошиків поза межами.</exception>
    public async Task<RawSeriesView> SeriesAsync(
        long documentId, long tableInstanceId, string? rowKey, int columnDefId, int mapId, int buckets,
        CancellationToken ct)
    {
        var count = RawDataQuery.BucketCount(buckets);
        var key = await KeyAsync(documentId, tableInstanceId, rowKey, columnDefId, mapId, ct).ConfigureAwait(false);

        return await RawDataQuery.SeriesAsync(reader, key, count, ct).ConfigureAwait(false);
    }

    /// <summary>Чи подія — властивість даних саме цього тега/мапінгу.</summary>
    public static bool Concerns(CellSourceEvent e, CellSourceMap map)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(map);

        if (!DataEventStatuses.Contains(e.Status, StringComparer.Ordinal)
            || !JobProgressMessageCodec.TryDecode(e.Details, out var envelope)
            || envelope.Params is not { } p)
        {
            return false;
        }

        // ⚠ Шлях порівнюється без регістру — так само, як збір зіставляє точку з мапінгом (`CollectionRunner`).
        return (p.TryGetValue("mapId", out var id)
                && string.Equals(id, map.MapId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
               || (p.TryGetValue("field", out var field)
                   && string.Equals(field, map.SourcePath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<RawSeriesKey> KeyAsync(
        long documentId, long tableInstanceId, string? rowKey, int columnDefId, int mapId, CancellationToken ct)
    {
        var cell = await ResolveAsync(documentId, tableInstanceId, rowKey, columnDefId, ct).ConfigureAwait(false);

        // ⛔ Мапінг, що не живить цю комірку, — та сама 404: інакше `mapId` став би ключем до точок
        // будь-якого тега системи через будь-яку видиму комірку.
        var map = cell.Maps.FirstOrDefault(m => m.MapId == mapId)
                  ?? throw DocumentVisibility.DocumentNotFound(documentId);

        return new RawSeriesKey(map.SourceEntityId, map.SourcePath, cell.FromUtc, cell.ToUtc);
    }

    /// <summary>Доводить, що користувач читає комірку, і повертає її мапінги з межами періоду.</summary>
    private async Task<ResolvedCell> ResolveAsync(
        long documentId, long tableInstanceId, string? rowKey, int columnDefId, CancellationToken ct)
    {
        // 1. Право хоч у якомусь проєкті — і (у тому ж методі) у проєкті документа разом із видимістю.
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);
        await DocumentVisibility
            .RequireVisibleAsync(access, profile, documentId, Permission, ct)
            .ConfigureAwait(false);

        // 2. Екземпляр належить документу з маршруту (чужий/відсутній — та сама 404).
        var instance = await DocumentVisibility
            .ResolveInDocumentAsync(rows, documentId, tableInstanceId, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(rowKey) || rowKey.Length > 100)
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        // 3. Deny на аркуш/таблицю/колонку — у періоді екземпляра, як у зрізі.
        var period = new PeriodKey(instance.PeriodKey);
        var anyPeriod = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        var readable = anyPeriod.InPeriod(period);
        if (!readable.CanReadTable(instance.TableDefId) || !readable.CanReadColumn(columnDefId))
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        // 4. Колонка належить таблиці екземпляра, не видалена й не прихована (зріз їх не віддає).
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == instance.TableDefId);
        var column = table?.Columns.FirstOrDefault(c => c.Id == columnDefId);
        if (table is null || column is null || column.IsDeleted || column.IsHidden)
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        // 5. Q-17: колонка іншого власника, чий період поза областю ролі читача, у зрізі замаскована.
        if (await OwnerBlockedAsync(documentId, table, period, anyPeriod, columnDefId, ct).ConfigureAwait(false))
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        // 6. Рядок існує в екземплярі.
        var rowIds = await rows.GetRowIdsAsync(tableInstanceId, period, ct).ConfigureAwait(false);
        if (!rowIds.ContainsKey(rowKey))
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        var maps = await store.MapsAsync(columnDefId, rowKey, MaxMaps, ct).ConfigureAwait(false);
        var window = await store.PeriodAsync(documentId, instance.PeriodKey, ct).ConfigureAwait(false)
                     ?? throw DocumentVisibility.DocumentNotFound(documentId);

        // ⚠ Ті самі межі, за якими згортає матеріалізація (`MaterializationTarget.UtcBounds`, D16-03).
        var bounds = Domain.Entities.Documents.Period.UtcBounds(
            window.PeriodStart, window.PeriodEnd, SiteTimeZone.Create(window.TimeZoneId).ToTimeZoneInfo());

        return new ResolvedCell(
            instance.PeriodKey,
            DateTime.SpecifyKind(bounds.StartUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(bounds.EndUtc, DateTimeKind.Utc),
            maps);
    }

    private async Task<bool> OwnerBlockedAsync(
        long documentId, Domain.Entities.Configuration.TableDef table, PeriodKey period, DocumentReadScope anyPeriod,
        int columnDefId, CancellationToken ct)
    {
        if (monthRouter is not { Enabled: true } || !monthRouter.AppliesToTable(table))
        {
            return false;
        }

        var document = await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return false;
        }

        var foreign = (await monthRouter.ForeignColumnsAsync(document, table, period, ct).ConfigureAwait(false))
            .FirstOrDefault(f => f.Column.Id == columnDefId);

        return foreign is not null && !anyPeriod.InPeriod(foreign.OwnerPeriod).CanReadColumn(columnDefId);
    }

    private sealed record ResolvedCell(int PeriodKey, DateTime FromUtc, DateTime ToUtc, IReadOnlyList<CellSourceMap> Maps);
}

/// <summary>Звідки комірка отримує значення з PI.</summary>
/// <param name="PeriodKey">Період комірки.</param>
/// <param name="PeriodFromUtc">Початок вікна згортки (UTC, у поясі проєкту).</param>
/// <param name="PeriodToUtc">Кінець вікна згортки (UTC, виключно).</param>
/// <param name="Sources">Мапінги, що живлять комірку; порожньо — значення не з PI.</param>
public sealed record CellSourceView(
    int PeriodKey, DateTime PeriodFromUtc, DateTime PeriodToUtc, IReadOnlyList<CellSourceItem> Sources);

/// <summary>Один мапінг, що живить комірку.</summary>
/// <param name="MapId">Мапінг — ключ для точок і графіка.</param>
/// <param name="SourceEntityId">Сутність збору.</param>
/// <param name="SourceEntityCode">Код сутності.</param>
/// <param name="SourceEntityName">Підпис сутності.</param>
/// <param name="SourcePath">Тег.</param>
/// <param name="Aggregation">Згортка періоду.</param>
/// <param name="SourceUnitCode">Одиниця джерела.</param>
/// <param name="TargetUnitCode">Одиниця документа.</param>
/// <param name="IsActive">Мапінг діє.</param>
/// <param name="PointCount">Зібраних точок тега за період.</param>
/// <param name="LastCollection">Останній прогін сутності й останній успіх.</param>
/// <param name="Events">Події покриття даних тега за період, новіші першими.</param>
public sealed record CellSourceItem(
    int MapId,
    int SourceEntityId,
    string SourceEntityCode,
    string? SourceEntityName,
    string SourcePath,
    string? Aggregation,
    string? SourceUnitCode,
    string? TargetUnitCode,
    bool IsActive,
    long PointCount,
    RawCollectionStatus LastCollection,
    IReadOnlyList<CellSourceEvent> Events);
