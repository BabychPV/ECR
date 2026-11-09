using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// SHEETNAME(): постановка перерахунку формул аркуша, коли змінилась його показувана назва або з'явились нові
/// екземпляри (перше відкриття копії). Один шлях для <see cref="DocumentSheetCopyHandler"/> і
/// <see cref="GetDocumentTablesHandler"/>: задача <see cref="IRecalculationJob"/> на ціль «документ × період»,
/// без витіснення (<c>Coalesced</c>) — та сама, що після правки шапки.
/// </summary>
public static class SheetNameRecalculation
{
    /// <summary>Аркуші версії, формули яких згадують <c>SHEETNAME</c> (за текстом виразу).</summary>
    /// <param name="snapshot">Структура версії.</param>
    public static IReadOnlyList<SheetDef> SheetsUsing(TemplateVersionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return
        [
            .. snapshot.Sheets.Where(s => !s.IsDeleted
                                          && Recalculation.SheetDisplayNames.MayUse(
                                              s.Tables.SelectMany(t => t.Formulas).Select(f => f.Expression))),
        ];
    }

    /// <summary>
    /// Обчислювані колонки таблиці, колонкові формули яких згадують <c>SHEETNAME</c> (за текстом виразу) — насіння
    /// перерахунку нового рядка.
    /// </summary>
    /// <param name="table">Таблиця версії.</param>
    /// <param name="columns">Колонки версії за Id (<see cref="TemplateVersionSnapshot.ColumnsById"/>).</param>
    /// <remarks>
    /// Новий рядок не має жодної введеної комірки, тож каскад від правки його не зачіпає, а перегляд рядка
    /// (<c>RowLocalPreview</c>) <c>SHEETNAME</c> навмисно пропускає: без цього насіння комірка лишалась порожньою
    /// до ручного перерахунку (найпомітніше в копії аркуша — «Vessel 2»).
    /// </remarks>
    public static IReadOnlyList<int> NewRowColumns(TableDef table, IReadOnlyDictionary<int, ColumnDef> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        return
        [
            .. table.Formulas
                .Where(f => f.Scope == Domain.Enums.FormulaScope.Column
                            && f.ColumnDefId is { } columnId
                            && columns.TryGetValue(columnId, out var column)
                            && column.IsComputed
                            && Recalculation.SheetDisplayNames.MayUse([f.Expression]))
                .Select(f => f.ColumnDefId!.Value)
                .Distinct(),
        ];
    }

    /// <summary>
    /// Ставить задачу формул (<see cref="IFormulaRecalculationJob"/>, та сама, що після PATCH і видалення рядка) з
    /// насінням «новий рядок × колонка SHEETNAME»; злиття — за документо-періодом і автором.
    /// </summary>
    /// <param name="jobs">Черга задач.</param>
    /// <param name="instance">Екземпляр таблиці (оригінал чи копія аркуша).</param>
    /// <param name="rowId">Новий рядок.</param>
    /// <param name="columnDefIds">Колонки з <see cref="NewRowColumns"/>.</param>
    /// <param name="userId">Автор (статус задачі доступний йому, Q-156).</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<string> EnqueueNewRowAsync(
        IBackgroundJobScheduler jobs, TableInstanceRef instance, long rowId, IReadOnlyList<int> columnDefIds, int userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(columnDefIds);

        return jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
            FormulaRecalculationTarget.Of(instance.DocumentId, instance.PeriodKey, userId),
            new
            {
                DocumentId = instance.DocumentId,
                TableInstanceId = instance.TableInstanceId,
                PeriodKey = instance.PeriodKey,
                Cells = RecalculationSeed.Stamp(
                    columnDefIds.Select(c => new RecalculationSeed(rowId, c)), instance.PeriodKey, instance.TableInstanceId),
            },
            ct,
            createdByUserId: userId);
    }

    /// <summary>Ставить перерахунок документа за період, звужений до аркуша (<c>null</c> — увесь документ).</summary>
    /// <param name="jobs">Черга задач.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="period">Період.</param>
    /// <param name="sheetDefId">Аркуш; <c>null</c> — кілька аркушів, увесь документ (злиття постановок на одну ціль
    /// інакше лишило б лише перший аркуш).</param>
    /// <param name="userId">Хто спричинив; <c>null</c> — системна.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<string> EnqueueAsync(
        IBackgroundJobScheduler jobs, long documentId, PeriodKey period, int? sheetDefId, int? userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        return jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(documentId, period),
            new
            {
                DocumentId = documentId,
                PeriodKey = period.Value,
                TriggeredByUserId = userId,
                SheetDefId = sheetDefId,
            },
            ct,
            userId);
    }
}
