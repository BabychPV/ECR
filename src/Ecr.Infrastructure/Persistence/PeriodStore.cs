using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IPeriodStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <param name="db">Контекст бази.</param>
/// <param name="clock">
/// Годинник для ЕФЕКТИВНОГО стану періоду (<see cref="FindPeriodStateAsync"/>, F-08, X6-01).
/// <c>null</c> — лише пряме конструювання поза DI (тести, читання меж у
/// <c>IntegrationCellPatcher</c>): тоді стан — збережений, як до X6-01.
/// </param>
public sealed class PeriodStore(EcrDbContext db, Ecr.Domain.Abstractions.IClock? clock = null) : IPeriodStore
{
    /// <summary>Правило ефективного стану — те саме, що в рішенні про запис (F-08).</summary>
    private static readonly Ecr.Domain.Services.PeriodStateCalculator PeriodStates = new();

    /// <inheritdoc />
    public async Task<Project?> FindProjectAsync(int projectId, CancellationToken ct)
    {
        var project = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            .ConfigureAwait(false);

        if (project is null)
        {
            return null;
        }

        // Періоди завантажуються явно: календар звіряє наявні ключі, і
        // порожня колекція означала б «періодів немає» — тобто побудову
        // дублікатів на кожному виклику.
        await db.Entry(project).Collection(p => p.Periods).LoadAsync(ct).ConfigureAwait(false);
        return project;
    }

