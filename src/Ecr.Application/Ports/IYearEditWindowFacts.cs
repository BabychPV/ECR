// src/Ecr.Application/Ports/IYearEditWindowFacts.cs

namespace Ecr.Application.Ports;

/// <summary>
/// ✎ b14 (D-579, огляд H-2): стан «вікна редагування = рік проєкту + зміщення» для річних даних документа — ОДНЕ
/// джерело для правки (<c>AccessDecisionService</c>) і перерахунку (<c>RecalculationJob</c>,
/// <c>RecalculateDocumentHandler</c>), щоб правку в Grace не приймали, а перерахунок тієї самої комірки не відхиляли.
/// </summary>
public interface IYearEditWindowFacts
{
    /// <summary>
    /// Стан вікна року <paramref name="year"/> для документа; <c>null</c> — шаблон документа без вікна, проєкт не
    /// місячний або не активний (тоді діє агрегат місяців, як доти).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="year">Рік екземпляра (не весь проміжок проєкту, огляд L-5).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<Domain.Enums.PeriodState?> StateAsync(long documentId, int year, CancellationToken ct);
}
