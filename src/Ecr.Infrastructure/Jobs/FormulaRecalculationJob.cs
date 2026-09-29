using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Каскадний перерахунок ФОРМУЛ ШАБЛОНУ після правки комірок.
/// </summary>
/// <remarks>
/// ⛔ Задача нова, і поява її — це виправлення `A7-63`. До цього
/// <c>PatchCellsHandler</c> ставив у чергу <c>IRecalculationJob</c> — задачу
/// МЕТОДОЛОГІЙ — із тілом <c>{TableInstanceId, PeriodKey}</c>, якого та не
/// розуміє: її запит має поля <c>ProjectId</c>, <c>DocumentId</c>,
/// <c>PeriodKey</c>, <c>TriggeredByUserId</c>. Розбір давав нулі, тобто після
/// кожної правки комірки в чергу лягала задача, яка не могла зробити нічого.
///
/// ⚠ Дві задачі з іменем «перерахунок» — не дублювання. Результати формул
/// шаблону лежать у <c>doc.CellValue</c> з <c>IsCalculated = 1</c>, результати
/// методологій — у <c>calc.CalculationResult</c> (<c>D-69</c>). Плутати ці два
/// шляхи не можна, і саме плутанина й сталася.
/// </remarks>
public sealed class FormulaRecalculationJob(RecalculationService recalculation, EcrDbContext db) : IFormulaRecalculationJob
{
    /// <summary>Налаштування розбору завдання; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Скільки одна спроба чекає лока документа, поки його тримає повний перерахунок.</summary>
    /// <remarks>
    /// ⚠ Коротше за повний перерахунок (<see cref="RecalculationJob.DocumentLockTimeout"/>):
    /// задача займає слот лейна <c>Default</c>, спільного з експортом та іншим, і
    /// тримати його весь річний прогін не можна. Не дочекалися —
    /// <see cref="InvalidOperationException"/>, і <c>JobRetryPolicy</c> повертає
    /// задачу в чергу (30/60/120 с): 4 спроби × 2 хв + 3.5 хв відступу ≈ 11.5 хв
    /// — більше за бюджет повного року (10 хв, ПРД-13). Без суперника лок береться
    /// одразу, тож звичайний шлях після PATCH не сповільнюється.
    /// </remarks>
    internal static readonly TimeSpan DocumentLockTimeout = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = Parse(payload);

        var dirty = new DirtySet();
        var periodKey = new PeriodKey(request.PeriodKey);

        foreach (var cell in request.Cells)
        {
            dirty.Add(new CellAddress(periodKey, cell.RowId, cell.ColumnDefId));
        }

        // ⛔ Порожній набір змінених комірок — це не «перерахувати все», а
        // «нема чого рахувати». Повний перерахунок від порожнього списку
        // перетворив би кожну правку на прогін по всьому документу.
        if (dirty.IsEmpty)
        {
            await progress.ReportKeyAsync(100, "jobs.formulaRecalcNone", ct).ConfigureAwait(false);

            return;
        }

        // ⛔ Лок документа — той самий, що в повного перерахунку
        // (`RecalculationDocumentLock`). Повний читає входи поза транзакцією запису;
        // без лока він, прочитавши вхід ДО цього PATCH, записував своє старіше
        // похідне число ПІСЛЯ нашого свіжого (`FormulaRecalculationDocumentLockTests`).
        // Під локом порядок один: хто б не був першим, останнім пише той, хто
        // читав останній вхід. Лок береться тут, у задачі, а не в транзакції
        // PATCH: там лише постановка в чергу.
        var documentId = await DocumentOfAsync(request, ct).ConfigureAwait(false);

        await using var documentLock = await RecalculationDocumentLock
            .AcquireAsync(db, documentId, DocumentLockTimeout, ct)
            .ConfigureAwait(false);

        var written = await recalculation
            .RecalculateAsync(request.TableInstanceId, dirty, ct)
            .ConfigureAwait(false);

        await progress
            .ReportKeyAsync(
                100,
                "jobs.formulaRecalcDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["written"] = written.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Документ екземпляра таблиці; <c>0</c> — екземпляра немає.</summary>
    /// <remarks>
    /// ⚠ З ключем періоду: <c>doc.TableInstance</c> партиціонована за ним. Немає
    /// екземпляра — лок не береться, а <see cref="RecalculationService.RecalculateAsync"/>
    /// нижче відмовить власним повідомленням.
    /// </remarks>
    private Task<long> DocumentOfAsync(FormulaRecalculationRequest request, CancellationToken ct)
        => db.TableInstances
            .AsNoTracking()
            .Where(i => i.Id == request.TableInstanceId && i.PeriodKeyValue == request.PeriodKey)
            .Select(i => i.DocumentId)
            .FirstOrDefaultAsync(ct);

    private static FormulaRecalculationRequest Parse(object? payload)
    {
        if (payload is FormulaRecalculationRequest typed)
        {
            return typed;
        }

        var json = payload as string ?? JsonSerializer.Serialize(payload);

        return JsonSerializer.Deserialize<FormulaRecalculationRequest>(json, PayloadOptions)
               ?? throw new InvalidOperationException(
                   "Завдання перерахунку формул не розбирається: невідома форма payload.");
    }
}

/// <summary>Завдання на каскадний перерахунок формул.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці, у якому сталася правка.</param>
/// <param name="PeriodKey">Період правки.</param>
/// <param name="Cells">Змінені комірки — насіння каскаду.</param>
/// <remarks>
/// ⚠ Змінені комірки передаються ЯВНО, а не виводяться із «щось змінилося».
/// Без них перерахунок був би повним на кожну правку, і сенс графа
/// залежностей зник би разом із його вартістю.
/// </remarks>
public sealed record FormulaRecalculationRequest(
    long TableInstanceId, int PeriodKey, IReadOnlyList<DirtyCell> Cells);

/// <summary>Змінена комірка в завданні перерахунку.</summary>
/// <param name="RowId">Рядок документа.</param>
/// <param name="ColumnDefId">Колонка.</param>
public sealed record DirtyCell(long RowId, int ColumnDefId);
