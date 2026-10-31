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

    /// <summary>
    /// Стеля точок, які «значення за правилом згортки» читає в пам'ять (як стеля перегляду мапінгу, <c>ФВ-13.14</c>);
    /// понад неї — <see cref="CellFoldState.TooManyPoints"/>, а не часткове число.
    /// </summary>
    public const int FoldMaxPoints = 50_000;

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
            var last = await reader.LastCollectionAsync(map.SourceEntityId, ct).ConfigureAwait(false);
            var events = await store
                .PeriodEventsAsync(map.SourceEntityId, cell.WindowPeriodKey, EventScan, ct)
                .ConfigureAwait(false);

            long count = 0;
            CellFoldView? fold = null;
            if (cell.Window is { } window)
            {
                var key = new RawSeriesKey(map.SourceEntityId, map.SourcePath, window.StartUtc, window.EndUtc);
                count = await reader.CountAsync(key, ct).ConfigureAwait(false);
                var folded = await store.FoldAsync(map, key, FoldMaxPoints, ct).ConfigureAwait(false);
                fold = Compare(folded, cell.Last, cell.Scale);
            }

            items.Add(new CellSourceItem(
                map.MapId, map.SourceEntityId, map.SourceEntityCode, map.SourceEntityName, map.SourcePath,
                map.Aggregation, map.SourceUnitCode, map.TargetUnitCode, map.IsActive, count, last,
                [.. events.Where(e => Concerns(e, map)).Take(MaxEvents)],
                fold));
        }

        return new CellSourceView(
            cell.PeriodKey,
            cell.Window?.StartUtc,
            cell.Window?.EndUtc,
            items,
            cell.WindowPeriodKey,
            cell.Month);
    }

    /// <summary>
    /// Порівнює значення за правилом згортки з тим, що записано в комірці, — лише те, що відомо з журналу
    /// змін ЦІЄЇ комірки (без журналу покриття, який не знає документа).
    /// </summary>
    /// <param name="fold">Згортка.</param>
    /// <param name="last">Останній запис журналу змін комірки; <c>null</c> — змін не записано.</param>
    /// <param name="scale">Масштаб колонки; числа порівнюються після округлення до нього.</param>
    public static CellFoldView Compare(CellFold fold, CellLastWrite? last, int? scale)
    {
        ArgumentNullException.ThrowIfNull(fold);

        var stored = last?.NewValue;
        CellFoldComparison comparison;
        if (fold.State != CellFoldState.Value || fold.Value is not { } value)
        {
            comparison = CellFoldComparison.NotComparable;
        }
        else if (last is null)
        {
            comparison = CellFoldComparison.NotWritten;
        }
        else if (decimal.TryParse(stored, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var cellValue)
                 && Round(cellValue, scale) == Round(value, scale))
        {
            comparison = CellFoldComparison.Match;
        }
        else
        {
            comparison = Documents.CellChangeOrigins.IsHuman(last.Origin)
                ? CellFoldComparison.ManualEdit
                : CellFoldComparison.NotUpdated;
        }

        return new CellFoldView(
            fold.State.ToString(), fold.Value, fold.PercentGood, stored, last?.Origin, last?.ChangedAt, comparison.ToString());
    }

    private static decimal Round(decimal value, int? scale)
        => decimal.Round(value, Math.Clamp(scale ?? 16, 0, 28), MidpointRounding.AwayFromZero);

    /// <summary>Сторінка сирих точок тега мапінгу в межах періоду комірки, новіші першими.</summary>
    /// <exception cref="Errors.NotFoundException">Комірку не видно або мапінг не її.</exception>
    /// <exception cref="Errors.BusinessRuleException">Розмір сторінки поза межами.</exception>
    public async Task<PagedResult<RawPointView>> PointsAsync(
        long documentId, long tableInstanceId, string? rowKey, int columnDefId, int mapId, string? cursor, int limit,
        CancellationToken ct)
    {
        var size = RawDataQuery.PageSize(limit);
        var key = await KeyAsync(documentId, tableInstanceId, rowKey, columnDefId, mapId, ct).ConfigureAwait(false);

        // ⚠ Колонка місяця поза періодом без власника — не комірка цього періоду: точок немає.
        return key is null
            ? new PagedResult<RawPointView>([], null, null)
            : await RawDataQuery.PageAsync(reader, units, key, cursor, size, ct).ConfigureAwait(false);
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

        return key is null
            ? new RawSeriesView(DateTime.UnixEpoch, DateTime.UnixEpoch, 1, 0, 0, [])
            : await RawDataQuery.SeriesAsync(reader, key, count, ct).ConfigureAwait(false);
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

    private async Task<RawSeriesKey?> KeyAsync(
        long documentId, long tableInstanceId, string? rowKey, int columnDefId, int mapId, CancellationToken ct)
    {
        var cell = await ResolveAsync(documentId, tableInstanceId, rowKey, columnDefId, ct).ConfigureAwait(false);

        // ⛔ Мапінг, що не живить цю комірку, — та сама 404: інакше `mapId` став би ключем до точок
        // будь-якого тега системи через будь-яку видиму комірку.
        var map = cell.Maps.FirstOrDefault(m => m.MapId == mapId)
                  ?? throw DocumentVisibility.DocumentNotFound(documentId);

        return cell.Window is { } window
            ? new RawSeriesKey(map.SourceEntityId, map.SourcePath, window.StartUtc, window.EndUtc)
            : null;
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

        // 5. Q-17: колонка іншого власника, чий період поза областю ролі читача, у зрізі замаскована;
        //    видима — згортається в періоді-ВЛАСНИКУ.
        var owner = await OwnerPeriodAsync(documentId, table, period, columnDefId, ct).ConfigureAwait(false);
        if (owner is { } ownerKey && !anyPeriod.InPeriod(ownerKey).CanReadColumn(columnDefId))
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

        // 7. Вікно — ТЕ САМЕ правило, що в матеріалізації (`Period.FoldBounds`, D-462): колонка місяця річного
        //    періоду — лише свій місяць; місяць поза періодом екземпляра — у періоді-власнику (Q-17).
        var month = column.IsMonthColumn && column.MonthNumber is { } m ? (int?)m : null;
        var own = await store.PeriodAsync(documentId, instance.PeriodKey, ct).ConfigureAwait(false)
                  ?? throw DocumentVisibility.DocumentNotFound(documentId);
        var windowPeriod = instance.PeriodKey;
        var window = Domain.Entities.Documents.Period.FoldBounds(
            own.PeriodStart, own.PeriodEnd, month, SiteTimeZone.Create(own.TimeZoneId).ToTimeZoneInfo());

        if (window is null && owner is { } ownerPeriod
            && await store.PeriodAsync(documentId, ownerPeriod.Value, ct).ConfigureAwait(false) is { } other)
        {
            windowPeriod = ownerPeriod.Value;
            window = Domain.Entities.Documents.Period.FoldBounds(
                other.PeriodStart, other.PeriodEnd, month, SiteTimeZone.Create(other.TimeZoneId).ToTimeZoneInfo());
        }

        // 8. Що записано в комірці: останній запис журналу змін — в екземплярі й (Q-17) у періоді-власнику.
        var last = maps.Count == 0
            ? null
            : await store
                .LastWriteAsync(
                    documentId, instance.TableDefId, rowKey, columnDefId,
                    [instance.PeriodKey, owner?.Value ?? instance.PeriodKey], ct)
                .ConfigureAwait(false);

        Domain.Entities.Documents.Period.UtcRange? utc = window is { } w
            ? new Domain.Entities.Documents.Period.UtcRange(
                DateTime.SpecifyKind(w.StartUtc, DateTimeKind.Utc), DateTime.SpecifyKind(w.EndUtc, DateTimeKind.Utc))
            : null;

        return new ResolvedCell(instance.PeriodKey, windowPeriod, month, utc, maps, last, column.Scale);
    }

    /// <summary>Q-17: період-власник колонки, якщо таблиця маршрутизована й колонка чужа; інакше <c>null</c>.</summary>
    private async Task<PeriodKey?> OwnerPeriodAsync(
        long documentId, Domain.Entities.Configuration.TableDef table, PeriodKey period, int columnDefId, CancellationToken ct)
    {
        if (monthRouter is not { Enabled: true } || !monthRouter.AppliesToTable(table))
        {
            return null;
        }

        var document = await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var foreign = (await monthRouter.ForeignColumnsAsync(document, table, period, ct).ConfigureAwait(false))
            .FirstOrDefault(f => f.Column.Id == columnDefId);

        return foreign?.OwnerPeriod;
    }

    private sealed record ResolvedCell(
        int PeriodKey,
        int WindowPeriodKey,
        int? Month,
        Domain.Entities.Documents.Period.UtcRange? Window,
        IReadOnlyList<CellSourceMap> Maps,
        CellLastWrite? Last,
        byte? Scale);
}

