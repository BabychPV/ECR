using System.Data;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IWorkflowStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class WorkflowStore(EcrDbContext db) : IWorkflowStore
{
    /// <summary>Правило ефективного стану періоду — те саме, що в рішенні про запис (F-08).</summary>
    private static readonly Domain.Services.PeriodStateCalculator PeriodStates = new();

    /// <inheritdoc />
    public async Task<Domain.Enums.PeriodState> EffectivePeriodStateAsync(
        Period period, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(period);

        var project = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == period.ProjectId)
            .Select(p => new { p.Status, p.PeriodEnd, p.YearGraceOffsetDays, p.TimeZoneId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // ⚠ Лише для АКТИВНОГО проєкту — як у `AccessDecisionService` і в самій
        // задачі станів (`A7-25`): періоди чернетки за датами не просуваються.
        if (project is null || project.Status != Domain.Enums.ProjectStatus.Active)
        {
            return period.State;
        }

        return PeriodStates.Effective(
            period,
            utcNow,
            Domain.Services.YearGraceWindow.For(
                project.PeriodEnd,
                project.YearGraceOffsetDays,
                SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo()));
    }

    /// <inheritdoc />
    public Task<bool> HasDocumentPeriodReopenAsync(
        long documentId, PeriodKey periodKey, DateTime utcNow, CancellationToken ct)
    {
        var key = periodKey.Value;
        return db.DocumentPeriodReopens
            .AsNoTracking()
            .AnyAsync(x => x.DocumentId == documentId && x.PeriodKeyValue == key && x.ClosedAt == null && x.OpenUntil > utcNow, ct);
    }

    /// <summary>Порожній набір версій методологій для зрізу без розрахунків.</summary>
    /// <remarks>
    /// Колонка <c>NOT NULL</c> навмисно: «версій не було» і «версії не
    /// записали» — різні речі, і порожній об'єкт відрізняє першу від другої.
    /// </remarks>
    private const string NoMethodologies = "{}";

    /// <inheritdoc />
    public async Task<ApprovalRoute?> FindRouteAsync(
        int projectId, int templateVersionId, CancellationToken ct)
    {
        // ⚠ ОДИН запит на всі рівні, а не чотири по черзі: подання і
        // затвердження — часті операції, і чотири походи в базу заради
        // таблиці на кілька рядків були б платою ні за що.
        var candidates = await db.ApprovalRoutes
            .AsNoTracking()
            .Include(r => r.Steps)
            .Where(r => r.IsActive
                        && (r.ProjectId == null || r.ProjectId == projectId)
                        && (r.TemplateVersionId == null || r.TemplateVersionId == templateVersionId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Найконкретніший виграє; при рівній конкретності — стабільний
        // порядок за кодом, щоб вибір не залежав від порядку рядків у базі.
        // ⛔ Правило — одне на затвердження й на чергу «До погодження» (ФВ-12.5): `ApprovalRoute.Resolve`.
        return ApprovalRoute.Resolve(candidates, projectId, templateVersionId);
    }

    /// <inheritdoc />
    public async Task<ApprovalRoute?> FindProjectRouteAsync(int projectId, CancellationToken ct)
        => await db.ApprovalRoutes
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.ProjectId == projectId, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddRouteAsync(ApprovalRoute route, CancellationToken ct)
    {
        await db.ApprovalRoutes.AddAsync(route, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Кроки прибираються ЯВНО. `FK_AS_Route` оголошений `Restrict` ще від
    /// Етапу 3, і покластися на каскад означало б, що прибирання маршруту
    /// падає на зовнішньому ключі — а виявилося б це вже тоді, коли хтось
    /// спробував повернути одноетапне затвердження.
    /// </remarks>
    public async Task RemoveRouteAsync(ApprovalRoute route, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);

        var steps = await db.ApprovalSteps
            .Where(s => s.ApprovalRouteId == route.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.ApprovalSteps.RemoveRange(steps);
        db.ApprovalRoutes.Remove(route);
    }

    /// <inheritdoc />
    public async Task RemoveStepsAsync(ApprovalRoute route, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);

        var steps = await db.ApprovalSteps
            .Where(s => s.ApprovalRouteId == route.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ Спершу ПОЗНАЧИТИ видаленими, потім прибрати з колекції. У
        // зворотному порядку EF бачить відвʼязану дитину з обовʼязковим
        // ключем і відмовляється зберігати зміни взагалі.
        db.ApprovalSteps.RemoveRange(steps);
        route.ClearSteps();
    }

    /// <inheritdoc />
    public async Task<int?> FirstMissingRoleAsync(IReadOnlyCollection<int> roleIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var distinct = roleIds.Distinct().ToList();
        var existing = (await db.Roles.AsNoTracking()
                .Where(r => distinct.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet();

        foreach (var id in roleIds)
        {
            if (!existing.Contains(id))
            {
                return id;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public Task<ApprovalState> GetOrCreateAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => GetOrCreateCoreAsync(documentId, sheetDefId, periodKey, null, ct);

    /// <inheritdoc />
    public Task<ApprovalState> GetOrCreateCopyAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, long sheetCopyId, CancellationToken ct)
        => GetOrCreateCoreAsync(documentId, sheetDefId, periodKey, sheetCopyId, ct);

    /// <summary>Стан оригіналу (<paramref name="sheetCopyId"/> = <c>null</c>) або копії аркуша за період.</summary>
    /// <remarks>
    /// ⛔ RS: фільтр за копією обов'язковий і для оригіналу — без нього <c>FirstOrDefault</c> міг би повернути стан
    /// КОПІЇ, і подання оригіналу змінило б не той рядок.
    /// </remarks>
    private async Task<ApprovalState> GetOrCreateCoreAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId, CancellationToken ct)
    {
        var existing = await db.ApprovalStates
            .FirstOrDefaultAsync(
                s => s.DocumentId == documentId
                     && s.SheetDefId == sheetDefId
                     && s.PeriodKey == periodKey.Value
                     && s.SheetCopyId == sheetCopyId,
                ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        // Відсутній рядок означає Draft, а не помилку: стан з'являється в
        // момент першої дії, а не разом із документом.
        var created = new ApprovalState(documentId, sheetDefId, periodKey.Value, sheetCopyId);
        db.ApprovalStates.Add(created);
        return created;
    }

    /// <inheritdoc />
    public async Task AddEventAsync(ApprovalEvent approvalEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(approvalEvent);

        await db.ApprovalEvents.AddAsync(approvalEvent, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApprovalEventRecord>> GetHistoryAsync(
        long documentId, PeriodKey periodKey, int limit, CancellationToken ct, long? sheetCopyId = null)
    {
        // ⚠ Користувач приєднується ЛІВИМ з'єднанням: `ByUserId = null` — це
        // системний перехід, і внутрішнє з'єднання мовчки викинуло б його з журналу.
        // `Id` — другий ключ порядку: кілька дій можуть мати той самий `At`.
        var query =
            from e in db.ApprovalEvents.AsNoTracking()
            join sheet in db.SheetDefs.AsNoTracking() on e.SheetDefId equals sheet.Id
            join u in db.Users.AsNoTracking() on e.ByUserId equals (int?)u.Id into users
            from user in users.DefaultIfEmpty()
            where e.DocumentId == documentId && e.PeriodKey == periodKey.Value
                  && e.SheetCopyId == sheetCopyId
            orderby e.At descending, e.Id descending
            select new ApprovalEventRecord(
                sheet.Code, e.FromStatus, e.ToStatus, e.Action, e.ByUserId,
                user == null ? null : user.DisplayName, e.At, e.Reason, e.StepOrdinal,
                // RS: подання тієї самої КОПІЇ (null — оригінал), що й погодження.
                // ⛔ D-440: «погоджено автором подання» — з ЖУРНАЛУ, а не з `ApprovalState.SubmittedByUserId`:
                // рядок стану тримає лише останнє подання, і після повторного подання іншою людиною стара
                // подія погодження змінила б позначку заднім числом. Подання — найближче ПЕРЕД цією подією
                // (`Id` — порядок вставки) того самого аркуша й періоду; той самий запит, без N+1.
                (e.Action == ApprovalAction.Approve || e.Action == ApprovalAction.ApproveStep)
                && e.ByUserId != null
                && db.ApprovalEvents
                    .Where(s => s.DocumentId == e.DocumentId && s.SheetDefId == e.SheetDefId
                                && s.SheetCopyId == e.SheetCopyId
                                && s.PeriodKey == e.PeriodKey && s.Action == ApprovalAction.Submit && s.Id < e.Id)
                    .OrderByDescending(s => s.Id)
                    .Select(s => s.ByUserId)
                    .FirstOrDefault() == e.ByUserId,
                e.Id,
                e.SheetCopyId);

        return await query.Take(limit).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApprovalState>> GetSheetsAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
        => await db.ApprovalStates
            .Where(s => s.DocumentId == documentId && s.PeriodKey == periodKey.Value)
            .OrderBy(s => s.SheetDefId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SubmissionCalendar?> GetSubmissionCalendarAsync(
        long documentId, int sheetDefId, DateTime utcNow, CancellationToken ct, long? sheetCopyId = null)
    {
        var project = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new
                {
                    p.Id,
                    p.Status,
                    p.IsArchiving,
                    p.PeriodKind,
                    p.PeriodEnd,
                    p.YearGraceOffsetDays,
                    p.TimeZoneId,
                })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var periods = await db.Periods
            .AsNoTracking()
            .Where(p => p.ProjectId == project.Id)
            .OrderBy(p => p.PeriodKeyValue)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // RS: календар подання — оригіналу аркуша або заданої копії (`sheetCopyId`).
        var statuses = await db.ApprovalStates
            .AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.SheetDefId == sheetDefId && s.SheetCopyId == sheetCopyId)
            .Select(s => new { s.PeriodKey, s.Status })
            .ToDictionaryAsync(s => s.PeriodKey, s => s.Status, ct)
            .ConfigureAwait(false);

        // ⛔ C-6: місяць, відкритий лише для ЦЬОГО документа (08.11 (i)), — Grace і в календарі подання, як у рішенні
        // про подання (`AccessDecisionService`, `DocumentPeriodReopenRule`); інакше D-441 його не пропонує.
        var reopened = (await db.DocumentPeriodReopens
                .AsNoTracking()
                .Where(x => x.DocumentId == documentId && x.ClosedAt == null && x.OpenUntil > utcNow)
                .Select(x => x.PeriodKeyValue)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();

        // ⚠ Те саме правило, що й `EffectivePeriodStateAsync` вище та `AccessDecisionService.BuildContextAsync`:
        // за датами — лише для АКТИВНОГО проєкту (`A7-25`), інакше збережений стан.
        Domain.Services.YearGraceWindow? window = project.Status == Domain.Enums.ProjectStatus.Active
            ? Domain.Services.YearGraceWindow.For(
                project.PeriodEnd,
                project.YearGraceOffsetDays,
                SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo())
            : null;

        return new SubmissionCalendar(
            project.Status,
            project.IsArchiving,
            project.PeriodKind,
            [
                .. periods.Select(p => new SubmissionCalendarPeriod(
                    p.PeriodKeyValue,
                    Domain.Services.DocumentPeriodReopenRule.Apply(
                        window is null ? p.State : PeriodStates.Effective(p, utcNow, window),
                        reopened.Contains(p.PeriodKeyValue)),
                    statuses.TryGetValue(p.PeriodKeyValue, out var status)
                        ? status
                        : Domain.Enums.DocumentStatus.Draft)),
            ]);
    }

    /// <inheritdoc />
    public async Task<Period> LockPeriodAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        // ⚠ UPDLOCK береться ДО будь-яких змін і тримається до кінця
        // транзакції. Без нього Reopen і PeriodStateJob перегоняють одне
        // одного, і повернення в роботу застосувалося б до вже закритого
        // періоду (ФВ-1.10a). ROWLOCK — щоб блокування не розповзалося на
        // сторінку і не зупиняло сусідні проєкти.
        var period = await db.Periods
            .FromSql($"""
                SELECT p.* FROM doc.Period AS p WITH (UPDLOCK, ROWLOCK)
                JOIN doc.Document AS d ON d.ProjectId = p.ProjectId
                WHERE d.Id = {documentId} AND p.PeriodKey = {periodKey.Value}
                """)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return period ?? throw new NotFoundException(
            "ECR-PRD-0422",
            $"Період {periodKey.Value} не належить проєкту документа {documentId}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRD-0422.periodNotInProjectOfDocument",
                ["periodKey"] = periodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <inheritdoc />
    public async Task<long> SaveSnapshotAsync(SubmissionSnapshotRecord snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var entity = new SubmissionSnapshot(
            snapshot.DocumentId,
            snapshot.SheetDefId,
            snapshot.PeriodKey,
            snapshot.TemplateVersionId,
            snapshot.MethodologyVersionsJson ?? NoMethodologies,
            snapshot.NumericMode,
            snapshot.CalendarMode,
            snapshot.PayloadJson,
            Convert.FromHexString(snapshot.ContentHash),
            snapshot.SubmittedAt,
            snapshot.SubmittedByUserId,
            snapshot.SheetCopyId);

        db.SubmissionSnapshots.Add(entity);

        // Ідентифікатор потрібен викликачеві, а IDENTITY заповнюється лише при
        // збереженні. Це той рідкісний випадок, коли SaveChanges виправданий у
        // сховищі: зріз мусить існувати ДО зміни стану аркуша.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity.Id;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubmissionSnapshotRecord>> GetSnapshotsAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => GetSnapshotsCoreAsync(documentId, sheetDefId, periodKey, null, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<SubmissionSnapshotRecord>> GetCopySnapshotsAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, long sheetCopyId, CancellationToken ct)
        => GetSnapshotsCoreAsync(documentId, sheetDefId, periodKey, sheetCopyId, ct);

    /// <summary>Зрізи оригіналу або копії аркуша (RS: зрізи копії окремі).</summary>
    private async Task<IReadOnlyList<SubmissionSnapshotRecord>> GetSnapshotsCoreAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId, CancellationToken ct)
        => await db.SubmissionSnapshots
            .AsNoTracking()
            .Where(s => s.DocumentId == documentId
                        && s.SheetDefId == sheetDefId
                        && s.PeriodKey == periodKey.Value
                        && s.SheetCopyId == sheetCopyId)
            .OrderByDescending(s => s.SubmittedAt)
            .Select(s => new SubmissionSnapshotRecord(
                s.DocumentId,
                s.SheetDefId,
                s.PeriodKey,
                s.TemplateVersionId,
                s.MethodologyVersionsJson,
                s.NumericMode,
                s.CalendarMode,
                s.PayloadJson,
                Convert.ToHexStringLower(s.ContentHash),
                s.SubmittedAt,
                s.SubmittedByUserId,
                s.SheetCopyId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<(long Id, string ContentHash)?> GetLatestSnapshotKeyAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => GetLatestSnapshotKeyCoreAsync(documentId, sheetDefId, periodKey, null, ct);

    /// <inheritdoc />
    public Task<(long Id, string ContentHash)?> GetLatestCopySnapshotKeyAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, long sheetCopyId, CancellationToken ct)
        => GetLatestSnapshotKeyCoreAsync(documentId, sheetDefId, periodKey, sheetCopyId, ct);

    /// <summary>Ключ найновішого зрізу оригіналу або копії аркуша.</summary>
    private async Task<(long Id, string ContentHash)?> GetLatestSnapshotKeyCoreAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId, CancellationToken ct)
    {
        // Без PayloadJson у проєкції: у вибірку йдуть лише Id і хеш (32 байти).
        var row = await db.SubmissionSnapshots
            .AsNoTracking()
            .Where(s => s.DocumentId == documentId
                        && s.SheetDefId == sheetDefId
                        && s.PeriodKey == periodKey.Value
                        && s.SheetCopyId == sheetCopyId)
            .OrderByDescending(s => s.SubmittedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.ContentHash })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? null : (row.Id, Convert.ToHexStringLower(row.ContentHash));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ PERF (AN-135): НЕ через EF <c>FirstOrDefaultAsync</c>. Асинхронне читання ~10 МБ <c>nvarchar(max)</c>
    /// у звичайному (не послідовному) режимі SqlClient коштувало 11–12 с проти ~0,15 с синхронного: кожен
    /// наступний мережевий пакет перечитує вже накопичене значення. Тут — сирий запит у тій самій
    /// транзакції з <see cref="CommandBehavior.SequentialAccess"/> і потоковим <c>GetTextReader</c>: зовнішній
    /// API лишається асинхронним, а значення читається частинами без квадратичного буфера.
    /// </remarks>
    public async Task<string?> GetSnapshotPayloadAsync(long snapshotId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "SELECT TOP (1) PayloadJson FROM calc.SubmissionSnapshot WHERE Id = @id;";
            var id = command.CreateParameter();
            id.ParameterName = "@id";
            id.DbType = DbType.Int64;
            id.Value = snapshotId;
            command.Parameters.Add(id);

            await using var reader = await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, ct)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            using var text = reader.GetTextReader(0);
            return await text.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<bool> HasSubmittedSheetsAsync(int projectId, PeriodKey periodKey, CancellationToken ct)
    {
        // AnyAsync, а не Count: провайдер перекладає його в EXISTS і зупиняється
        // на першому рядку. Різниця помітна саме тут — таблиця станів росте
        // разом із документами.
        var query =
            from state in db.ApprovalStates.AsNoTracking()
            join document in db.Documents.AsNoTracking()
                on state.DocumentId equals document.Id
            where document.ProjectId == projectId
                  && state.PeriodKey == periodKey.Value

                  // ⚠ Дужки обов'язкові: `&&` зв'язує сильніше за `||`, і без
                  // них умова читалася б як «(проєкт і період і Submitted) АБО
                  // Approved» — тобто будь-який затверджений аркуш будь-якого
                  // проєкту робив би відповідь істинною.
                  && (state.Status == Domain.Enums.DocumentStatus.Submitted
                      || state.Status == Domain.Enums.DocumentStatus.Approved)
            select state.DocumentId;

        return query.AnyAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Application.Calculations.SkippedSheet>> ListApprovedSheetsAsync(
        int? projectId, long? documentId, int? periodKey, int take, CancellationToken ct)
    {
        // RS: перерахунок пропускає аркуш × КОПІЮ окремо (`SkippedScopesAsync`), тож і перелік — кожен затверджений
        // стан, з копією (✎ борг 09.11: раніше `OriginalOnly()` мовчки ховав затверджені копії з переліку).
        var rows = await (
                from state in db.ApprovalStates.AsNoTracking()
                join document in db.Documents.AsNoTracking() on state.DocumentId equals document.Id
                join sheet in db.SheetDefs.AsNoTracking() on state.SheetDefId equals sheet.Id
                from copy in db.DocumentSheets.AsNoTracking().Where(c => c.Id == state.SheetCopyId).DefaultIfEmpty()
                where (projectId == null || document.ProjectId == projectId)
                      && (documentId == null || document.Id == documentId)
                      && (periodKey == null || state.PeriodKey == periodKey)
                      && state.Status == Domain.Enums.DocumentStatus.Approved
                orderby document.Id, state.PeriodKey, sheet.Ordinal, sheet.Id,
                    copy == null ? Domain.Entities.Documents.DocumentSheet.OriginalOrdinal : copy.CopyOrdinal
                select new
                {
                    document.Id,
                    document.BusinessKey,
                    document.NameL10n,
                    SheetDefId = sheet.Id,
                    sheet.Code,
                    SheetName = sheet.NameL10n,
                    state.PeriodKey,
                    state.Status,
                    state.SheetCopyId,
                    CopyOrdinal = copy == null ? Domain.Entities.Documents.DocumentSheet.OriginalOrdinal : copy.CopyOrdinal,
                })
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return [];
        }

        // Назва групи й «чи є копії» — одним запитом на документи переліку (≤ стелі переліку).
        var documentIds = rows.Select(r => r.Id).Distinct().ToList();
        var groups = (await db.DocumentSheets.AsNoTracking()
                .Where(s => documentIds.Contains(s.DocumentId))
                .Select(s => new { s.DocumentId, s.SheetDefId, s.CopyOfId, s.NameOverrideL10n })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(s => (s.DocumentId, s.SheetDefId))
            .ToDictionary(
                g => g.Key,
                g => (Override: g.FirstOrDefault(s => s.CopyOfId == null)?.NameOverrideL10n, HasCopies: g.Any(s => s.CopyOfId != null)));

        return [.. rows.Select(r =>
        {
            var group = groups.TryGetValue((r.Id, r.SheetDefId), out var found) ? found : (Override: null, HasCopies: false);
            var display = Domain.ValueObjects.SheetCopyNaming.Display(
                Domain.ValueObjects.SheetCopyNaming.Effective(r.SheetName, group.Override),
                r.CopyOrdinal,
                group.HasCopies || r.SheetCopyId is not null);

            return new Application.Calculations.SkippedSheet(
                r.Id, r.BusinessKey, r.NameL10n, r.SheetDefId, r.Code, r.SheetName, r.PeriodKey, r.Status.ToString(),
                r.SheetCopyId, display);
        })];
    }

    /// <inheritdoc />
    public async Task<bool> AllSheetsApprovedAsync(int projectId, int periodKey, CancellationToken ct)
    {
        var documents = db.Documents.AsNoTracking().Where(d => d.ProjectId == projectId);

        if (!await documents.AnyAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        // Є що рахувати: документ без складу або з рядком складу, що не затверджений. ✎ F13 p218 #788: рядок складу —
        // оригінал АБО копія (RS: стан копії свій, SheetCopyId = Id рядка); копія-чернетка затвердженого оригіналу — є що рахувати.
        var recalculable = await documents
            .AnyAsync(
                d => !db.DocumentSheets.Any(s => s.DocumentId == d.Id && s.IsIncluded && s.CopyOfId == null)
                     || db.DocumentSheets.Any(s => s.DocumentId == d.Id && s.IsIncluded
                         && !db.ApprovalStates.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId
                             && ((s.CopyOfId == null && a.SheetCopyId == null) || a.SheetCopyId == s.Id)
                             && a.PeriodKey == periodKey
                             && a.Status == Domain.Enums.DocumentStatus.Approved)),
                ct)
            .ConfigureAwait(false);

        return !recalculable;
    }
}
