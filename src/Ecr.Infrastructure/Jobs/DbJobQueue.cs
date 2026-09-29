// src/Ecr.Infrastructure/Jobs/DbJobQueue.cs
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Черга фонових задач у <c>itg.JobProgress</c> (<c>MI-02</c>, <c>D14-01</c>, <c>D-208</c>).
/// </summary>
/// <remarks>
/// ⚠ Кожен запит — сирий SQL на з'єднанні <see cref="EcrDbContext"/> і в його
/// ПОТОЧНІЙ транзакції: постановка, відкочена разом із бізнес-зміною, не лишає
/// рядка. Моменти черги (<c>AvailableAt</c>, <c>LeaseUntil</c>) — лише
/// <c>SYSUTCDATETIME()</c>; <see cref="IClock"/> — для колонок показу.
///
/// ⚠ Перед кожним запитом — <c>SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON</c>
/// (правка В «Аудиту»): без них запис у таблицю з фільтрованими індексами
/// відкидається помилкою 1934, а сесію могли лишити з OFF (див. <see cref="RunAsync{T}"/>).
///
/// ⛔ Лейн у SQL — лише параметр <c>@lane</c> зі значенням з <c>JobLanes.All</c>,
/// жодної конкатенації вводу; тип <c>varchar(32)</c> явно — <c>nvarchar</c>
/// дав би CONVERT_IMPLICIT на ключі <c>IX_JobProgress_Claim</c>.
///
/// ⚠ Claim іде по ОДНОМУ лейну за запит, а не <c>Lane IN (…)</c>: з рівністю
/// порядок індексу (Lane, State, AvailableAt, JobId) збігається з ORDER BY, і
/// TOP(1) зупиняється на першому рядку. <c>IN</c> дав би сортування всіх
/// кандидатів — і UPDLOCK на кожному з них до кінця транзакції захоплювача,
/// тобто сусідній хост із READPAST не бачив би жодного.
/// </remarks>
public sealed class DbJobQueue(EcrDbContext db, IClock clock) : IJobQueue
{
    private const string SessionOptions = "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;\n";

    // Спільна частина UPDATE захоплення: новий токен, власник, оренда за годинником СУБД.
    private const string ClaimSet = """
            ClaimToken = @token, InstanceId = @owner,
            LeaseUntil = DATEADD(millisecond, @leaseMs, SYSUTCDATETIME()),
            HeartbeatAt = @shown, UpdatedAt = @shown
        """;

    private const string ClaimOutput = """
        OUTPUT inserted.JobId, inserted.JobCode, inserted.Lane, inserted.Payload, inserted.TargetKey,
               ISNULL(inserted.Attempt, 0), ISNULL(inserted.ReclaimCount, 0), inserted.LeaseUntil,
               inserted.CreatedByUserId, inserted.CorrelationId, inserted.DocumentId
        """;

    /// <summary>Переклейм простроченої <c>Running</c>: Attempt не чіпає, ReclaimCount + 1 (правка Б).</summary>
    internal const string ReclaimSql = """
        /* ecr:jobqueue-reclaim */
        WITH candidate AS (
            SELECT TOP (1) q.*
            FROM itg.JobProgress AS q WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE q.Lane = @lane AND q.Lane IS NOT NULL
              AND q.[State] = 'Running' AND q.LeaseUntil < CAST(SYSUTCDATETIME() AS datetime2(3))
              AND q.CancelRequestedAt IS NULL
              AND ISNULL(q.ReclaimCount, 0) < @maxReclaims
            ORDER BY q.AvailableAt, q.JobId)
        UPDATE candidate SET
        """ + "\n" + ClaimSet + "\n" + """
            , ReclaimCount = ISNULL(ReclaimCount, 0) + 1
        """ + "\n" + ClaimOutput + ";";

    /// <summary>
    /// Захоплення <c>Queued</c>: один UPDATE по CTE TOP(1). NOT EXISTS — БЕЗ
    /// READPAST (правка А): <c>Running</c> під чужим локом читається останньою
    /// закоміченою версією (RCSI), а не пропускається.
    /// </summary>
    /// <remarks>
    /// ⛔ «Зараз» — <c>CAST(SYSUTCDATETIME() AS datetime2(3))</c>, не голий
    /// <c>SYSUTCDATETIME()</c>: <c>AvailableAt</c> пишеться як <c>datetime2(3)</c>, а
    /// перетворення ОКРУГЛЮЄ (до 0,5 мс угору). Без CAST задача, поставлена в ту
    /// саму мілісекунду, що й claim, лежала «в майбутньому» і claim повертав null.
    /// Те саме для <c>LeaseUntil</c> у переклеймі й <see cref="ExpireAsync"/>.
    /// </remarks>
    internal const string ClaimQueuedSql = """
        /* ecr:jobqueue-claim */
        WITH candidate AS (
            SELECT TOP (1) q.*
            FROM itg.JobProgress AS q WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE q.Lane = @lane AND q.Lane IS NOT NULL
              AND q.[State] = 'Queued' AND q.AvailableAt <= CAST(SYSUTCDATETIME() AS datetime2(3))
              AND NOT EXISTS (
                  SELECT 1 FROM itg.JobProgress AS r
                  WHERE r.TargetKey = q.TargetKey AND r.TargetKey IS NOT NULL AND r.[State] = 'Running')
            ORDER BY q.AvailableAt, q.JobId)
        UPDATE candidate SET
            [State] = 'Running', Attempt = ISNULL(Attempt, 0) + 1, ReclaimCount = ISNULL(ReclaimCount, 0),
            CancelRequestedAt = NULL, StartedAt = @shown, [Percent] = 0, [Message] = NULL, Error = NULL, ErrorCode = NULL,
        """ + "\n" + ClaimSet + "\n" + ClaimOutput + ";";

