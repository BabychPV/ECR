using System.Data;
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICollectionStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// Джерело істини щодо того, за які інтервали дані вже є, — це
/// <c>itg.CollectionCoverage</c>, а не <c>Watermark</c> у розкладі:
/// watermark — оптимізація, а не стан, і його втрата не має коштувати даних
/// (ER-I-03).
/// </remarks>
public sealed class CollectionStore(EcrDbContext db, IClock clock) : ICollectionStore
{
    /// <summary>Стеля вибірки інтервалів покриття.</summary>
    private const int MaxIntervals = 100_000;

    /// <inheritdoc />
    public Task<SourceEntity?> FindSourceEntityAsync(int sourceEntityId, CancellationToken ct)
        => db.SourceEntities.FirstOrDefaultAsync(e => e.Id == sourceEntityId && e.IsActive, ct);

    /// <inheritdoc />
    public Task<DataSource?> FindDataSourceAsync(int dataSourceId, CancellationToken ct)
        => db.DataSources.FirstOrDefaultAsync(s => s.Id == dataSourceId && s.IsActive, ct);

    /// <inheritdoc />
    public async Task<long> StartRunAsync(
        int sourceEntityId,
        DateTime fromUtc,
        DateTime toUtc,
        bool isCatchUp,
        int? triggeredByUserId,
        CancellationToken ct)
    {
        var run = new CollectionRun(
            sourceEntityId, fromUtc, toUtc, isCatchUp, triggeredByUserId, clock.UtcNow);

        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return run.Id;
    }

