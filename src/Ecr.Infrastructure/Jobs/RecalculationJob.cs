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

        var run = new Domain.Entities.Calculations.CalculationRun(
            request.ProjectId, request.PeriodKey, request.TriggeredByUserId, clock.UtcNow);

        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
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
            // ⚠ Провал теж ЗАВЕРШУЄ прогін, а не лишає його «Running» назавжди.
            // Прогін, що висить у стані виконання, виглядає як довгий — і його
            // чекають замість того, щоб перезапустити.
            run.Complete("Failed", clock.UtcNow, profileJson: null, errorMessage: ex.Message);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
