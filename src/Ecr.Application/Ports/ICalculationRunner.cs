// src/Ecr.Application/Ports/ICalculationRunner.cs

using Ecr.Application.Calculations;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Виконавець прогону розрахунку.
/// </summary>
/// <remarks>
/// ⚠ Порт, а не пряме посилання. Задача перерахунку живе в
/// <c>Ecr.Infrastructure</c>, оркестратор — в <c>Ecr.Calculations</c>, і це
/// **сусідні** проєкти: обидва залежать від <c>Ecr.Application</c> і не бачать
/// один одного. Пряме посилання зв'язало б їх боком і зламало напрям
/// залежностей, який архітектурний тест перевіряє.
/// <para>
/// Та сама причина, що й у <c>ICalculationModule</c>: застосунок називає, що
/// має статися, не знаючи, хто це зробить.
/// </para>
/// </remarks>
public interface ICalculationRunner
{
    /// <summary>Виконує прогін і повертає профіль по модулях.</summary>
    /// <param name="calculationRunId">Прогін, уже створений викликачем.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="bindings">Прив'язки методологій до таблиць документа.</param>
    /// <param name="progress">Канал прогресу для UI.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Профіль — заповнюється завжди (J-1).</returns>
    public Task<ModuleProfile> RunAsync(
        long calculationRunId,
        long documentId,
        PeriodKey periodKey,
        IReadOnlyList<CalculationBindingRef> bindings,
        IJobProgress progress,
        CancellationToken ct);

    /// <summary>
    /// Виконує прогін, читаючи довідники станом на момент прогону (RT-23a, <c>D-158</c>).
    /// </summary>
    /// <param name="calculationRunId">Прогін, уже створений викликачем.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="bindings">Прив'язки методологій до таблиць документа.</param>
    /// <param name="progress">Канал прогресу для UI.</param>
    /// <param name="registryAsOfUtc">
    /// <c>CalculationRun.RegistryAsOfUtc</c>: усі довідники прогону читаються
    /// <c>FOR SYSTEM_TIME AS OF</c> цього моменту (AC-7); <c>null</c> — поточні дані.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Профіль — заповнюється завжди (J-1).</returns>
    /// <remarks>
    /// ⚠ Перевантаження з типовою реалізацією, а не необов'язковий параметр: токен
    /// скасування мусить лишатися останнім (CA1068), а необов'язковий параметр перед ним
    /// неможливий. Типова реалізація відкидає момент — так поводяться реалізації, що
    /// довідників не читають (фейки в тестах), і їхні виклики не змінюються.
    /// </remarks>
    public Task<ModuleProfile> RunAsync(
        long calculationRunId,
        long documentId,
        PeriodKey periodKey,
        IReadOnlyList<CalculationBindingRef> bindings,
        IJobProgress progress,
        DateTime? registryAsOfUtc,
        CancellationToken ct)
        => RunAsync(calculationRunId, documentId, periodKey, bindings, progress, ct);
}

/// <summary>Прив'язка методології до таблиці документа.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="MethodologyId">Методологія; версію підбирає резолвер за датою.</param>
public sealed record CalculationBindingRef(long TableInstanceId, int MethodologyId);