    /// <inheritdoc />
    public async Task<PeriodPolicy> GetPolicyAsync(int periodPolicyId, CancellationToken ct)
        => await db.PeriodPolicies
            .FirstOrDefaultAsync(p => p.Id == periodPolicyId, ct)
            .ConfigureAwait(false)
           ?? throw new NotFoundException(
               "ECR-PRD-0422",
               $"Політику періодів {periodPolicyId} не знайдено. Виконайте seed перед створенням проєкту.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-PRD-0422.policyNotFound",
                   ["periodPolicyId"] = periodPolicyId.ToString(System.Globalization.CultureInfo.InvariantCulture),
               });

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListProjectIdsUsingPolicyAsync(int periodPolicyId, CancellationToken ct)
        => await db.Projects
            .AsNoTracking()
            .Where(p => p.PeriodPolicyId == periodPolicyId)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Стеля переліку політик.</summary>
    /// <remarks>
    /// ⚠ Межа є навіть там, де рядків завідомо одиниці. «Їх завжди мало» —
    /// це те саме припущення, з якого починається кожен запит без межі:
    /// таблиця конфігурації одного разу стає таблицею даних, і помічають це
    /// на бойовому обсязі. Двісті політик періодів — це не масштаб, а
    /// зламане налаштування майданчика, і обрізаний перелік у полі вибору
    /// нічого не псує: будь-яка з них лишається придатною до вибору.
    /// </remarks>
    private const int MaxPolicies = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PeriodPolicy>> ListPoliciesAsync(CancellationToken ct)
        => await db.PeriodPolicies
            .AsNoTracking()
            .OrderBy(p => p.Code)
            .Take(MaxPolicies)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void AddPolicy(PeriodPolicy policy) => db.PeriodPolicies.Add(policy);

    /// <inheritdoc />
    public Task<Period?> LockAsync(int periodId, CancellationToken ct)
        // ⚠ UPDLOCK тримається до кінця транзакції: адміністративне відкриття і
        // PeriodStateJob беруть той самий рядок і мусять серіалізуватися
        // (ФВ-1.10a). ROWLOCK — щоб блокування не розповзалося на сторінку і не
        // зупиняло сусідні проєкти.
        => db.Periods
            .FromSql($"""
                SELECT * FROM doc.Period WITH (UPDLOCK, ROWLOCK) WHERE Id = {periodId}
                """)
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public void AddRange(IEnumerable<Period> periods) => db.Periods.AddRange(periods);

    /// <inheritdoc />
    public Task AddProjectAsync(Project project, CancellationToken ct)
    {
        db.Projects.Add(project);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PeriodStateRef>> GetPeriodStatesAsync(
        int projectId, int? periodKey, CancellationToken ct)
    {
        var query = db.Periods
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId);

        if (periodKey is { } single)
        {
            query = query.Where(p => p.PeriodKeyValue == single);
        }

        // Take за межею календаря: 12 місяців × запас. Без неї помилка в даних
        // виглядала б як повільність, а не як помилка (правило 6 архітектурних
        // тестів — ToListAsync без Take).
        return await query
            .OrderBy(p => p.PeriodKeyValue)
            .Take(MaxPeriods)
            .Select(p => new PeriodStateRef(p.PeriodKeyValue, p.State))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Стеля вибірки періодів: рік має щонайбільше 12 (D-108).</summary>
    private const int MaxPeriods = 64;

    /// <inheritdoc />
    public async Task<PeriodBounds?> FindPeriodBoundsAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        var query =
            from document in db.Documents.AsNoTracking()
            join period in db.Periods.AsNoTracking()
                on document.ProjectId equals period.ProjectId
            where document.Id == documentId && period.PeriodKeyValue == periodKey
            select new PeriodBounds(period.PeriodStart, period.PeriodEnd);

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Ecr.Domain.Enums.PeriodState?> FindPeriodStateAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        // Той самий ланцюг документ → проєкт → період, що й у меж: `PeriodKey`
        // не унікальний глобально, і без проєкту питання «який стан у 202601»
        // відповіді не має.
        //
        // ⛔ X6-01: стан — ЕФЕКТИВНИЙ на `clock.UtcNow`, тим самим правилом, що й
        // рішення про запис (`AccessDecisionService`, F-08). Доти тут був
        // збережений `period.State`, який просуває лише годинна задача станів
        // (о :05): від межі `Grace` до її прогону доступ уже пускав правку як
        // пізню, а `IsLateEdit` (D-70) писав у журнал `0` — «вчасно» саме в годину
        // дедлайну. Один запит, як і доти (храповик кількості запитів PATCH):
        // проєкт — з того самого рядка, період — підзапитом.
        var row = await (
                from document in db.Documents.AsNoTracking()
                where document.Id == documentId
                join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
                select new
                {
                    project.Status,
                    project.PeriodEnd,
                    project.YearGraceOffsetDays,
                    project.TimeZoneId,
                    Period = db.Periods
                        .AsNoTracking()
                        .Where(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
                        .FirstOrDefault(),
                })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row?.Period is not { } period)
        {
            return null;
        }

        // ⚠ Лише для АКТИВНОГО проєкту — як у `AccessDecisionService`, `WorkflowStore`
        // і в самій задачі станів (`A7-25`): періоди чернетки за датами не просуваються.
        if (clock is null || row.Status != Ecr.Domain.Enums.ProjectStatus.Active)
        {
            return period.State;
        }

        return PeriodStates.Effective(
            period,
            clock.UtcNow,
            Ecr.Domain.Services.YearGraceWindow.For(
                row.PeriodEnd,
                row.YearGraceOffsetDays,
                Ecr.Domain.ValueObjects.SiteTimeZone.Create(row.TimeZoneId).ToTimeZoneInfo()));
    }

    /// <inheritdoc />
    public async Task<bool> HasReopenedSheetAsync(
        long documentId, IReadOnlyCollection<int> sheetDefIds, int periodKey, CancellationToken ct)
    {
        // ⚠ `Reject` теж ставить ApprovedAt, але статус тоді Rejected: доопрацювання
        // відхиленого після Reopen лишається пізньою правкою. Знімає позначку
        // лише Approved, датований не раніше за Reopen.
        return await db.ApprovalStates.AsNoTracking()
            .AnyAsync(
                a => a.DocumentId == documentId
                     && a.PeriodKey == periodKey
                     && sheetDefIds.Contains(a.SheetDefId)
                     && a.ReopenedAt != null
                     && !(a.Status == Ecr.Domain.Enums.DocumentStatus.Approved && a.ApprovedAt >= a.ReopenedAt),
                ct)
            .ConfigureAwait(false);
    }
}
