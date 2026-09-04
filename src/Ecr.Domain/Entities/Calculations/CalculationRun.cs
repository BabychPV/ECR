// src/Ecr.Domain/Entities/Calculations/CalculationRun.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Прогін розрахунку. Одиниця відтворюваності: результат завжди належить
/// конкретному прогону, а не «поточному стану» (ФВ-9.11).
/// </summary>
/// <remarks>
/// ⚠ Окремого поля «режим» тут **немає**, і це навмисно: три режими `ФВ-12.1`
/// виводяться з двох nullable-полів, тому їх неможливо виставити суперечливо.
/// <list type="bullet">
///   <item><see cref="PeriodKey"/> = <c>null</c> → **плановий повний** (річний);</item>
///   <item><see cref="PeriodKey"/> задано → **інкрементний** за подією;</item>
///   <item><see cref="TriggeredByUserId"/> задано → **ручний**, інакше за розкладом.</item>
/// </list>
/// Окреме поле режиму дозволило б запис «повний прогін одного періоду», який
/// нічого не означає.
/// </remarks>
public sealed class CalculationRun : Entity<long>
{
    private CalculationRun() { }

    public CalculationRun(int projectId, int? periodKey, int? triggeredByUserId, DateTime utcNow)
    {
        ProjectId = projectId;
        PeriodKey = periodKey;
        TriggeredByUserId = triggeredByUserId;
        StartedAt = utcNow;
        Status = "Running";
    }

    public int ProjectId { get; private set; }

    /// <summary><c>null</c> — повний рік; інакше конкретний період.</summary>
    public int? PeriodKey { get; private set; }

    /// <summary><c>null</c> — запуск за розкладом, не людиною.</summary>
    public int? TriggeredByUserId { get; private set; }

    /// <summary>Рядок, а не enum — за DDL (`nvarchar(32)`).</summary>
    public string Status { get; private set; } = null!;

    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }

    /// <summary>Текст помилки при <c>Failed</c>; порожній при успіху.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Профіль по модулях: скільки тривав кожен. Це не діагностика заради
    /// діагностики — саме з нього видно, звідки брати різницю між 20 і 10
    /// хвилинами річного перерахунку (`J-1`, ПРД-13).
    /// </summary>
    public string? ModulesProfileJson { get; private set; }

    public void Complete(string status, DateTime utcNow, string? profileJson, string? errorMessage)
        => throw new NotImplementedException(
            "TODO: зафіксувати статус ('Succeeded' | 'Failed'), час завершення, " +
            "профіль по модулях і текст помилки. " +
            "Перемикання IsCurrent на результатах — ОКРЕМА транзакція в use-case " +
            "(ФВ-9.11), тут його немає: сутність не бачить інших прогонів.");
}
