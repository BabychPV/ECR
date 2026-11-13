// src/Ecr.Application/Ports/IFormulaErrorStore.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>✎ C-1 / D-475: поточний стан помилок формул (<c>calc.FormulaError</c>).</summary>
public interface IFormulaErrorStore
{
    /// <summary>Стан комірок екземплярів одного періоду — ОДНИМ запитом; порожній набір — без запиту.</summary>
    /// <param name="tableInstanceIds">Екземпляри таблиць.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<FormulaErrorRow>> ReadInstancesAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Замінює стан комірок: <paramref name="upserts"/> пишуться (попередній рядок тієї ж комірки видаляється),
    /// <paramref name="deletes"/> — видаляються. Видалення — одразу, у транзакції викликача; нові рядки — в контекст
    /// (їх зберігає викликач своїм <c>SaveChanges</c>, як <c>ICalculationIssueStore.ReplaceAsync</c>).
    /// </summary>
    /// <param name="upserts">Нові стани комірок.</param>
    /// <param name="deletes">Адреси комірок, стан яких знімається.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task ApplyAsync(
        IReadOnlyCollection<FormulaErrorRow> upserts, IReadOnlyCollection<CellAddress> deletes, CancellationToken ct);

    /// <summary>Стан документа за період і копію (панель проблем, «Перевірити») — ОДНИМ запитом, лише живі рядки.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінали.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<FormulaErrorView>> ListAsync(
        long documentId, int periodKey, long? sheetCopyId, CancellationToken ct);

    /// <summary>
    /// ✎ B78: назви записів довідника для входів помилки, що є значеннями довідникових колонок («Diesel», а не «37») —
    /// ОДНИМ запитом. Пара не з довідникової колонки чи невідомий запис — відсутня у відповіді.
    /// </summary>
    /// <param name="refs">Пари (колонка входу, Id запису довідника).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Реалізація за замовчуванням (тестові двійники) — без назв: панель показує значення як є.</remarks>
    public Task<IReadOnlyDictionary<(int ColumnDefId, long EntryId), LocalizedText>> LookupNamesAsync(
        IReadOnlyCollection<(int ColumnDefId, long EntryId)> refs, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<(int ColumnDefId, long EntryId), LocalizedText>>(
            new Dictionary<(int ColumnDefId, long EntryId), LocalizedText>());
}

/// <summary>✎ C-1: рядок стану помилки формули (для читання й запису).</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="TableDefId">Таблиця шаблону.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="TableRowId">Рядок.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="FormulaDefId">Формула комірки.</param>
/// <param name="Kind"><c>Error</c> або <c>Blocked</c>.</param>
/// <param name="ErrorCode">Код помилки (для Blocked — код джерела).</param>
/// <param name="InputsJson">Вхідні значення помилки (JSON); лише для Error.</param>
/// <param name="SourcePeriodKey">Період джерела (Blocked).</param>
/// <param name="SourceTableDefId">Таблиця джерела (Blocked).</param>
/// <param name="SourceRowKey">Рядок джерела (Blocked).</param>
/// <param name="SourceColumnDefId">Колонка джерела (Blocked).</param>
/// <param name="Since">Відколи (UTC).</param>
public sealed record FormulaErrorRow(
    long DocumentId, int PeriodKey, long TableInstanceId, int TableDefId, long? SheetCopyId,
    long TableRowId, string RowKey, int ColumnDefId, int FormulaDefId, string Kind, string ErrorCode,
    string? InputsJson, int? SourcePeriodKey, int? SourceTableDefId, string? SourceRowKey, int? SourceColumnDefId,
    DateTime Since)
{
    /// <summary>Адреса комірки.</summary>
    public CellAddress Address => new(new PeriodKey(PeriodKey), TableRowId, ColumnDefId);
}

/// <summary>✎ C-1: рядок стану з кодами для показу (з'єднання з cfg.* і живим doc.TableRow).</summary>
/// <param name="Row">Стан.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="ColumnHeaderL10n">Заголовок колонки.</param>
/// <param name="RowOrdinal">
/// Номер рядка, як його бачить сітка: позиція з 1 у порядку зрізу (Ordinal опису рядка шаблону, далі
/// <c>TableRow.Id</c>). ✎ B78: не <c>doc.TableRow.Ordinal</c> — у динамічних таблицях він однаковий (0) у всіх рядків.
/// </param>
/// <param name="Formula">Вираз формули; <c>null</c> — формули вже немає.</param>
/// <param name="SourceTableCode">Код таблиці джерела (Blocked).</param>
/// <param name="SourceColumnCode">Код колонки джерела (Blocked).</param>
/// <param name="RowLabelL10n">Назва фіксованого рядка шаблону; <c>null</c> — динамічний рядок.</param>
public sealed record FormulaErrorView(
    FormulaErrorRow Row, string TableCode, string ColumnCode, LocalizedText ColumnHeaderL10n,
    int RowOrdinal, string? Formula, string? SourceTableCode, string? SourceColumnCode,
    LocalizedText? RowLabelL10n = null);
