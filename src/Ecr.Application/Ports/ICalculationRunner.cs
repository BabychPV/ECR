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
}

/// <summary>Прив'язка методології до таблиці документа.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="MethodologyId">Методологія; версію підбирає резолвер за датою.</param>
public sealed record CalculationBindingRef(long TableInstanceId, int MethodologyId);
