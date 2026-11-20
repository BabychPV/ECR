// src/Ecr.Application/Calculations/IFormulaLinkRebuilder.cs
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Calculations;

/// <summary>
/// Відновлює похідні від ФОРМУЛ версії зв'язки: ребра між методологіями (<c>calc.MethodologyDependency</c>) і ребра
/// довідників (<c>cfg.RegistryUse</c>).
/// </summary>
/// <remarks>
/// Потрібне перемиканню версії зі скрипта назад на формули (MS, F13a): скрипт при матеріалізації переписує ці зв'язки
/// за СВОЇМ текстом, і після повернення на <c>Formulas</c> вони мають збігатися з тим, що вивела б публікація формул.
/// </remarks>
public interface IFormulaLinkRebuilder
{
    /// <summary>Переписує ребра методологій і довідників версії за її формулами.</summary>
    /// <param name="methodology">Методологія-власник.</param>
    /// <param name="version">Версія з рушієм <c>Formulas</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RebuildAsync(Methodology methodology, MethodologyVersion version, CancellationToken ct);
}