    /// <inheritdoc />
    public async Task FinishRunAsync(
        long collectionRunId,
        string status,
        int pointsRetrieved,
        string? errorMessage,
        CancellationToken ct)
    {
        var run = await db.CollectionRuns
            .FirstOrDefaultAsync(r => r.Id == collectionRunId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Прогону збору {collectionRunId} не існує.");

        // ⚠ Відмова джерела — теж ЗАВЕРШЕННЯ, зі статусом і кодом. Прогін, що
        // лишився «Running» назавжди, виглядає як довгий: його чекають замість
        // того, щоб подивитися на джерело.
        run.Complete(status, pointsRetrieved, clock.UtcNow, errorMessage);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Upsert за ПРИРОДНИМ ключем, а не вставка. Повторний запуск того
    /// самого діапазону не має дублювати точок (ФВ-11.3) — саме це робить
    /// наздоганяння безпечним: інакше кожне відновлення після простою
    /// подвоювало б суму за період.
    /// <para>
    /// ⛔ Одним set-based <c>MERGE</c> на батч, без change tracker і без
    /// читання наявних точок у пам'ять (аудит B4/P6). Попередня форма —
    /// «прочитати наявні <c>Take(100 000)</c> → <c>Add</c> решту →
    /// <c>SaveChanges</c>» — мала три шляхи до <c>UQ_RawDataPoint</c>, і
    /// кожен відкидав ВЕСЬ батч:
    /// (а) мітка з точністю <c>DateTime</c> (100 нс) не знаходила збереженої
    /// <c>datetime2(3)</c>; (б) дві точки з однаковою міткою в одному батчі
    /// (PI це допускає) — друга не бачила першої; (в) понад стелю наявних
    /// точок решту не читали і вставляли вдруге. Тепер ключ порівнює сама
    /// база, мітка нормалізується до мілісекунд ДО порівняння, дублікат
    /// батча згортається (виграє ОСТАННЯ точка — як і раніше, коли пізніший
    /// <c>SetValue</c> перезаписував ранішній), а стелі читання немає зовсім.
    /// </para>
    /// <para>
    /// ⚠ Паралельний збір тієї самої сутності (ручний «зібрати зараз» поруч
    /// із плановим) серіалізується <c>sp_getapplock</c> на сутність у
    /// транзакції БАТЧА, а не прогону: лок живе мілісекунди, два прогони
    /// чергуються батчами і не чекають один одного годинами, а check-then-insert
    /// всередині <c>MERGE</c> більше не має вікна для гонки. Лок у задачі
    /// (<c>CollectionJob</c>) обрано НЕ було: він тримав би з'єднання весь
    /// прогін (до 15 хв) і не захищав би інших викликачів сховища.
    /// </para>
    /// </remarks>
    public async Task<int> UpsertRawPointsAsync(
        long collectionRunId,
        int sourceEntityId,
        IReadOnlyList<SourceDataPoint> points,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return 0;
        }

        // Так само одним запитом на батч, не по точці: у батчі — тисячі
        // точок, але зазвичай лічені одиниці джерела (усі точки одного
        // джерела зазвичай в одній одиниці) — без цього кожна точка тягла б
        // окремий SELECT до uom.Unit (N+1).
        var unitIds = await ResolveUnitsAsync(points, ct).ConfigureAwait(false);

        // ⛔ Значення лягає В ОДИНИЦІ ДЖЕРЕЛА (ФВ-16.10, D-79). Конвертувати
        // тут означало б, що повторний перерахунок з архіву дасть інший
        // результат, якщо мапінг одиниць за цей час змінили — і ніхто не
        // зможе сказати, яке число правильне.
        var rows = points.Select((point, ordinal) => new RawPointRow(
                ordinal,
                point.SourcePath,
                ToStoredPrecision(point.Timestamp).ToString(StoredTimestampFormat, CultureInfo.InvariantCulture),
                point.ValueNumeric?.ToString(CultureInfo.InvariantCulture),
                point.ValueString,
                point.SourceUnitSymbol is not null && unitIds.TryGetValue(point.SourceUnitSymbol, out var unitId)
                    ? unitId
                    : null,
                point.Quality))
            .ToList();

        var written = new SqlParameter("@written", SqlDbType.Int) { Direction = ParameterDirection.Output };

        await db.Database
            .ExecuteSqlRawAsync(
                UpsertRawPointsSql,
                [
                    new SqlParameter("@points", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(rows) },
                    new SqlParameter("@entity", SqlDbType.Int) { Value = sourceEntityId },
                    new SqlParameter("@run", SqlDbType.BigInt) { Value = collectionRunId },
                    new SqlParameter("@retrievedAt", SqlDbType.DateTime2) { Scale = 3, Value = clock.UtcNow },
                    new SqlParameter("@resource", SqlDbType.NVarChar, 255)
                    {
                        Value = string.Create(CultureInfo.InvariantCulture, $"Ecr.RawDataPoint:{sourceEntityId}"),
                    },
                    written,
                ],
                ct)
            .ConfigureAwait(false);

        return written.Value is int count ? count : 0;
    }

    /// <summary>Точність <c>ext.RawDataPoint.Timestamp</c> — <c>datetime2(3)</c>, тобто мілісекунди.</summary>
    /// <remarks>
    /// ⚠ ВІДКИДАННЯ, а не округлення: саме так <c>SqlClient</c> пише
    /// <c>DateTime</c> у параметр <c>datetime2(3)</c> (ділить тіки націло), і
    /// так лягли всі точки, записані до цієї зміни через EF. Округлення дало б
    /// для <c>…:00.1236</c> ключ <c>.124</c> там, де в базі вже лежить
    /// <c>.123</c>, — і та сама точка з'явилася б удруге на мілісекунду пізніше.
    /// </remarks>
    /// <param name="timestamp">Мітка часу від джерела.</param>
    /// <returns>Мітка, обрізана до мілісекунд; <see cref="DateTime.Kind"/> зберігається.</returns>
    public static DateTime ToStoredPrecision(DateTime timestamp)
        => new(timestamp.Ticks - (timestamp.Ticks % TimeSpan.TicksPerMillisecond), timestamp.Kind);

    /// <summary>Формат мітки в JSON-параметрі: ISO без зони, рівно три знаки дробу.</summary>
    /// <remarks>⚠ Без <c>Z</c>: <c>datetime2</c> в <c>OPENJSON … WITH</c> суфікс зони не приймає (див. <c>MaterializationTargets</c>).</remarks>
    private const string StoredTimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff";

    /// <summary>Скільки чекати лок сутності, мс; менше за <c>CommandTimeout</c> (60 с).</summary>
    private const int RawPointLockTimeoutMs = 30_000;

    /// <summary>Set-based upsert батча сирих точок.</summary>
    /// <remarks>
    /// ⚠ Типи в <c>OPENJSON … WITH</c> навмисно ШИРШІ за колонки
    /// (<c>nvarchar(4000)</c> проти <c>nvarchar(400)</c>): вужчий тип мовчки
    /// обрізав би шлях чи текст, і точка лягла б під чужим ключем. Із широким
    /// задовгий рядок падає на вставці так само голосно, як падав через EF.
    /// Число — рядком і <c>CAST</c>: JSON-число могло б пройти через
    /// <c>float</c> (D-30).
    /// <para>
    /// ⚠ <c>SET XACT_ABORT ON</c>: будь-яка помилка відкочує транзакцію
    /// батча цілком і звільняє лок; <c>EnableRetryOnFailure</c> повторює
    /// весь пакет — він ідемпотентний.
    /// </para>
    /// </remarks>
    private static readonly string UpsertRawPointsSql = $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DECLARE @lock int;
        EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                                   @LockOwner = 'Transaction', @LockTimeout = {RawPointLockTimeoutMs};
        IF @lock < 0
            THROW 50001, N'ext.RawDataPoint: не вдалося взяти лок сутності джерела для запису точок (sp_getapplock < 0).', 1;

        WITH batch AS (
            SELECT j.SourcePath,
                   j.Ts,
                   CAST(j.Num AS decimal(34,16)) AS ValueNumeric,
                   j.Str AS ValueString,
                   j.UnitId,
                   j.Quality,
                   ROW_NUMBER() OVER (PARTITION BY j.SourcePath, j.Ts ORDER BY j.Ord DESC) AS rn
            FROM OPENJSON(@points)
                 WITH (Ord int '$.Ordinal',
                       SourcePath nvarchar(4000) '$.SourcePath',
                       Ts datetime2(3) '$.Timestamp',
                       Num nvarchar(64) '$.ValueNumeric',
                       Str nvarchar(max) '$.ValueString',
                       UnitId int '$.UnitId',
                       Quality nvarchar(4000) '$.Quality') AS j
        )
        MERGE ext.RawDataPoint AS t
        USING (SELECT SourcePath, Ts, ValueNumeric, ValueString, UnitId, Quality FROM batch WHERE rn = 1) AS s
           ON t.SourceEntityId = @entity AND t.SourcePath = s.SourcePath AND t.[Timestamp] = s.Ts
        WHEN MATCHED THEN
            UPDATE SET ValueNumeric = s.ValueNumeric, ValueString = s.ValueString,
                       UnitId = s.UnitId, Quality = s.Quality
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, ValueString, UnitId, Quality,
                    RetrievedAt, CollectionRunId)
            VALUES (@entity, s.SourcePath, s.Ts, s.ValueNumeric, s.ValueString, s.UnitId, s.Quality,
                    @retrievedAt, @run);

