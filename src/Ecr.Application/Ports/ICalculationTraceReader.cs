// src/Ecr.Application/Ports/ICalculationTraceReader.cs
namespace Ecr.Application.Ports;

/// <summary>
/// ✎ D-565 (PERF-03): читання записаного трейсу — кроки рядка в прогоні й профілі прогонів (факт «чиї кроки записано»).
/// </summary>
/// <remarks>
/// ⚠ Мінімальне читання для діагностики проблеми рядка <c>StepError</c>: «який крок упав і з якими входами». Повний екран
/// «Чому це число?» (FEATURE-HSE301-VIEW §10.3) — окрема робота; він бере ті самі кроки за адресою кроку.
/// </remarks>
public interface ICalculationTraceReader
{
    /// <summary>Кроки трейсу одного рядка документа в прогоні, за порядком.</summary>
    /// <param name="calculationRunId">Прогін.</param>
    /// <param name="periodKey">Період (партиція кроків).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці рядка; кроки без екземпляра (до D-401) теж беруться.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="max">Стеля кроків; читається на один більше, щоб знати, що обрізано.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Кроки з розпакованим <c>TraceJson</c>; порожньо — кроків немає (трасування вимкнено або рядок без кроків).</returns>
    public Task<IReadOnlyList<CalculationStepRecord>> ReadRowStepsAsync(
        long calculationRunId, int periodKey, long documentId, long tableInstanceId, string rowKey, int max,
        CancellationToken ct);

    /// <summary>Профілі прогонів (<c>ModulesProfileJson</c>) за ідентифікаторами — одним запитом.</summary>
    /// <param name="calculationRunIds">Прогони.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прогін → профіль; прогону, якого немає (прибраний), у словнику немає.</returns>
    public Task<IReadOnlyDictionary<long, string?>> ReadRunProfilesAsync(
        IReadOnlyCollection<long> calculationRunIds, CancellationToken ct);
}

/// <summary>Один записаний крок трейсу.</summary>
/// <param name="Id">Ідентифікатор кроку.</param>
/// <param name="StepOrder">Порядок кроку в рядку.</param>
/// <param name="StepCode">Код кроку (формула або вихід).</param>
/// <param name="Expression">Вираз, як його бачив рушій (обрізаний до 2000).</param>
/// <param name="Value">Значення; <c>null</c> — крок не дав числа.</param>
/// <param name="TraceJson">Деталізація (<c>TraceJson</c> v1 або голий код помилки), розпакована.</param>
/// <param name="Masked">Причина маскування в нуль (<c>H-24d-1</c>); <c>None</c> — немає.</param>
/// <param name="SubstanceEntryId">Речовина кроку.</param>
public sealed record CalculationStepRecord(
    long Id,
    int StepOrder,
    string StepCode,
    string? Expression,
    decimal? Value,
    string? TraceJson,
    Domain.Enums.MaskedZeroReason Masked,
    long? SubstanceEntryId);