    /// <summary>Змінні злиття масиву payload (<see cref="MergeArraysSql"/>).</summary>
    private const string MergeDeclarations = """
        DECLARE @basePayload nvarchar(max) = NULL, @addPayload nvarchar(max) = NULL, @merged nvarchar(max) = NULL;
        DECLARE @oldArray nvarchar(max), @addArray nvarchar(max), @oldBody nvarchar(max), @addBody nvarchar(max);
        """;

    /// <summary>
    /// <c>@merged</c> = <c>@basePayload</c>, у якого масив <c>@mergePath</c> доповнено
    /// елементами того самого масиву з <c>@addPayload</c>; <c>NULL</c> — не зливається.
    /// </summary>
    /// <remarks>
    /// ⚠ Склейкою тексту двох масивів, а не <c>JSON_MODIFY 'append'</c> поелементно:
    /// append копіює весь рядок на кожен елемент, і батч зі ста комірок у payload на
    /// сотні КБ коштував би десятки МБ копіювання в транзакції PATCH. Результат
    /// перевіряється <c>ISJSON</c>; не JSON, немає масиву в одному з payload або понад
    /// <c>@maxPayload</c> — <c>NULL</c>, і викликач не зливає (O1).
    /// </remarks>
    private const string MergeArraysSql = """
            SET @merged = NULL;
            IF ISJSON(@basePayload) = 1 AND ISJSON(@addPayload) = 1
            BEGIN
                SET @oldArray = JSON_QUERY(@basePayload, @mergePath);
                SET @addArray = JSON_QUERY(@addPayload, @mergePath);
                IF LEFT(@oldArray, 1) = N'[' AND LEFT(@addArray, 1) = N'['
                BEGIN
                    SET @oldBody = SUBSTRING(@oldArray, 2, LEN(@oldArray) - 2);
                    SET @addBody = SUBSTRING(@addArray, 2, LEN(@addArray) - 2);
                    SET @merged = JSON_MODIFY(@basePayload, @mergePath, JSON_QUERY(
                        N'[' + @oldBody + CASE WHEN @oldBody <> N'' AND @addBody <> N'' THEN N',' ELSE N'' END
                        + @addBody + N']'));
                    IF ISJSON(@merged) <> 1 OR LEN(@merged) > @maxPayload
                        SET @merged = NULL;
                END
            END
        """;