/// <summary>Звідки комірка отримує значення з PI.</summary>
/// <param name="PeriodKey">Період екземпляра комірки.</param>
/// <param name="PeriodFromUtc">Початок вікна згортки (UTC); <c>null</c> — колонка місяця поза періодом без власника.</param>
/// <param name="PeriodToUtc">Кінець вікна згортки (UTC, виключно).</param>
/// <param name="Sources">Мапінги, що живлять комірку; порожньо — значення не з PI.</param>
/// <param name="WindowPeriodKey">Період, у якому згортається комірка (Q-17 — період-власник колонки).</param>
/// <param name="WindowMonth">Місяць колонки місяця (1..12): вікно — лише цей місяць періоду (D-462); <c>null</c> — увесь період.</param>
public sealed record CellSourceView(
    int PeriodKey,
    DateTime? PeriodFromUtc,
    DateTime? PeriodToUtc,
    IReadOnlyList<CellSourceItem> Sources,
    int WindowPeriodKey,
    int? WindowMonth);

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
/// <param name="PointCount">Зібраних точок тега за вікно.</param>
/// <param name="LastCollection">Останній прогін сутності й останній успіх.</param>
/// <param name="Events">Події покриття даних тега за період, новіші першими.</param>
/// <param name="Fold">Значення за правилом згортки й порівняння з коміркою; <c>null</c> — вікна немає.</param>
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
    IReadOnlyList<CellSourceEvent> Events,
    CellFoldView? Fold);