        SET @written = @@ROWCOUNT;

        COMMIT TRANSACTION;
        """;

    /// <summary>Рядок JSON-параметра upsert'а; імена властивостей — шляхи в <c>OPENJSON … WITH</c>.</summary>
    /// <param name="Ordinal">Позиція в батчі: за однакового ключа виграє пізніша.</param>
    /// <param name="SourcePath">Шлях атрибута.</param>
    /// <param name="Timestamp">Мітка, уже обрізана до мілісекунд, рядком ISO.</param>
    /// <param name="ValueNumeric">Число рядком (інваріантна культура).</param>
    /// <param name="ValueString">Текст.</param>
    /// <param name="UnitId">Одиниця джерела.</param>
    /// <param name="Quality">Якість від джерела.</param>
    private sealed record RawPointRow(
        int Ordinal,
        string SourcePath,
        string Timestamp,
        string? ValueNumeric,
        string? ValueString,
        int? UnitId,
        string? Quality);

    /// <inheritdoc />
    public async Task WriteCoverageAsync(
        long collectionRunId,
        int sourceEntityId,
        IReadOnlyList<TimeInterval> covered,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(covered);

        if (covered.Count == 0)
        {
            return;
        }

        foreach (var interval in covered)
        {
            db.CollectionCoverages.Add(
                new CollectionCoverage(sourceEntityId, interval.FromUtc, interval.ToUtc, collectionRunId));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeInterval>> GetCoverageAsync(
        int sourceEntityId, DateTime notBefore, CancellationToken ct)
        => await db.CollectionCoverages
            .AsNoTracking()
            .Where(c => c.SourceEntityId == sourceEntityId && c.CoveredTo >= notBefore)
            .OrderBy(c => c.CoveredFrom)
            .Take(MaxIntervals)
            .Select(c => new TimeInterval(c.CoveredFrom, c.CoveredTo))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Стелі вибірки тут немає навмисно — вона вже є в самому запиті:
    /// мапінгів на одну сутність джерела одиниці, і <c>Take</c> міг би тихо
    /// відрізати саме той, чия одиниця змінилася.
    /// <para>
    /// ⛔ Лише мапінги на КОЛОНКУ (<see cref="FieldTargetKind.Column"/>).
    /// Атрибут, змаплений на поле довідника, — не часовий ряд: його поточне
    /// значення читає <c>RegistrySyncJob</c> (<c>ФВ-8.11</c>, S5). Без звуження
    /// збирач тягнув би історію атрибута довідника в <c>ext.RawDataPoint</c>,
    /// де її не читає ніхто, а зміна його одиниці ставила б на паузу мапінг,
    /// якого збір не стосується.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<EntityFieldMap>> GetFieldMapsAsync(
        int sourceEntityId, CancellationToken ct)
        => await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.SourceEntityId == sourceEntityId && m.IsActive && m.TargetKind == FieldTargetKind.Column)
            .OrderBy(m => m.SourceField)
            .Take(MaxFieldMaps)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Стеля вибірки мапінгів; тисяча полів на одну сутність — вже аварія.</summary>
    private const int MaxFieldMaps = 1_000;

    /// <inheritdoc />
    public async Task<EntityFieldMap> AddFieldMapAsync(EntityFieldMap map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);

        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return map;
    }

    /// <inheritdoc />
    public Task<EntityFieldMap?> FindFieldMapAsync(int fieldMapId, CancellationToken ct)
        => db.EntityFieldMaps.FirstOrDefaultAsync(m => m.Id == fieldMapId, ct);

    /// <inheritdoc />
    public async Task PauseForSourceUnitChangeAsync(
        int fieldMapId, string actualUnitCode, int? actualUnitId, CancellationToken ct)
    {
        // Мапінги збору читаються без відстеження, тож пауза — окремим
        // відстежуваним читанням і власним збереженням.
        var map = await db.EntityFieldMaps
            .FirstOrDefaultAsync(m => m.Id == fieldMapId, ct)
            .ConfigureAwait(false);

        if (map is null)
        {
            return;
        }

        map.PauseForSourceUnitChange(actualUnitCode, actualUnitId, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveFieldMapAsync(EntityFieldMap map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);

        db.EntityFieldMaps.Remove(map);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CollectedFieldStats> CountCollectedAsync(
        int sourceEntityId, string sourceField, CancellationToken ct)
    {
        // ⚠ Один запит на три числа, а не три запити. Лічильник тут — це
        // подробиця відмови, і платити за неї трьома походами в таблицю, у
        // якій мільйони рядків, не варто.
        var stats = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId && p.SourcePath == sourceField)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Points = g.Count(),
                FirstAt = (DateTime?)g.Min(p => p.Timestamp),
                LastAt = (DateTime?)g.Max(p => p.Timestamp),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return stats is null
            ? new CollectedFieldStats(0, null, null)
            : new CollectedFieldStats(stats.Points, stats.FirstAt, stats.LastAt);
    }

    /// <inheritdoc />
    public Task<string?> FindUnitCodeAsync(int unitId, CancellationToken ct)
        => db.Units
            .AsNoTracking()
            .Where(u => u.Id == unitId)
            .Select(u => (string?)u.Code)
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public Task<bool> ColumnDefExistsAsync(int columnDefId, CancellationToken ct)
        => db.ColumnDefs.AsNoTracking().AnyAsync(c => c.Id == columnDefId && !c.IsDeleted, ct);

    /// <inheritdoc />
    public Task<bool> UnitExistsAsync(int unitId, CancellationToken ct)
        => db.Units.AsNoTracking().AnyAsync(u => u.Id == unitId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceEntityStatus>> ListSourceEntitiesAsync(CancellationToken ct)
    {
        // ⚠ Три запити на весь перелік, а не три на кожну сутність: джерел
        // десятки, і N+1 тут перетворив би екран конфігуратора на сотні
        // звернень до бази.
        var entities = await db.SourceEntities
            .AsNoTracking()
            .OrderBy(e => e.Code)
            .Take(MaxSourceEntities)
            .Select(e => new
            {
                e.Id,
                e.Code,
                e.DisplayName,
                e.EntityPath,
                e.IsActive,
                e.DataSourceId,
                e.RegistryDefId,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (entities.Count == 0)
        {
            return [];
        }

        var ids = entities.ConvertAll(e => e.Id);

        // ⚠ Лише з'єднання цих сутностей: зовнішній ключ гарантує, що кожне
        // знайдеться, тож код з'єднання ніколи не підміняється заглушкою.
        var dataSourceIds = entities.Select(e => e.DataSourceId).Distinct().ToList();

        var transports = await db.DataSources
            .AsNoTracking()
            .Where(s => dataSourceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Transport, s.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Останній прогін кожної сутності: беремо всі завершені за стелею і
        // згортаємо в пам'яті — вибірка на кожну сутність коштувала б N запитів.
        var runs = await db.CollectionRuns
            .AsNoTracking()
            .Where(r => ids.Contains(r.SourceEntityId))
            .OrderByDescending(r => r.StartedAt)
            .Take(MaxRunsScanned)
            .Select(r => new { r.SourceEntityId, r.FinishedAt, r.Status, r.PointsRetrieved, r.StartedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var coverage = await db.CollectionCoverages
            .AsNoTracking()
            .Where(c => ids.Contains(c.SourceEntityId))
            .OrderBy(c => c.CoveredFrom)
            .Take(MaxIntervals)
            .Select(c => new { c.SourceEntityId, c.CoveredFrom, c.CoveredTo })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var transportById = transports.ToDictionary(s => s.Id, s => s.Transport.ToString());
        var codeById = transports.ToDictionary(s => s.Id, s => s.Code);

        var lastRun = runs
            .GroupBy(r => r.SourceEntityId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.StartedAt).First());

        var now = clock.UtcNow;

        return entities.ConvertAll(entity =>
        {
            var run = lastRun.GetValueOrDefault(entity.Id);

            var intervals = coverage
                .Where(c => c.SourceEntityId == entity.Id)
                .Select(c => new TimeInterval(c.CoveredFrom, c.CoveredTo))
                .ToList();

            // ⚠ Прогалина шукається ТИМ САМИМ кодом, що й наздоганяння:
            // друга реалізація «що таке дірка» показувала б на екрані одне,
            // а збирала б інше.
            var gaps = Ecr.Application.Integration.GapFinder.Find(intervals, now.AddDays(-GapLookbackDays), now);

            return new SourceEntityStatus(
                entity.Id,
                entity.Code,
                entity.DisplayName,
                entity.EntityPath,
                transportById.GetValueOrDefault(entity.DataSourceId, "—"),
                entity.IsActive,
                run is null ? null : new CollectionRunStatus(run.FinishedAt, run.Status, run.PointsRetrieved),
                gaps.Count == 0 ? null : gaps[0].From,
                entity.DataSourceId,
                codeById[entity.DataSourceId],
                entity.RegistryDefId);
        });
    }

    /// <inheritdoc />
    public Task<bool> SourceEntityCodeExistsAsync(int dataSourceId, string code, CancellationToken ct)
        => db.SourceEntities.AsNoTracking().AnyAsync(e => e.DataSourceId == dataSourceId && e.Code == code, ct);

    /// <inheritdoc />
    public async Task<SourceEntity> AddSourceEntityAsync(SourceEntity entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);

        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return entity;
    }

    /// <inheritdoc />
    public Task SaveSourceEntityAsync(SourceEntity entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public Task<bool> RegistryDefExistsAsync(int registryDefId, CancellationToken ct)
        => db.RegistryDefs.AsNoTracking().AnyAsync(r => r.Id == registryDefId, ct);

    /// <inheritdoc />
    public Task<int?> FindRegistryFieldOwnerAsync(int registryFieldDefId, CancellationToken ct)
        => db.RegistryFieldDefs
            .AsNoTracking()
            .Where(f => f.Id == registryFieldDefId)
            .Select(f => (int?)f.RegistryDefId)
            .FirstOrDefaultAsync(ct);

    /// <summary>Стеля переліку сутностей збору.</summary>
    private const int MaxSourceEntities = 5_000;

    /// <summary>Скільки прогонів проглядати, шукаючи останній по кожній сутності.</summary>
    private const int MaxRunsScanned = 20_000;

    /// <summary>Наскільки глибоко екран шукає прогалини.</summary>
    /// <remarks>
    /// Сорок п'ять діб — звітний місяць плюс пільговий строк, той самий обрій,
    /// що й у наздоганянні (<c>CollectionRunner.CatchUpLookback</c>). Різні
    /// обрії давали б екран, який показує «все добре», поки збирач наздоганяє.
    /// </remarks>
    private const int GapLookbackDays = 45;

    /// <summary>Одиниці джерела за їхніми символами — одним запитом на весь батч.</summary>
    /// <remarks>
    /// ⚠ Нерозпізнаний символ не потрапляє у словник, і виклик через
    /// <c>TryGetValue</c> дає <c>null</c>, а не здогадку. Одиниця, взята
    /// навмання, — це число, помножене невідомо на що; порожня одиниця
    /// принаймні видима у звіті про збір (ФВ-16.12). Один запит
    /// <c>WHERE Code IN (...)</c>, а не по запиту на точку: у типовому батчі
    /// — лічені РІЗНІ символи одиниць (усі точки одного джерела зазвичай в
    /// одній), тож запит на кожну точку окремо був би N+1 без жодної
    /// причини.
    /// </remarks>
    private async Task<Dictionary<string, int?>> ResolveUnitsAsync(
        IReadOnlyList<SourceDataPoint> points, CancellationToken ct)
    {
        var symbols = points
            .Where(p => !string.IsNullOrWhiteSpace(p.SourceUnitSymbol))
            .Select(p => p.SourceUnitSymbol!)
            .Distinct()
            .ToList();

        if (symbols.Count == 0)
        {
            return [];
        }

        var units = await db.Units
            .AsNoTracking()
            .Where(u => symbols.Contains(u.Code))
            .Select(u => new { u.Code, u.Id })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return units.ToDictionary(u => u.Code, u => (int?)u.Id);
    }
}
