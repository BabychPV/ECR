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
}
