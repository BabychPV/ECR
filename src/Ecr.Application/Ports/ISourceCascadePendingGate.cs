// src/Ecr.Application/Ports/ISourceCascadePendingGate.cs
namespace Ecr.Application.Ports;

/// <summary>
/// F5 (рішення людини «Заборонити, поки йде перерахунок»): чи чекає документ-споживач × період у каскаді від правки
/// документа-джерела (HSE400, B179/<c>D-566</c>) — задача залежних (<c>doc{id}-y{рік}-dependents</c> чи поперіодна
/// <c>doc{id}-p{період}-dependents</c>) у черзі, відкладена чи виконується, і період ще є в її плані.
/// </summary>
/// <remarks>
/// ⛔ Подання й затвердження такого місяця відмовляють: затверджений аркуш каскад не переписує, і затвердження до
/// перерахунку закріпило б застарілі числа. Без живої задачі каскаду документа — <c>false</c> одним індексованим запитом.
/// </remarks>
public interface ISourceCascadePendingGate
{
    /// <summary>Чи період <paramref name="periodKey"/> документа ще має порахувати каскад від зміни джерела.</summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKey">Період аркуша, що подають чи затверджують.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<bool> IsPendingAsync(long documentId, int periodKey, CancellationToken ct);

    /// <summary>
    /// ✎ b11 (огляд координатора 24.11): ЧОМУ період чекає — каскад від зміни джерела чи власна жива задача перерахунку
    /// або формул самого документа (огляд b11-2 (A)); від причини залежить текст відмови. <see cref="CascadePendingReason.None"/>
    /// — не чекає (те саме, що <see cref="IsPendingAsync"/> = <c>false</c>).
    /// </summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKey">Період аркуша, що подають чи затверджують.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<CascadePendingReason> GetPendingReasonAsync(long documentId, int periodKey, CancellationToken ct);
}

/// <summary>Причина, з якої ворота F5 тримають період (<see cref="ISourceCascadePendingGate.GetPendingReasonAsync"/>).</summary>
public enum CascadePendingReason
{
    /// <summary>Не чекає.</summary>
    None = 0,

    /// <summary>Каскад від зміни документа-джерела: каскад залежних цього документа чи ланка джерела (<c>D-567</c>).</summary>
    SourceCascade = 1,

    /// <summary>Власна жива задача перерахунку чи формул самого документа (натискання, <c>-auto</c>, формули, SHEETNAME…).</summary>
    OwnRecalculation = 2,
}
