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

    /// <summary>Статус прогону, що зараз вважається актуальним (ФВ-9.11).</summary>
    /// <remarks>
    /// ⚠ «Актуальність» живе саме тут, а не на кожному результаті: у схемі
    /// <c>calc.CalculationResult</c> колонки <c>IsCurrent</c> немає, і це
    /// правильно — інакше перемикання означало б оновити десятки мільйонів
    /// рядків замість одного (`calc`-частина `Q-027`).
    /// </remarks>
    public const string CurrentStatus = "Current";

    /// <summary>Прогін завершився успішно, але вже не актуальний.</summary>
    public const string SupersededStatus = "Superseded";

    /// <summary>Чи є цей прогін актуальним джерелом чисел.</summary>
    public bool IsCurrent => string.Equals(Status, CurrentStatus, StringComparison.Ordinal);

    /// <summary>Фіксує завершення прогону.</summary>
    /// <param name="status">"Succeeded" або "Failed".</param>
    /// <param name="utcNow">Час завершення в UTC.</param>
    /// <param name="profileJson">Профіль по модулях; пишеться завжди.</param>
    /// <param name="errorMessage">Текст помилки при провалі.</param>
    /// <remarks>
    /// Перемикання актуальності — ОКРЕМА операція use-case (ФВ-9.11): сутність
    /// не бачить інших прогонів і не може знати, котрий із них зараз чинний.
    /// </remarks>
    public void Complete(string status, DateTime utcNow, string? profileJson, string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Status = status;
        FinishedAt = utcNow;

        // ⚠ Профіль пишеться навіть при провалі: саме провальний прогін
        // найцікавіше розглядати за часом — де він зупинився і що встиг.
        ModulesProfileJson = profileJson;
        ErrorMessage = errorMessage;
    }

    /// <summary>Робить прогін актуальним.</summary>
    /// <exception cref="DomainException">Прогін ще не завершився.</exception>
    public void MakeCurrent()
    {
        // ⛔ Незавершений прогін не може бути джерелом чисел: половина
        // результатів уже є, половини ще немає, і звіт покаже суму, якої не
        // існує в жодному стані системи.
        if (FinishedAt is null)
        {
            throw new DomainException(
                "ECR-CALC-0422", $"Прогін {Id} ще не завершився: актуальним його зробити не можна.");
        }

        Status = CurrentStatus;
    }

    /// <summary>Знімає актуальність — попередній прогін лишається читабельним.</summary>
    public void Supersede() => Status = SupersededStatus;
}
