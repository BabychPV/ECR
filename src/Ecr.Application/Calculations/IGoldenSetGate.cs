// src/Ecr.Application/Calculations/IGoldenSetGate.cs
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Calculations;

/// <summary>
/// Ворота золотого набору поза публікацією (PSS-23): той самий прогін і ті самі вердикти, що й у
/// <see cref="PublishMethodologyHandler"/>, — для перемикання опублікованої версії на рушій <c>Script</c>.
/// </summary>
/// <remarks>
/// ⛔ Перемикання рушія змінює числа всіх періодів, які версія рахує, так само, як публікація; доти воно звіряло лише
/// компіляцію й склад, а золотий набір — ні. Рішення людини 20.11 (§6 п. 9 аудиту PSS): на <c>Script</c> — лише з
/// зеленим золотим набором.
/// </remarks>
public interface IGoldenSetGate
{
    /// <summary>
    /// Проганяє золотий набір версії її ПОТОЧНИМ рушієм і відмовляє, якщо він не зелений. Бібліотека набору не має
    /// (D-556) — для неї ворота відкриті.
    /// </summary>
    /// <param name="methodology">Методологія-власник.</param>
    /// <param name="version">Версія, уже переведена на новий рушій і матеріалізована в поточній транзакції.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// <c>ECR-CALC-0422</c>: <c>switchGoldenSetEmpty</c>, <c>switchGoldenNoExpectations</c>, <c>switchGoldenSetDiverged</c>,
    /// <c>goldenTestNoPeriod</c>.
    /// </exception>
    public Task RequireGreenForEngineSwitchAsync(Methodology methodology, MethodologyVersion version, CancellationToken ct);
}
