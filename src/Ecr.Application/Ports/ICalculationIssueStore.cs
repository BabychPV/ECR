// src/Ecr.Application/Ports/ICalculationIssueStore.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Поточні проблеми розрахунку (<c>calc.CalculationIssue</c>, AN-69): запис при завершенні прогону і читання для аркуша.
/// </summary>
/// <remarks>
/// ⛔ Зберігаються ЛИШЕ поточні проблеми (рішення людини 28.10: лише останній результат): прохід
/// (документ × період × таблиця × копія) замінює свої попередні рядки, а не додає до них.
/// </remarks>
public interface ICalculationIssueStore
{
    /// <summary>Вид проблеми «рядок без правила методології».</summary>
    public const string NoMatchingRule = "NoMatchingRule";

    /// <summary>Вид проблеми «рядок відхилено правилом категорії» (L2-6): правило закрило рядок, ключа категорії немає.</summary>
    public const string CategoryRuleRejected = "CategoryRuleRejected";

    /// <summary>
    /// Вид проблеми «рядок без значення» (AN-164): правило закрило рядок, але формулам бракує аргументів (немає комірки
    /// чи вона порожня) — жодного результату. Колонки, які треба заповнити, виводить читання.
    /// </summary>
    public const string MissingInputs = "MissingInputs";

    /// <summary>
    /// Вид проблеми «вихід методики без результату через помилку» (B66: <c>#VALUE</c>, <c>#ARG</c>…): одна проблема на
    /// рядок — перший такий вихід і код, кількість різних виходів рядка без результату.
    /// </summary>
    public const string StepError = "StepError";

    /// <summary>
    /// Вид проблеми «рядки відсіяно фільтром активності» (B66, D-425): один підсумок на таблицю з кількістю рядків.
    /// </summary>
    public const string RowInactive = "RowInactive";

    /// <summary>
    /// Скільки проблем на таблицю зберігає один прохід; решта відсікається. Підсумок <see cref="RowInactive"/> (один на
    /// таблицю) у стелю не входить і не відсікається (B66).
    /// </summary>
    public const int MaxPerTable = 1000;

    /// <summary>
    /// Замінює проблеми таблиць, які прохід прогону <paramref name="calculationRunId"/> перерахував.
    /// </summary>
    /// <param name="calculationRunId">Прогін; його період — ключ проходу.</param>
    /// <param name="evaluatedTableInstanceIds">Екземпляри таблиць, чиї рядки зіставлено з правилами (навіть без проблем).</param>
    /// <param name="issues">Рядки без правила; їхні таблиці входять до <paramref name="evaluatedTableInstanceIds"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки проблем відсічено стелею <see cref="MaxPerTable"/>.</returns>
    /// <remarks>
    /// Виконується в транзакції викликача (разом з перемиканням актуальності прогону): відкат не лишає напівзаміни.
    /// Старі рядки видаляються одразу; нові лише додаються в контекст - їх зберігає викликач своїм <c>SaveChanges</c>.
    /// </remarks>
    public Task<int> ReplaceAsync(
        long calculationRunId,
        IReadOnlyCollection<long> evaluatedTableInstanceIds,
        IReadOnlyCollection<CalculationIssueInput> issues,
        CancellationToken ct);

    /// <summary>
    /// Видаляє поточні проблеми таблиць, у яких немає жодної активної прив'язки методики (AN-160).
    /// </summary>
    /// <param name="documentId">Лише цей документ; <c>null</c> - усі.</param>
    /// <param name="periodKey">Лише цей період; <c>null</c> - усі.</param>
    /// <param name="tableDefIds">Лише ці таблиці шаблону; <c>null</c> - усі; порожній набір - нічого.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки рядків видалено.</returns>
    /// <remarks>
    /// Ідемпотентно й набором; виконується одразу, у транзакції викликача. Рядки таблиць, що мають хоч одну активну
    /// прив'язку, не чіпає.
    /// </remarks>
    public Task<int> DeleteForUnboundTablesAsync(
        long? documentId, int? periodKey, IReadOnlyCollection<int>? tableDefIds, CancellationToken ct);

    /// <summary>Читає поточні проблеми документа за період і копію аркуша — ОДНИМ запитом.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Усі таблиці (видимість застосовує обробник), за таблицею й номером рядка.</returns>
    public Task<IReadOnlyList<CalculationIssueRow>> ListAsync(
        long documentId, int periodKey, long? sheetCopyId, CancellationToken ct);
}

/// <summary>Проблема для запису: рядок екземпляра таблиці без правила.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowNumber">Номер рядка, з 1.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="MethodologyId">Методологія прив'язки; <c>null</c> — невідома.</param>
/// <param name="Kind">Вид проблеми; <c>null</c> — <see cref="ICalculationIssueStore.NoMatchingRule"/>.</param>
/// <param name="StepCode">B66: перший невдалий крок (<see cref="ICalculationIssueStore.StepError"/>).</param>
/// <param name="ErrorCode">B66: код помилки цього кроку.</param>
/// <param name="AffectedCount">B66: невдалих кроків рядка (StepError) або відсіяних рядків таблиці (RowInactive).</param>
public sealed record CalculationIssueInput(
    long TableInstanceId,
    int RowNumber,
    string RowKey,
    int? MethodologyId,
    string? Kind = null,
    string? StepCode = null,
    string? ErrorCode = null,
    int? AffectedCount = null);

/// <summary>Збережена проблема для читання.</summary>
/// <param name="TableDefId">Таблиця шаблону (для видимості).</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="RowNumber">Номер рядка, з 1.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="Kind">Вид проблеми.</param>
/// <param name="MethodologyCode">Код методології (з <c>cfg.Methodology</c>, не мітка версії); <c>null</c> — невідома.</param>
/// <param name="DetectedAt">Коли виявлено (UTC).</param>
/// <param name="MethodologyId">Методологія (не версія); <c>null</c> — невідома. Потрібна причині відхилення.</param>
/// <param name="StepCode">B66: перший невдалий крок (StepError).</param>
/// <param name="ErrorCode">B66: код помилки кроку (StepError).</param>
/// <param name="AffectedCount">B66: невдалих кроків рядка (StepError) або відсіяних рядків таблиці (RowInactive).</param>
/// <param name="CalculationRunId">✎ D-565: прогін, що записав проблему — за ним читається факт «чиї кроки записано».</param>
public sealed record CalculationIssueRow(
    int TableDefId,
    string TableCode,
    long? SheetCopyId,
    int RowNumber,
    string RowKey,
    string Kind,
    string? MethodologyCode,
    DateTime DetectedAt,
    int? MethodologyId = null,
    string? StepCode = null,
    string? ErrorCode = null,
    int? AffectedCount = null,
    long? CalculationRunId = null);