    /// <summary>
    /// Постановка з коалесценцією: UPDLOCK+HOLDLOCK тримає слот цілі в
    /// <c>UX_JobProgress_Target_Queued</c> до кінця транзакції.
    /// </summary>
    /// <remarks>
    /// ⛔ Підказка індексу і <c>TargetKey IS NOT NULL</c> поруч із <c>= @target</c>
    /// — не зайві: без них оптимізатор на малій таблиці обирає скан
    /// <c>PK_JobProgress</c> (перевірено планом), і HOLDLOCK бере діапазон на ВСЮ
    /// таблицю — постановки на різні цілі стають у чергу одна за одною. З
    /// підказкою діапазон — лише ключ цілі; якщо фільтр індексу колись перестане
    /// збігатися із запитом, SQL Server відмовить (8622), а не просканує мовчки.
    /// </remarks>
    internal const string EnqueueSql = """
        /* ecr:jobqueue-enqueue */
        BEGIN TRANSACTION;
        DECLARE @jobId nvarchar(100) = NULL, @outcome int = 0;
        DECLARE @available datetime2(3) = DATEADD(millisecond, @delayMs, SYSUTCDATETIME());
        """ + "\n" + MergeDeclarations + "\n" + """
        IF @target IS NOT NULL
            SELECT @jobId = q.JobId, @basePayload = q.Payload
            FROM itg.JobProgress AS q WITH (UPDLOCK, HOLDLOCK, INDEX(UX_JobProgress_Target_Queued))
            WHERE q.TargetKey = @target AND q.TargetKey IS NOT NULL AND q.[State] = 'Queued';
        IF @jobId IS NOT NULL AND @mergePath IS NOT NULL
        BEGIN
            SET @addPayload = @payload;
        """ + "\n" + MergeArraysSql + "\n" + """
            IF @merged IS NOT NULL
                UPDATE itg.JobProgress SET Payload = @merged WHERE JobId = @jobId;
            ELSE
            BEGIN
                -- Не зливається (межа payload, чужа форма) — наявна лишається в черзі як є, без цілі.
                UPDATE itg.JobProgress SET TargetKey = NULL WHERE JobId = @jobId;
                SET @jobId = NULL;
            END
        END
        IF @jobId IS NOT NULL
        BEGIN
            UPDATE itg.JobProgress
            SET AvailableAt = CASE WHEN AvailableAt > @available THEN @available ELSE AvailableAt END
            WHERE JobId = @jobId;
            SET @outcome = 1;
        END
        ELSE
        BEGIN
            INSERT INTO itg.JobProgress
                (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt,
                 CreatedByUserId, CorrelationId, DocumentId, Lane, Payload, AvailableAt, TargetKey, ReclaimCount)
            VALUES (@newId, @code, 'Queued', 0, @shown, @shown, @shown, @shown,
                    @user, @correlation, @document, @lane, @payload, @available, @target, 0);
            SET @jobId = @newId;
        END
        IF @supersede = 1 AND @target IS NOT NULL
            UPDATE r SET CancelRequestedAt = SYSUTCDATETIME()
            FROM itg.JobProgress AS r WITH (INDEX(UX_JobProgress_Target_Running))
            WHERE r.TargetKey = @target AND r.TargetKey IS NOT NULL AND r.[State] = 'Running'
              AND r.CancelRequestedAt IS NULL;
        COMMIT TRANSACTION;
        SELECT @jobId, @outcome;
        """;

    // Умова «оренда за цим токеном жива» — спільна для всіх дій власника.
    private const string OwnedBy = "WHERE JobId = @id AND ClaimToken = @token AND [State] = 'Running'";

    private const string AbsorbedMarker = "@@behind@@";

