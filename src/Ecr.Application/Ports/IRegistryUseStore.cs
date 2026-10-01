// src/Ecr.Application/Ports/IRegistryUseStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Ребра <c>cfg.RegistryUse</c> формул версії методології (<c>SourceKind = 1</c>, RT-23b,
/// FEATURE-REGISTRY-TABLES §5.8–5.9): які довідники й поля читає кожна формула.
/// </summary>
/// <remarks>
/// ⛔ Ребра — похідні дані публікації: публікація версії переписує їх ЦІЛКОМ. Змінена
/// формула, що більше не читає довідник, не має лишати застарілого ребра — інакше
/// «Де використано» (RT-19) і свіжість результатів (RT-25) показували б залежність,
/// якої у виразах уже немає.
///
/// ⚠ Правила довідника (<c>SourceKind = 2</c>) переписує <see cref="IRegistryStore.ReplaceRuleUsesAsync"/>;
/// формули шаблону (<c>SourceKind = 0</c>) — публікація шаблону (RT-24).
/// </remarks>
public interface IRegistryUseStore
{
    /// <summary>
    /// Переписує ребра версії методології: усі ребра <c>SourceKind = 1</c> з
    /// <c>SourceId = <paramref name="methodologyVersionId"/></c> прибираються,
    /// <paramref name="uses"/> ставляться в чергу вставки.
    /// </summary>
    /// <param name="methodologyVersionId">Версія, що публікується.</param>
    /// <param name="uses">
    /// Нові ребра — лише <see cref="RegistryUse.ForMethodologyFormula"/> цієї версії;
    /// порожньо — версія довідників не читає.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Задача заміни; збереження робить <c>IUnitOfWork</c> публікації.</returns>
    /// <remarks>
    /// ⚠ Зберігає викликач тим самим <c>SaveChanges</c>, що й публікацію версії: видалення
    /// й вставка лягають разом із нею, тож проміжного стану «ребер немає» ніхто не бачить.
    /// </remarks>
    public Task ReplaceMethodologyUsesAsync(
        int methodologyVersionId, IReadOnlyCollection<RegistryUse> uses, CancellationToken ct);
}
