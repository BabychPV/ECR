using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IPeriodStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class PeriodStore(EcrDbContext db) : IPeriodStore
{
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
               $"Політику періодів {periodPolicyId} не знайдено. Виконайте seed перед створенням проєкту.");

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
        var query =
            from document in db.Documents.AsNoTracking()
            join period in db.Periods.AsNoTracking()
                on document.ProjectId equals period.ProjectId
            where document.Id == documentId && period.PeriodKeyValue == periodKey
            select (Ecr.Domain.Enums.PeriodState?)period.State;

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
