using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IWorkflowStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class WorkflowStore(EcrDbContext db) : IWorkflowStore
{
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
        return candidates
            .OrderByDescending(r => r.Specificity)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .FirstOrDefault();
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
    public async Task<bool> RoleExistsAsync(int roleId, CancellationToken ct)
        => await db.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ApprovalState> GetOrCreateAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        var existing = await db.ApprovalStates
            .FirstOrDefaultAsync(
                s => s.DocumentId == documentId
                     && s.SheetDefId == sheetDefId
                     && s.PeriodKey == periodKey.Value,
                ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        // Відсутній рядок означає Draft, а не помилку: стан з'являється в
        // момент першої дії, а не разом із документом.
        var created = new ApprovalState(documentId, sheetDefId, periodKey.Value);
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
        long documentId, PeriodKey periodKey, int limit, CancellationToken ct)
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
            orderby e.At descending, e.Id descending
            select new ApprovalEventRecord(
                sheet.Code, e.FromStatus, e.ToStatus, e.Action, e.ByUserId,
                user == null ? null : user.DisplayName, e.At, e.Reason, e.StepOrdinal);

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
            snapshot.SubmittedByUserId);

        db.SubmissionSnapshots.Add(entity);

        // Ідентифікатор потрібен викликачеві, а IDENTITY заповнюється лише при
        // збереженні. Це той рідкісний випадок, коли SaveChanges виправданий у
        // сховищі: зріз мусить існувати ДО зміни стану аркуша.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubmissionSnapshotRecord>> GetSnapshotsAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => await db.SubmissionSnapshots
            .AsNoTracking()
            .Where(s => s.DocumentId == documentId
                        && s.SheetDefId == sheetDefId
                        && s.PeriodKey == periodKey.Value)
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
                s.SubmittedByUserId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

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
}
