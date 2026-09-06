using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;

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
public sealed class FormulaRecalculationJob(RecalculationService recalculation) : IFormulaRecalculationJob
{
    /// <summary>Налаштування розбору завдання; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

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
            await progress.ReportAsync(100, "Змінених комірок немає.", ct).ConfigureAwait(false);

            return;
        }

        var written = await recalculation
            .RecalculateAsync(request.TableInstanceId, dirty, ct)
            .ConfigureAwait(false);

        await progress
            .ReportAsync(100, $"Перераховано комірок: {written}.", ct)
            .ConfigureAwait(false);
    }

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
