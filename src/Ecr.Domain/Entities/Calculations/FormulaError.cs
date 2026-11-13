// src/Ecr.Domain/Entities/Calculations/FormulaError.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// ✎ C-1 / D-475: ПОТОЧНИЙ стан помилки формули комірки (<c>calc.FormulaError</c>): власна помилка обчислення
/// (<see cref="ErrorKind"/>) або «не оновлено через помилку в іншій комірці» (<see cref="BlockedKind"/>).
/// </summary>
/// <remarks>
/// ⛔ Значення комірки тут НЕ зберігається: у <c>doc.CellValue</c> лишається останнє правильне (рішення PO 11.11).
/// Рядок живе, доки помилка триває; успішний перерахунок його видаляє. Зовнішніх ключів немає навмисно (архівація
/// звільняє партиції <c>doc.TableRow</c> через TRUNCATE), читачі відкидають рядки без живого рядка таблиці.
/// </remarks>
public sealed class FormulaError : Entity<long>
{
    /// <summary>Власна помилка обчислення формули комірки.</summary>
    public const string ErrorKind = "Error";

    /// <summary>Комірка не оновлена через помилку в іншій (транзитивно).</summary>
    public const string BlockedKind = "Blocked";

    /// <summary>Стеля ключа рядка.</summary>
    public const int MaxRowKeyLength = 100;

    /// <summary>Стеля коду помилки.</summary>
    public const int MaxErrorCodeLength = 16;

    /// <summary>Стеля JSON вхідних значень.</summary>
    public const int MaxInputsLength = 2000;

    private FormulaError() { }

    /// <summary>Створює стан помилки комірки.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="tableDefId">Таблиця шаблону.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="tableRowId">Рядок.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="columnDefId">Колонка.</param>
    /// <param name="formulaDefId">Формула, що рахує комірку.</param>
    /// <param name="kind"><see cref="ErrorKind"/> або <see cref="BlockedKind"/>.</param>
    /// <param name="errorCode">Код помилки (для Blocked — код джерела).</param>
    /// <param name="inputsJson">Вхідні значення (лише Error).</param>
    /// <param name="sourcePeriodKey">Період комірки-джерела (Blocked).</param>
    /// <param name="sourceTableDefId">Таблиця комірки-джерела (Blocked).</param>
    /// <param name="sourceRowKey">Рядок комірки-джерела (Blocked).</param>
    /// <param name="sourceColumnDefId">Колонка комірки-джерела (Blocked).</param>
    /// <param name="since">Відколи (UTC).</param>
    public FormulaError(
        long documentId, int periodKey, long tableInstanceId, int tableDefId, long? sheetCopyId,
        long tableRowId, string rowKey, int columnDefId, int formulaDefId, string kind, string errorCode,
        string? inputsJson, int? sourcePeriodKey, int? sourceTableDefId, string? sourceRowKey, int? sourceColumnDefId,
        DateTime since)
    {
        ArgumentException.ThrowIfNullOrEmpty(rowKey);
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        if (kind is not (ErrorKind or BlockedKind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Kind is Error or Blocked.");
        }

        DocumentId = documentId;
        PeriodKey = periodKey;
        TableInstanceId = tableInstanceId;
        TableDefId = tableDefId;
        SheetCopyId = sheetCopyId;
        TableRowId = tableRowId;
        RowKey = rowKey;
        ColumnDefId = columnDefId;
        FormulaDefId = formulaDefId;
        Kind = kind;
        ErrorCode = errorCode.Length > MaxErrorCodeLength ? errorCode[..MaxErrorCodeLength] : errorCode;
        InputsJson = inputsJson;
        SourcePeriodKey = sourcePeriodKey;
        SourceTableDefId = sourceTableDefId;
        SourceRowKey = sourceRowKey;
        SourceColumnDefId = sourceColumnDefId;
        Since = since;
    }

    /// <summary>Документ.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Період.</summary>
    public int PeriodKey { get; private set; }

    /// <summary>Екземпляр таблиці.</summary>
    public long TableInstanceId { get; private set; }

    /// <summary>Таблиця шаблону.</summary>
    public int TableDefId { get; private set; }

    /// <summary>Копія аркуша; <c>null</c> — оригінал.</summary>
    public long? SheetCopyId { get; private set; }

    /// <summary>Рядок таблиці.</summary>
    public long TableRowId { get; private set; }

    /// <summary>Ключ рядка.</summary>
    public string RowKey { get; private set; } = null!;

    /// <summary>Колонка.</summary>
    public int ColumnDefId { get; private set; }

    /// <summary>Формула, що рахує комірку.</summary>
    public int FormulaDefId { get; private set; }

    /// <summary><see cref="ErrorKind"/> або <see cref="BlockedKind"/>.</summary>
    public string Kind { get; private set; } = null!;

    /// <summary>Код помилки (<c>#DIV/0</c>, <c>#REF</c>, <c>#BUDGET</c>…); для Blocked — код джерела.</summary>
    public string ErrorCode { get; private set; } = null!;

    /// <summary>Вхідні значення, що дали помилку (JSON, до 20 записів); лише для Error.</summary>
    public string? InputsJson { get; private set; }

    /// <summary>Період комірки-джерела (Blocked).</summary>
    public int? SourcePeriodKey { get; private set; }

    /// <summary>Таблиця комірки-джерела (Blocked).</summary>
    public int? SourceTableDefId { get; private set; }

    /// <summary>Рядок комірки-джерела (Blocked).</summary>
    public string? SourceRowKey { get; private set; }

    /// <summary>Колонка комірки-джерела (Blocked).</summary>
    public int? SourceColumnDefId { get; private set; }

    /// <summary>Відколи помилка триває (UTC); не змінюється, поки той самий стан повторюється.</summary>
    public DateTime Since { get; private set; }
}