    // Бази, для яких RCSI уже підтверджено: «сервер|база» → 0. Лише ON (див. EnsureRcsiAsync).
    private static readonly ConcurrentDictionary<string, byte> RcsiConfirmed = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<JobEnqueueResult> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.JobCode);
        ArgumentNullException.ThrowIfNull(request.PayloadJson);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.JobCode.Length, 64, nameof(request));

        if (!JobLanes.IsKnown(request.Lane))
        {
            throw new ArgumentException($"Невідомий лейн «{request.Lane}»: лише JobLanes.All.", nameof(request));
        }

        // Сторож розміру: payload черги ≤ 1 МБ (D-208) — більше не є «аргументами задачі».
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            request.PayloadJson.Length, JobQueueLimits.MaxPayloadLength, nameof(request));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            request.TargetKey?.Length ?? 0, JobProgress.MaxTargetKeyLength, nameof(request));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            request.CorrelationId?.Length ?? 0, JobProgress.MaxCorrelationIdLength, nameof(request));
        var delay = request.Delay ?? TimeSpan.Zero;
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero, nameof(request));

        var newId = $"{ShortCode(request.JobCode)}-{Guid.NewGuid():N}";

        // ⚠ 2601/2627 можливі лише якщо HOLDLOCK не спрацював (інший план):
        // унікальний індекс відбив дубль — повтор знайде чужу Queued і зіллється.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunAsync(EnqueueSql, p =>
                {
                    p.Add("@newId", SqlDbType.NVarChar, 100).Value = newId;
                    p.Add("@code", SqlDbType.NVarChar, 64).Value = request.JobCode;
                    p.Add("@lane", SqlDbType.VarChar, JobLanes.MaxLength).Value = request.Lane;
                    p.Add("@payload", SqlDbType.NVarChar, -1).Value = request.PayloadJson;
                    p.Add("@target", SqlDbType.NVarChar, JobProgress.MaxTargetKeyLength).Value = Db(request.TargetKey);
                    p.Add("@user", SqlDbType.Int).Value = Db(request.CreatedByUserId);
                    p.Add("@correlation", SqlDbType.NVarChar, JobProgress.MaxCorrelationIdLength).Value =
                        Db(request.CorrelationId);
                    p.Add("@document", SqlDbType.BigInt).Value = Db(request.DocumentId);
                    p.Add("@delayMs", SqlDbType.Int).Value = checked((int)delay.TotalMilliseconds);
                    p.Add("@supersede", SqlDbType.Bit).Value = request.SupersedeRunning;
                    BindMerge(p, JobPayloadMerge.ArrayPathOf(request.JobCode));
                    AddShown(p);
                }, async r =>
                {
                    await r.ReadAsync(ct).ConfigureAwait(false);
                    return new JobEnqueueResult(
                        r.GetString(0),
                        r.GetInt32(1) == 1 ? JobEnqueueOutcome.CoalescedIntoQueued : JobEnqueueOutcome.Created);
                }, ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (IsDuplicateKey(ex) && attempt < 3)
            {
            }
        }
    }

    /// <inheritdoc />
    public async Task<ClaimedJob?> ClaimAsync(
        IReadOnlyCollection<string> lanes, string owner, TimeSpan lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(owner.Length, JobProgress.MaxInstanceIdLength, nameof(owner));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero, nameof(lease));

        if (lanes.Count == 0 || lanes.Any(l => !JobLanes.IsKnown(l)))
        {
            throw new ArgumentException("Лейни claim — лише з JobLanes.All, щонайменше один.", nameof(lanes));
        }

        await EnsureRcsiAsync(ct).ConfigureAwait(false);

        // Значення лейна в SQL — константа з JobLanes.All, а не рядок викликача.
        var ordered = JobLanes.All.Where(l => lanes.Contains(l, StringComparer.Ordinal)).ToArray();
        var token = Guid.NewGuid();

        void Bind(SqlParameterCollection p, string lane)
        {
            p.Add("@lane", SqlDbType.VarChar, JobLanes.MaxLength).Value = lane;
            p.Add("@token", SqlDbType.UniqueIdentifier).Value = token;
            p.Add("@owner", SqlDbType.NVarChar, JobProgress.MaxInstanceIdLength).Value = owner;
            p.Add("@leaseMs", SqlDbType.Int).Value = checked((int)lease.TotalMilliseconds);
            p.Add("@maxReclaims", SqlDbType.Int).Value = JobQueueLimits.MaxReclaims;
            AddShown(p);
        }

        async Task<ClaimedJob?> Read(SqlDataReader r, bool reclaimed)
            => await r.ReadAsync(ct).ConfigureAwait(false)
                ? new ClaimedJob(
                    new JobClaimToken(r.GetString(0), token), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetInt32(5), r.GetInt32(6), reclaimed, DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc),
                    r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetInt64(10))
                : null;

        try
        {
            // Прострочені Running першими — по всіх лейнах, далі Queued.
            foreach (var (sql, reclaimed) in new[] { (ReclaimSql, true), (ClaimQueuedSql, false) })
            {
                foreach (var lane in ordered)
                {
                    var job = await RunAsync(sql, p => Bind(p, lane), r => Read(r, reclaimed), ct)
                        .ConfigureAwait(false);

                    if (job is not null)
                    {
                        return job;
                    }
                }
            }

            return null;
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            // Правка А: гонку, яку NOT EXISTS не побачив, відбив UX_JobProgress_Target_Running —
            // це «нічого не взяв», а не помилка.
            return null;
        }
    }

    /// <inheritdoc />
    public Task<LeaseState> RenewAsync(JobClaimToken claim, TimeSpan lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero, nameof(lease));

        return RunAsync(
            $"""
            UPDATE itg.JobProgress
            SET LeaseUntil = DATEADD(millisecond, @leaseMs, SYSUTCDATETIME()), HeartbeatAt = @shown
            OUTPUT CASE WHEN inserted.CancelRequestedAt IS NULL THEN 0 ELSE 1 END
            {OwnedBy};
            """,
            p =>
            {
                BindClaim(p, claim);
                p.Add("@leaseMs", SqlDbType.Int).Value = checked((int)lease.TotalMilliseconds);
                AddShown(p);
            },
            async r => !await r.ReadAsync(ct).ConfigureAwait(false) ? LeaseState.Lost
                : r.GetInt32(0) == 1 ? LeaseState.CancelRequested : LeaseState.Held,
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ UPDATE «сам у себе» — не для змін, а заради X-локу на рядок задачі до
    /// коміту викликача: переклейм, що прийде в цю мить, чекає на нього, тож
    /// «видиме» і «оренда жива» комітяться разом або ніяк.
    /// </remarks>
    public Task<bool> FenceAsync(JobClaimToken claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("FenceAsync має сенс лише у відкритій транзакції EcrDbContext.");
        }

        return OwnedActionAsync("UPDATE itg.JobProgress SET ClaimToken = ClaimToken", claim, null, ct);
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(JobClaimToken claim, CancellationToken ct)
        => OwnedActionAsync(
            "UPDATE itg.JobProgress SET [State] = 'Succeeded', [Percent] = 100, LeaseUntil = NULL, UpdatedAt = @shown",
            claim, null, ct);

    /// <inheritdoc />
    public Task<bool> FailAsync(JobClaimToken claim, string reason, string? errorCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode?.Length ?? 0, JobProgress.MaxErrorCodeLength, nameof(errorCode));

        return OwnedActionAsync(
            """
            UPDATE itg.JobProgress
            SET [State] = 'Failed', Error = @error, ErrorCode = @errorCode, LeaseUntil = NULL, UpdatedAt = @shown
            """,
            claim,
            p =>
            {
                p.Add("@error", SqlDbType.NVarChar, IJobProgressStore.MaxErrorLength).Value =
                    JobProgressMessageCodec.Shorten(reason, IJobProgressStore.MaxErrorLength);
                p.Add("@errorCode", SqlDbType.VarChar, JobProgress.MaxErrorCodeLength).Value = Db(errorCode);
            },
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Queued позаду на ту саму ціль уже несе актуальний payload цілі — наш
    /// рядок закривається <c>Cancelled</c> з конвертом <c>jobs.absorbedBy</c>, а
    /// та, що позаду, стає доступною не пізніше, ніж став би наш ретрай.
    /// </remarks>
    public Task<bool> RequeueAsync(JobClaimToken claim, TimeSpan delay, CancellationToken ct)
        => RequeueCoreAsync(claim, delay, restoreAttempt: false, ct);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Той самий пакет, що <see cref="RequeueAsync"/> (поглинання й злиття масиву
    /// задачею позаду — так само), з однією різницею: <c>Attempt − 1</c>, тобто
    /// значення до захоплення, яке додало <c>+1</c>.
    /// </remarks>
    public Task<bool> DeferAsync(JobClaimToken claim, TimeSpan delay, CancellationToken ct)
        => RequeueCoreAsync(claim, delay, restoreAttempt: true, ct);

    private Task<bool> RequeueCoreAsync(JobClaimToken claim, TimeSpan delay, bool restoreAttempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero, nameof(delay));

        var absorbed = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            "jobs.absorbedBy", new Dictionary<string, string>(StringComparer.Ordinal) { ["jobId"] = AbsorbedMarker }));

        return RunAsync(
            $"""
            BEGIN TRANSACTION;
            DECLARE @target nvarchar(200), @behind nvarchar(100), @ok int = 0, @code nvarchar(64), @absorb int = 0;
            DECLARE @available datetime2(3) = DATEADD(millisecond, @delayMs, SYSUTCDATETIME());
            {MergeDeclarations}
            SELECT @target = TargetKey, @ok = 1, @code = JobCode, @addPayload = Payload
            FROM itg.JobProgress WITH (UPDLOCK) {OwnedBy};
            IF @ok = 1 AND @target IS NOT NULL
                SELECT @behind = JobId, @basePayload = Payload
                FROM itg.JobProgress WITH (UPDLOCK, HOLDLOCK, INDEX(UX_JobProgress_Target_Queued))
                WHERE TargetKey = @target AND TargetKey IS NOT NULL AND [State] = 'Queued';
            IF @ok = 1 AND @behind IS NOT NULL
            BEGIN
                SET @absorb = 1;
                -- O1: масив злиття повернутої — у задачу позаду; не зливається — не поглинаємо.
                IF @mergePath IS NOT NULL AND @code = @mergeCode
                BEGIN
            {MergeArraysSql}
                    IF @merged IS NOT NULL
                        UPDATE itg.JobProgress SET Payload = @merged WHERE JobId = @behind;
                    ELSE
                        SET @absorb = 0;
                END
            END
            IF @absorb = 1
            BEGIN
                UPDATE itg.JobProgress
                SET [State] = 'Cancelled', LeaseUntil = NULL, UpdatedAt = @shown,
                    [Message] = REPLACE(@absorbed, N'{AbsorbedMarker}', STRING_ESCAPE(@behind, 'json'))
                {OwnedBy};
                UPDATE itg.JobProgress
                SET AvailableAt = CASE WHEN AvailableAt > @available THEN @available ELSE AvailableAt END
                WHERE JobId = @behind;
            END
            ELSE IF @ok = 1
                UPDATE itg.JobProgress
                SET [State] = 'Queued', AvailableAt = @available, ClaimToken = NULL, LeaseUntil = NULL,
                    UpdatedAt = @shown, HeartbeatAt = @shown,
                    Attempt = CASE WHEN @restoreAttempt = 1 AND ISNULL(Attempt, 0) > 0 THEN Attempt - 1 ELSE Attempt END,
                    -- Позаду на ціль уже стоїть інша Queued: дві Queued на ціль не пускає UX_JobProgress_Target_Queued.
                    TargetKey = CASE WHEN @behind IS NULL THEN TargetKey ELSE NULL END
                {OwnedBy};
            COMMIT TRANSACTION;
            SELECT @ok;
            """,
            p =>
            {
                BindClaim(p, claim);
                BindMerge(p, JobPayloadMerge.ArrayPathOf(JobPayloadMerge.MergeableJobCode));
                p.Add("@mergeCode", SqlDbType.NVarChar, 64).Value = JobPayloadMerge.MergeableJobCode;
                p.Add("@restoreAttempt", SqlDbType.Bit).Value = restoreAttempt;
                p.Add("@delayMs", SqlDbType.Int).Value = checked((int)delay.TotalMilliseconds);
                p.Add("@absorbed", SqlDbType.NVarChar, JobProgressMessageCodec.MaxEncodedLength).Value = absorbed;
                AddShown(p);
            },
            async r => await r.ReadAsync(ct).ConfigureAwait(false) && r.GetInt32(0) == 1,
            ct);
    }

    /// <inheritdoc />
    public Task<bool> AcknowledgeCancelAsync(JobClaimToken claim, CancellationToken ct)
        => OwnedActionAsync(
            "UPDATE itg.JobProgress SET [State] = 'Cancelled', LeaseUntil = NULL, UpdatedAt = @shown",
            claim, null, ct);

    /// <inheritdoc />
    public Task<CancelOutcome> RequestCancelAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        // Один UPDATE: Queued → Cancelled одразу, Running → позначка; гонки
        // «Queued стала Running між двома запитами» немає.
        return RunAsync(
            """
            DECLARE @before TABLE ([State] nvarchar(32));
            UPDATE itg.JobProgress
            SET [State] = CASE WHEN [State] = 'Queued' THEN 'Cancelled' ELSE [State] END,
                CancelRequestedAt = CASE WHEN [State] = 'Running'
                    THEN ISNULL(CancelRequestedAt, SYSUTCDATETIME()) ELSE CancelRequestedAt END,
                UpdatedAt = @shown
            OUTPUT deleted.[State] INTO @before
            WHERE JobId = @id AND Lane IS NOT NULL AND [State] IN ('Queued', 'Running');
            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM @before WHERE [State] = 'Queued') THEN 1
                WHEN EXISTS (SELECT 1 FROM @before) THEN 2
                WHEN EXISTS (SELECT 1 FROM itg.JobProgress WHERE JobId = @id AND Lane IS NOT NULL) THEN 3
                ELSE 0 END;
            """,
            p =>
            {
                p.Add("@id", SqlDbType.NVarChar, 100).Value = jobId;
                AddShown(p);
            },
            async r =>
            {
                await r.ReadAsync(ct).ConfigureAwait(false);
                return r.GetInt32(0) switch
                {
                    1 => CancelOutcome.Cancelled,
                    2 => CancelOutcome.CancelRequested,
                    3 => CancelOutcome.AlreadyFinished,
                    _ => CancelOutcome.NotFound,
                };
            },
            ct);
    }

    /// <inheritdoc />
    public Task<bool> IsCancelRequestedAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return RunAsync(
            """
            SELECT CASE WHEN CancelRequestedAt IS NOT NULL OR [State] = 'Cancelled' THEN 1 ELSE 0 END
            FROM itg.JobProgress WHERE JobId = @id AND Lane IS NOT NULL;
            """,
            p => p.Add("@id", SqlDbType.NVarChar, 100).Value = jobId,
            async r => await r.ReadAsync(ct).ConfigureAwait(false) && r.GetInt32(0) == 1,
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ На ціль уже стоїть інша <c>Queued</c> — перезапуск зайвий: вона візьме
    /// актуальний стан цілі. <c>UX_JobProgress_Target_Queued</c> відбиває
    /// перехід (2601), і метод повертає <c>false</c>.
    /// </remarks>
    public async Task<bool> RestartAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        try
        {
            return await RunAsync(
                """
                UPDATE itg.JobProgress
                SET [State] = 'Queued', Attempt = 0, ReclaimCount = 0, AvailableAt = SYSUTCDATETIME(),
                    ClaimToken = NULL, LeaseUntil = NULL, CancelRequestedAt = NULL, [Percent] = 0,
                    [Message] = NULL, Error = NULL, ErrorCode = NULL, UpdatedAt = @shown, HeartbeatAt = @shown
                OUTPUT 1
                WHERE JobId = @id AND Lane IS NOT NULL AND [State] IN ('Failed', 'Cancelled');
                """,
                p =>
                {
                    p.Add("@id", SqlDbType.NVarChar, 100).Value = jobId;
                    AddShown(p);
                },
                r => r.ReadAsync(ct),
                ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Просту прострочену оренду сам переклеймить <see cref="ClaimAsync"/>;
    /// тут лише ті, кого claim не бере: отруйна (<c>ReclaimCount</c> ≥ межі) →
    /// <c>Failed</c> з конвертом <c>jobs.leaseLostTooOften</c>, і та, яку просили
    /// скасувати, → <c>Cancelled</c>.
    /// </remarks>
    public Task<int> ExpireAsync(int maxReclaims, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxReclaims);

        var poisoned = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            "jobs.leaseLostTooOften",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reclaims"] = maxReclaims.ToString(CultureInfo.InvariantCulture),
            }));

        return RunAsync(
            """
            DECLARE @n int;
            UPDATE itg.JobProgress WITH (ROWLOCK, READPAST)
            SET [State] = 'Failed', [Message] = @poisoned, LeaseUntil = NULL, UpdatedAt = @shown
            WHERE Lane IS NOT NULL AND [State] = 'Running' AND LeaseUntil < CAST(SYSUTCDATETIME() AS datetime2(3))
              AND CancelRequestedAt IS NULL AND ISNULL(ReclaimCount, 0) >= @max;
            SET @n = @@ROWCOUNT;
            UPDATE itg.JobProgress WITH (ROWLOCK, READPAST)
            SET [State] = 'Cancelled', LeaseUntil = NULL, UpdatedAt = @shown
            WHERE Lane IS NOT NULL AND [State] = 'Running' AND LeaseUntil < CAST(SYSUTCDATETIME() AS datetime2(3))
              AND CancelRequestedAt IS NOT NULL;
            SELECT @n + @@ROWCOUNT;
            """,
            p =>
            {
                p.Add("@max", SqlDbType.Int).Value = maxReclaims;
                p.Add("@poisoned", SqlDbType.NVarChar, JobProgressMessageCodec.MaxEncodedLength).Value = poisoned;
                AddShown(p);
            },
            async r =>
            {
                await r.ReadAsync(ct).ConfigureAwait(false);
                return r.GetInt32(0);
            },
            ct);
    }

    private Task<bool> OwnedActionAsync(
        string update, JobClaimToken claim, Action<SqlParameterCollection>? bind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return RunAsync(
            $"{update}\nOUTPUT 1\n{OwnedBy};",
            p =>
            {
                BindClaim(p, claim);
                AddShown(p);
                bind?.Invoke(p);
            },
            r => r.ReadAsync(ct),
            ct);
    }

    /// <summary>Виконує пакет на з'єднанні контексту в його поточній транзакції.</summary>
    private async Task<T> RunAsync<T>(
        string sql, Action<SqlParameterCollection> bind, Func<SqlDataReader, Task<T>> read, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            var transaction = (SqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();

            // ⚠ Окремим пакетом БЕЗ параметрів, а не префіксом запиту: запит з
            // параметрами йде через sp_executesql, і SET ANSI_NULLS усередині
            // нього не діє на вже скомпільований DML (перевірено: 1934 на UPDATE).
            // Пакет без параметрів ставить опції сесії до кінця з'єднання.
            await using (var options = connection.CreateCommand())
            {
                options.Transaction = transaction;
                options.CommandText = SessionOptions;
                await options.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            bind(command.Parameters);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = await read(reader).ConfigureAwait(false);

            // Дочитати решту: помилка пізнішого оператора пакета інакше загубилася б.
            while (await reader.NextResultAsync(ct).ConfigureAwait(false))
            {
            }

            return result;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Захоплення вимагає RCSI бази: перевіряється до першого claim, раз на
    /// процес для кожної бази (сервер + ім'я).
    /// </summary>
    /// <remarks>
    /// ⛔ <c>NOT EXISTS</c> на <c>Running</c> тієї ж цілі в <see cref="ClaimQueuedSql"/>
    /// іде БЕЗ <c>READPAST</c> (правка А) і розраховує, що рядок під чужим
    /// локом читається останньою закоміченою версією. Без RCSI той самий запит
    /// чекає на лок — хости стають у чергу один за одним, і черга, що «працює»,
    /// насправді серіалізована. Тому OFF — відмова з поясненням, а не мовчазна
    /// деградація; постановку (<see cref="EnqueueAsync"/>) це не блокує.
    ///
    /// ⚠ <see cref="Startup.SqlCapabilitiesProbe"/> читає те саме, але заповнюється
    /// лише стартом Api і лише для бази з конфігурації — черзі він недоступний
    /// (конструктор без <c>ISqlCapabilities</c>, воркер і тести без старту).
    /// Кешується лише ON: після <c>06-rcsi.sql</c> перезапуск процесу не потрібен.
    /// </remarks>
    private async Task EnsureRcsiAsync(CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var key = $"{connection.DataSource}|{connection.Database}";
        if (RcsiConfirmed.ContainsKey(key))
        {
            return;
        }

        var (database, on) = await RunAsync(
            "SELECT DB_NAME(), CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = DB_NAME();",
            _ => { },
            async r => await r.ReadAsync(ct).ConfigureAwait(false)
                ? (r.GetString(0), !r.IsDBNull(1) && r.GetInt32(1) == 1)
                : (connection.Database, false),
            ct).ConfigureAwait(false);

        if (!on)
        {
            throw new InvalidOperationException(
                $"Черга задач не захоплює задачі: у базі «{database}» вимкнено READ_COMMITTED_SNAPSHOT (RCSI). " +
                "Claim читає Running тієї ж цілі без READPAST і без RCSI блокується на чужих орендах — " +
                "хости виконували б задачі по черзі. Увімкніть RCSI скриптом " +
                "src/Ecr.Infrastructure/Persistence/Sql/06-rcsi.sql (у вікні обслуговування: обриває сеанси).");
        }

        RcsiConfirmed.TryAdd(key, 0);
    }

    private void AddShown(SqlParameterCollection p)
        => p.Add("@shown", SqlDbType.DateTime2).Value = clock.UtcNow;

    /// <summary>Параметри <see cref="MergeArraysSql"/>; <paramref name="path"/> <c>null</c> — злиття немає.</summary>
    private static void BindMerge(SqlParameterCollection p, string? path)
    {
        p.Add("@mergePath", SqlDbType.NVarChar, 100).Value = Db(path);
        p.Add("@maxPayload", SqlDbType.Int).Value = JobQueueLimits.MaxPayloadLength;
    }

    private static void BindClaim(SqlParameterCollection p, JobClaimToken claim)
    {
        p.Add("@id", SqlDbType.NVarChar, 100).Value = claim.JobId;
        p.Add("@token", SqlDbType.UniqueIdentifier).Value = claim.Token;
    }

    private static bool IsDuplicateKey(SqlException ex) => ex.Number is 2601 or 2627;

    private static object Db<T>(T? value) => value is null ? DBNull.Value : value;

    /// <summary>Коротке ім'я типу для читабельного JobId: лише літери й цифри, ≤ 60.</summary>
    private static string ShortCode(string jobCode)
    {
        var name = jobCode[(jobCode.LastIndexOf('.') + 1)..];
        var clean = new string(name.Where(char.IsAsciiLetterOrDigit).Take(60).ToArray());
        return clean.Length == 0 ? "job" : clean;
    }
}

/// <summary>
/// Задачі, чиє злиття на ціль ОБ'ЄДНУЄ масив payload, а не відкидає нову постановку
/// (O1, I2 ФВ-9.8) — спільне правило черги в базі (<see cref="DbJobQueue"/>) і Quartz
/// (<see cref="QuartzJobScheduler"/>).
/// </summary>
/// <remarks>
/// ⛔ Інкрементна задача формул несе НАСІННЯ каскаду — змінені комірки. Звичайне
/// злиття (payload визначається ціллю, нова постановка поглинається) загубило б
/// комірки другої правки, і формули від них не перерахувалися б. Повний прогін
/// документо-періоду на кожне злиття — теж ні: він пише всі обчислені комірки
/// (I2: 66 % ЦП SQL на такому записі), а на гарячому документі злиття —
/// постійний стан. Тому масив <c>cells</c> об'єднується; дублікати нешкідливі —
/// задача складає їх у множину (<c>DirtySet</c>).
/// </remarks>
internal static class JobPayloadMerge
{
    /// <summary>Код задачі з масивом злиття (поки що одна).</summary>
    public static readonly string MergeableJobCode =
        typeof(IFormulaRecalculationJob).FullName ?? nameof(IFormulaRecalculationJob);

    /// <summary>JSON-шлях масиву злиття для коду задачі; <c>null</c> — звичайне злиття.</summary>
    public static string? ArrayPathOf(string? jobCode)
        => string.Equals(jobCode, MergeableJobCode, StringComparison.Ordinal)
            ? FormulaRecalculationTarget.MergedArrayPath
            : null;

    /// <summary>
    /// Те саме злиття, що <c>DbJobQueue.MergeArraysSql</c>, у пам'яті (Quartz):
    /// масив <paramref name="path"/> (<c>$.властивість</c> кореня) доповнюється елементами
    /// з <paramref name="addJson"/>.
    /// </summary>
    /// <returns><c>null</c> — не зливається (немає масиву, чужа форма, понад межу payload).</returns>
    public static string? Merge(string baseJson, string addJson, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var property = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(baseJson) is not System.Text.Json.Nodes.JsonObject target
                || target[property] is not System.Text.Json.Nodes.JsonArray into
                || System.Text.Json.Nodes.JsonNode.Parse(addJson) is not System.Text.Json.Nodes.JsonObject source
                || source[property] is not System.Text.Json.Nodes.JsonArray from)
            {
                return null;
            }

            foreach (var item in from.ToList())
            {
                from.Remove(item);
                into.Add(item);
            }

            var merged = target.ToJsonString();

            return merged.Length > JobQueueLimits.MaxPayloadLength ? null : merged;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
