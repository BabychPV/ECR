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

    /// <summary>Скільки проблем на таблицю зберігає один прохід; решта відсікається.</summary>
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
/// <param name="MethodologyCode">Методологія прив'язки; <c>null</c> — невідома.</param>
public sealed record CalculationIssueInput(long TableInstanceId, int RowNumber, string RowKey, string? MethodologyCode);

/// <summary>Збережена проблема для читання.</summary>
/// <param name="TableDefId">Таблиця шаблону (для видимості).</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="RowNumber">Номер рядка, з 1.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="Kind">Вид проблеми.</param>
/// <param name="MethodologyCode">Методологія; <c>null</c> — невідома.</param>
/// <param name="DetectedAt">Коли виявлено (UTC).</param>
public sealed record CalculationIssueRow(
    int TableDefId,
    string TableCode,
    long? SheetCopyId,
    int RowNumber,
    string RowKey,
    string Kind,
    string? MethodologyCode,
    DateTime DetectedAt);