/// <summary>Значення за правилом згортки поруч зі значенням комірки.</summary>
/// <param name="State">Стан згортки (<see cref="CellFoldState"/>).</param>
/// <param name="Value">Значення в одиниці документа — те, що записала б матеріалізація.</param>
/// <param name="PercentGood">Частка придатних точок / покритого часу.</param>
/// <param name="StoredValue">Значення в комірці за останнім записом журналу змін; <c>null</c> — записів немає.</param>
/// <param name="StoredOrigin">Хто записав (<c>Integration</c>, <c>UserEdit</c>, <c>Import</c>…).</param>
/// <param name="StoredAt">Коли (UTC).</param>
/// <param name="Comparison">
/// <see cref="CellFoldComparison"/>: <c>Match</c>; <c>ManualEdit</c> — інше значення, останній запис людини;
/// <c>NotUpdated</c> — інше значення, останній запис машини (точки змінилися після запису); <c>NotWritten</c> —
/// записів у комірку не було (значення збору не записано); <c>NotComparable</c> — числа за правилом немає.
/// </param>
public sealed record CellFoldView(
    string State,
    decimal? Value,
    decimal? PercentGood,
    string? StoredValue,
    string? StoredOrigin,
    DateTime? StoredAt,
    string Comparison);

/// <summary>Порівняння значення за правилом згортки з коміркою.</summary>
public enum CellFoldComparison
{
    /// <summary>Збігається.</summary>
    Match,

    /// <summary>У комірці інше значення; останній запис — людини.</summary>
    ManualEdit,

    /// <summary>У комірці інше значення; останній запис — машини (не оновлено після нових точок).</summary>
    NotUpdated,

    /// <summary>Записів у комірку не було — значення збору не записано.</summary>
    NotWritten,

    /// <summary>Числа за правилом немає — порівнювати нема з чим.</summary>
    NotComparable,
}
