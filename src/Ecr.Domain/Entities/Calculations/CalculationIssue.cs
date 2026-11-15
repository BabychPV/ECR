// src/Ecr.Domain/Entities/Calculations/CalculationIssue.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Поточна проблема розрахунку (<c>calc.CalculationIssue</c>, AN-69): рядок таблиці, якому не підійшло жодне правило,
/// або рядок, відхилений правилом категорії.
/// </summary>
/// <remarks>
/// ⛔ Таблиця тримає ЛИШЕ поточні проблеми (рішення людини 28.10: лише останній результат): прохід
/// (документ × період × таблиця × копія) видаляє свої попередні рядки й вставляє поточні в одній транзакції з
/// результатами. Значень комірок тут немає навмисно — лише адреса рядка.
/// </remarks>
public sealed class CalculationIssue : Entity<long>
{
    /// <summary>Вид: рядок без правила методології.</summary>
    public const string NoMatchingRuleKind = "NoMatchingRule";

    /// <summary>
    /// Вид: правило методології рядок закрило, але правило категорії версії не дало ключа (L2-6) — рядок відхилено.
    /// Правило й порожні колонки виводить читання (<c>CalculationRejectionReasons</c>), у рядку їх немає (без міграції).
    /// </summary>
    public const string CategoryRuleRejectedKind = "CategoryRuleRejected";

    /// <summary>
    /// Вид (AN-164): правило методології рядок закрило, але формулам бракує аргументів — жодного результату. Колонки, які
    /// треба заповнити, виводить читання (<c>CalculationRejectionReasons</c>), у рядку їх немає (без міграції).
    /// </summary>
    public const string MissingInputsKind = "MissingInputs";

    /// <summary>
    /// Вид (B66): крок методики рядка не порахувався (<c>#VALUE</c>, <c>#ARG</c>, <c>#DIV/0</c>…). ОДНА проблема на рядок —
    /// перший невдалий крок (<see cref="StepCode"/>, <see cref="ErrorCode"/>) і скільки різних кроків рядка не порахувалось
    /// (<see cref="AffectedCount"/>); <c>#NULL</c> («немає значення») сюди не йде — це <see cref="MissingInputsKind"/>.
    /// </summary>
    public const string StepErrorKind = "StepError";

    /// <summary>
    /// Вид (B66): рядки таблиці, відсіяні фільтром активності методики (D-425, «лише Running»). ОДИН підсумок на таблицю —
    /// перший такий рядок (<see cref="RowKey"/>, <see cref="RowNumber"/>) і кількість (<see cref="AffectedCount"/>): у Land
    /// неактивні рядки — виключення за задумом, тож поштучно список не засмічують.
    /// </summary>
    public const string RowInactiveKind = "RowInactive";

    /// <summary>Стеля колонки <see cref="RowKey"/>.</summary>
    public const int MaxRowKeyLength = 100;

    /// <summary>Стеля колонки <see cref="StepCode"/> (код формули методики).</summary>
    public const int MaxStepCodeLength = 100;

    /// <summary>Стеля колонки <see cref="ErrorCode"/> (<c>#VALUE</c>, <c>#DIV/0</c>…).</summary>
    public const int MaxErrorCodeLength = 16;

    private CalculationIssue() { }

    /// <summary>Створює проблему «рядок без правила».</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="tableDefId">Таблиця шаблону.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="rowNumber">Номер рядка, з 1.</param>
    /// <param name="methodologyId">Методологія прив'язки; <c>null</c> — невідома.</param>
    /// <param name="calculationRunId">Прогін, що виявив проблему.</param>
    /// <param name="detectedAt">Момент виявлення (UTC).</param>
    /// <param name="kind">Вид; <c>null</c> — <see cref="NoMatchingRuleKind"/>.</param>
    /// <param name="stepCode">B66: перший невдалий крок (<see cref="StepErrorKind"/>); інакше <c>null</c>.</param>
    /// <param name="errorCode">B66: його код помилки (<see cref="StepErrorKind"/>); інакше <c>null</c>.</param>
    /// <param name="affectedCount">
    /// B66: <see cref="StepErrorKind"/> — скільки різних кроків рядка не порахувалось; <see cref="RowInactiveKind"/> — скільки
    /// рядків таблиці відсіяно; інакше <c>null</c>.
    /// </param>
    public CalculationIssue(
        long documentId,
        int periodKey,
        int tableDefId,
        long? sheetCopyId,
        string rowKey,
        int rowNumber,
        int? methodologyId,
        long calculationRunId,
        DateTime detectedAt,
        string? kind = null,
        string? stepCode = null,
        string? errorCode = null,
        int? affectedCount = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rowKey);

        DocumentId = documentId;
        PeriodKey = periodKey;
        TableDefId = tableDefId;
        SheetCopyId = sheetCopyId;
        RowKey = rowKey;
        RowNumber = rowNumber;
        Kind = string.IsNullOrEmpty(kind) ? NoMatchingRuleKind : kind;
        MethodologyId = methodologyId;
        CalculationRunId = calculationRunId;
        DetectedAt = detectedAt;
        StepCode = Truncate(stepCode, MaxStepCodeLength);
        ErrorCode = Truncate(errorCode, MaxErrorCodeLength);
        AffectedCount = affectedCount;
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    public long DocumentId { get; private set; }
    public int PeriodKey { get; private set; }
    public int TableDefId { get; private set; }

    /// <summary>Копія аркуша (<c>doc.TableInstance.SheetCopyId</c>); <c>null</c> — оригінал.</summary>
    public long? SheetCopyId { get; private set; }

    public string RowKey { get; private set; } = null!;
    public int RowNumber { get; private set; }
    public string Kind { get; private set; } = null!;
    /// <summary>Методологія (не версія) прив'язки таблиці; <c>null</c> — невідома.</summary>
    public int? MethodologyId { get; private set; }
    public long CalculationRunId { get; private set; }
    public DateTime DetectedAt { get; private set; }

    /// <summary>B66: перший невдалий крок рядка (<see cref="StepErrorKind"/>); <c>null</c> — інший вид.</summary>
    public string? StepCode { get; private set; }

    /// <summary>B66: код помилки кроку <see cref="StepCode"/>; <c>null</c> — інший вид.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>B66: кількість — невдалих кроків рядка або відсіяних рядків таблиці; <c>null</c> — інший вид.</summary>
    public int? AffectedCount { get; private set; }
}
