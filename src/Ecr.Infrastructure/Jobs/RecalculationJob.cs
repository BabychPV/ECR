using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перерахунок: інкрементний за dirty-set або повний за адміністративною
/// командою.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, і формулювання «не гірше» тут не застосовується.
/// Паралельність за пакетами графа і пакетне читання входів живуть в
/// <c>CalculationOrchestrator</c>; тут — розбір завдання, життєвий цикл
/// прогону і завершення.
/// <para>
/// ⚠ Клас реалізує <see cref="IRecalculationJob"/>, а не лише
/// <c>IBackgroundJob</c>. Маркер існує саме щоб use-case міг назвати задачу,
/// не знаючи її реалізації; без нього <c>EnqueueAsync&lt;IRecalculationJob&gt;</c>
/// не мав би що запустити — черга приймала б завдання, і не робилося б нічого.
/// </para>
/// </remarks>
public sealed class RecalculationJob(
    EcrDbContext db,
    ICalculationRunner orchestrator,
    RunCalculationHandler runs,
    Domain.Abstractions.IClock clock) : IRecalculationJob
{
    /// <summary>Стеля прив'язок на прогін: методологій у системі — десятки.</summary>
    private const int MaxBindings = 5_000;

    /// <summary>Налаштування розбору завдання; спільні на всі виклики.</summary>
    /// <remarks>
    /// Один екземпляр на клас, а не на виклик: <c>JsonSerializerOptions</c>
    /// кешує метадані типів усередині, і новий об'єкт щоразу означає повторний
    /// розбір рефлексією на кожне завдання черги.
    /// </remarks>
    private static readonly JsonSerializerOptions PayloadOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = Parse(payload);

        // ⛔ `ProjectId` визначається з ДОКУМЕНТА, коли payload його не несе, а
        // не приймається як 0. `RecalculateDocumentHandler` кладе в чергу
        // `new { DocumentId, PeriodKey }` — без `ProjectId` узагалі; при
        // розборі в non-nullable `int` це мовчки стає `0`. `CalculationRun`
        // із `ProjectId = 0` не проходить `FK_CalculationRun_Project`, і
        // `SaveChangesAsync` нижче кидав `SqlException 547` — виміряно живим
        // прогоном (директива №09 §1.3).
        var projectId = request.ProjectId > 0
            ? request.ProjectId
            : await db.Documents
                .AsNoTracking()
                .Where(d => d.Id == request.DocumentId)
                .Select(d => d.ProjectId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        var run = new Domain.Entities.Calculations.CalculationRun(
            projectId, request.PeriodKey, request.TriggeredByUserId, clock.UtcNow);

        try
        {
            // ⛔ Створення й ПЕРШЕ збереження — ВСЕРЕДИНІ `try`, а не до нього.
            // Раніше стояли до `try`: коли сам `INSERT` провалювався (рівно
            // так і сталося з `ProjectId = 0` вище), виняток летів МИМО catch
            // нижче — і задача лишалася `Running` назавжди, хоча catch
            // виглядав так, ніби мав це перехопити.
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var bindings = await BindingsAsync(request, ct).ConfigureAwait(false);

            var profile = await orchestrator
                .RunAsync(
                    run.Id,
                    request.DocumentId,
                    new PeriodKey(request.PeriodKey ?? 0),
                    bindings,
                    progress,
                    ct)
                .ConfigureAwait(false);

            // Завершення — прикладний сценарій: профіль і перемикання
            // актуальності однією транзакцією (ФВ-9.11).
            await runs.CompleteAsync(run.Id, profile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ⛔ Трекер очищається ПЕРЕД повторним записом. `EcrDbContext` тут
            // — ТОЙ САМИЙ скоуп-інстанс, яким `QuartzJobAdapter` пише
            // `itg.JobProgress` (`JobProgressStore`, той самий DI-скоуп): якщо
            // лишити в трекері сутність, чий `INSERT` щойно провалився,
            // НАСТУПНЕ `SaveChangesAsync` — навіть чуже, запис прогресу в
            // `itg.JobProgress` — повторно спробує вставити той самий
            // зіпсований рядок і провалиться теж. Тоді `QuartzJobAdapter`
            // не зможе позначити задачу `Failed`, і вона лишиться `Running`
            // назавжди — це і є справжня причина «задача висить 0 %»
            // (директива №09 §1.3), а не сам факт «catch не викликається».
            db.ChangeTracker.Clear();

            // Прогін позначається `Failed` лише якщо його `INSERT` УСПІШНО
            // відбувся (`run.Id` призначений базою): позначати нема чого,
            // якщо самого рядка в базі немає.
            if (run.Id > 0)
            {
                db.CalculationRuns.Attach(run);
                run.Complete("Failed", clock.UtcNow, profileJson: null, errorMessage: ex.Message);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Прив'язки методологій до таблиць документа.</summary>
    /// <remarks>
    /// Прив'язка живе в <c>cfg.CalculationBinding</c> і посилається на
    /// <c>TableDefId</c> — опис таблиці. Прогін працює з ЕКЗЕМПЛЯРАМИ, тому
    /// опис розгортається в екземпляри цього документа й періоду.
    /// </remarks>
    private async Task<List<CalculationBindingRef>> BindingsAsync(
        RecalculationRequest request, CancellationToken ct)
    {
        var instances = await db.TableInstances
            .AsNoTracking()
            .Where(i => i.DocumentId == request.DocumentId
                        && (request.PeriodKey == null || i.PeriodKeyValue == request.PeriodKey))
            .Take(MaxBindings)
            .Select(i => new InstanceRow(i.Id, i.TableDefId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            return [];
        }

        var tableDefIds = instances.Select(i => i.TableDefId).Distinct().ToList();

        var bindings = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && tableDefIds.Contains(b.TableDefId))
            .Take(MaxBindings)
            .Select(b => new BindingRow(b.TableDefId, b.MethodologyId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return instances
            .SelectMany(i => bindings
                .Where(b => b.TableDefId == i.TableDefId)
                .Select(b => new CalculationBindingRef(i.Id, b.MethodologyId)))
            .Distinct()
            .ToList();
    }

    /// <summary>Розбирає завдання черги.</summary>
    /// <remarks>
    /// Payload приходить як анонімний об'єкт від use-case і як JSON із черги —
    /// обидва шляхи мусять читатися однаково, інакше задача працювала б
    /// у тесті й падала в проді.
    /// </remarks>
    private static RecalculationRequest Parse(object? payload)
    {
        if (payload is RecalculationRequest typed)
        {
            return typed;
        }

        var json = payload as string ?? JsonSerializer.Serialize(payload);

        return JsonSerializer.Deserialize<RecalculationRequest>(json, PayloadOptions)
               ?? throw new InvalidOperationException(
                   "Завдання перерахунку не розбирається: невідома форма payload.");
    }

    /// <summary>Екземпляр таблиці документа.</summary>
    private sealed record InstanceRow(long Id, int TableDefId);

    /// <summary>Прив'язка методології до опису таблиці.</summary>
    private sealed record BindingRow(int TableDefId, int MethodologyId);
}

/// <summary>Завдання на перерахунок.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentId">Документ; нуль — усі документи проєкту.</param>
/// <param name="PeriodKey">Період; <c>null</c> — повний рік.</param>
/// <param name="TriggeredByUserId">Хто запустив; <c>null</c> — за розкладом.</param>
public sealed record RecalculationRequest(
    int ProjectId, long DocumentId, int? PeriodKey, int? TriggeredByUserId);
