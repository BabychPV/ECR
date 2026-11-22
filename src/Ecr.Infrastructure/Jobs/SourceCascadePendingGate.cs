// src/Ecr.Infrastructure/Jobs/SourceCascadePendingGate.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// F5 (рішення людини «Заборонити, поки йде перерахунок»): реалізація <see cref="ISourceCascadePendingGate"/> над чергою
/// в базі (<c>itg.JobProgress</c>).
/// </summary>
/// <remarks>
/// <para>
/// Один запит: живі (<c>Queued</c> — і відкладені, і запарковані; <c>Running</c>) рядки документа за фільтрованим
/// <c>IX_JobProgress_Document_Active</c> з ціллю рівно каскаду цього року (<c>doc{id}-y{рік}-dependents</c>,
/// <c>D-566</c>) чи цього періоду (<c>doc{id}-p{період}-dependents</c> — черга без об'єднання, довідник RT-25, повернення
/// покритої задачі). Документ без живої задачі каскаду — порожній результат пошуком за індексом. «З затвердженими»
/// (<c>-dependents-approved</c>) не блокує: вона й так переписує затверджені аркуші.
/// </para>
/// <para>
/// Чи період ще в плані задачі року — <see cref="Pends"/>.
/// </para>
/// <para>
/// ⚠ Черга Quartz у пам'яті рядків черги з payload не має — там ворота мовчать (Production — черга в базі, <c>D-563</c>).
/// ⚠ Вікно ДО постановки каскаду (власний перерахунок джерела <c>-auto</c> і задача впливу зв'язки) ворота не бачать: у
/// черзі ще немає задачі споживача.
/// </para>
/// </remarks>
/// <param name="db">Контекст (запит іде в його підключенні й транзакції обробника).</param>
public sealed class SourceCascadePendingGate(EcrDbContext db) : ISourceCascadePendingGate
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<bool> IsPendingAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var yearTarget = CalculationTrigger.SourceCascadeTargetOf(documentId, periodKey / 100);
        var periodTarget = CalculationTrigger.DependentsTargetOf(documentId, new PeriodKey(periodKey));

        var rows = await db.Database
            .SqlQueryRaw<CascadeRow>(
                """
                SELECT q.TargetKey AS TargetKey, q.[State] AS State, q.Payload AS Payload
                  FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Document_Active))
                 WHERE q.DocumentId = @doc AND q.DocumentId IS NOT NULL AND q.[State] IN ('Queued', 'Running')
                   AND q.TargetKey IN (@yearTarget, @periodTarget)
                """,
                new SqlParameter("@doc", documentId),
                new SqlParameter("@yearTarget", yearTarget),
                new SqlParameter("@periodTarget", periodTarget))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Exists(r => r.TargetKey == periodTarget || Pends(r.State == "Running", r.Payload, periodKey));
    }

    /// <summary>
    /// Чи задача каскаду року (payload <see cref="RecalculationRequest"/>) ще має порахувати період <paramref name="periodKey"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Перший крок ще не завершився (<c>chainAfter</c> = <c>null</c>, план не записано): період у
    /// <c>cascadePeriods</c>/<c>cascadeAppend</c> — так.</item>
    /// <item>Після першого кроку — період у решті плану <c>chainPlan</c> (з кроком, що виконується) чи в <c>cascadeAppend</c>.
    /// Крок, що завершився, знімає свій період із <c>chainPlan</c> продовження — і відмова зникає. (<c>cascadePeriods</c>
    /// продовження несе далі незмінними — після першого кроку вони не ознака.)</item>
    /// <item>План ще не ухвалено чи до ухваленого злилися нові періоди (<c>cascadeAppend</c>): план допише ПІЗНІШІ місяці,
    /// що читають обчислені числа (<c>CrossReadsAsync</c>, <c>D-566</c> п. 2), — тому кожен період, пізніший за найменший
    /// із ще не спланованих (<c>cascadePeriods</c>/<c>cascadeAppend</c>/<c>cascadeSeeds</c>), теж чекає. Сумнів — у бік
    /// «чекати»: це секунди вікна злиття й черги, а хибне «можна» закріпило б застарілі числа.</item>
    /// <item>Крок, що виконується (<c>Running</c>), може дописати в план пізніших читачів своїх змінених чисел — поки він
    /// іде, кожен період, пізніший за його (перший у <c>chainPlan</c>), теж чекає.</item>
    /// <item>Старий payload ланцюжка без плану — усі періоди після <c>chainAfter</c>.</item>
    /// <item>Payload не читається — чекає (консервативно).</item>
    /// </list>
    /// </remarks>
    /// <param name="running">Рядок у стані <c>Running</c> (крок виконується).</param>
    /// <param name="payload">Тіло задачі.</param>
    /// <param name="periodKey">Період споживача.</param>
    internal static bool Pends(bool running, string? payload, int periodKey)
    {
        RecalculationRequest? request;
        try
        {
            request = payload is null ? null : JsonSerializer.Deserialize<RecalculationRequest>(payload, PayloadOptions);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return true;
        }

        if (request.ChainAfter is not { } after)
        {
            // Перший крок ще не завершився: план не записано. Задача року без періодів каскаду — не наша форма: увесь рік.
            if (request.CascadePeriods is null)
            {
                return true;
            }

            return Has(request.CascadePeriods, periodKey)
                   || Has(request.CascadeAppend, periodKey)
                   || periodKey > Floor(request.CascadePeriods, request.CascadeAppend, request.CascadeSeeds);
        }

        // ⚠ Продовження ланцюжка несе ПЕРШІ cascadePeriods далі (`request with { … }`) — після першого кроку вони вже не
        // ознака роботи; чинні решта плану (chainPlan) і злиті після нього періоди (cascadeAppend).
        if (Has(request.ChainPlan, periodKey) || Has(request.CascadeAppend, periodKey))
        {
            return true;
        }

        if (request.CascadeAppend is not null)
        {
            return periodKey > Floor(request.CascadeAppend, request.CascadeSeeds);
        }

        // Крок, що ВИКОНУЄТЬСЯ (перший у chainPlan), дописує в план пізніших читачів своїх змінених чисел
        // (`ExtendWithLaterReadersAsync`) — поки він іде, пізніші за нього теж чекають.
        if (running && request.ChainPlan is { Length: > 0 } plan && periodKey > plan.Min())
        {
            return true;
        }

        return request.ChainPlan is null && periodKey > after;
    }

    private static int Floor(params int[]?[] sets)
        => sets.SelectMany(s => s ?? []).DefaultIfEmpty(int.MaxValue).Min();

    private static bool Has(int[]? periods, int periodKey) => periods is not null && Array.IndexOf(periods, periodKey) >= 0;

    /// <summary>Рядок черги каскаду.</summary>
    /// <param name="TargetKey">Ціль.</param>
    /// <param name="State">Стан (<c>Queued</c>/<c>Running</c>).</param>
    /// <param name="Payload">Тіло задачі.</param>
    private sealed record CascadeRow(string TargetKey, string State, string? Payload);
}
