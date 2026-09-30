// src/Ecr.Application/Registries/Rules/IRegistryRuleEngine.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Registries.Rules;

/// <summary>
/// Рушій правил довідника (RT-17a, FEATURE-REGISTRY-TABLES §6, <c>ФВ-8.18</c>) — єдина точка оцінки
/// правил для всіх шляхів запису.
/// </summary>
/// <remarks>
/// ⛔ Оцінка й відмова розділені. <see cref="EvaluateAsync"/> лише ОЦІНЮЄ і ніколи не кидає
/// <c>ECR-REG-4221</c>; перетворити <c>Error</c> на відмову — окремий крок викликача
/// (<see cref="RegistryRuleCheck.ThrowIfErrors"/>). Так ручний запис (upsert, пакет, CSV) і синк
/// довідника бачать ОДНІ Й ТІ САМІ порушення, а розходяться лише в тому, що з ними роблять.
/// </remarks>
public interface IRegistryRuleEngine
{
    /// <summary>
    /// Оцінює правила записаних записів довідника і правила їхніх батьків композиції, що читають
    /// змінений чи видалений дочірній довідник, — без відмови.
    /// </summary>
    /// <param name="definition">Довідник, записи якого змінено.</param>
    /// <param name="changed">Створені чи змінені записи (уже збережені: мають Id).</param>
    /// <param name="removed">Видалені записи: їхні власні правила не оцінюються, правила батьків — так.</param>
    /// <param name="businessDate">
    /// Дата знімка довідників; <c>null</c> — сьогодні (UTC). Запис темпорального довідника, нечинний
    /// на цю дату, оцінюється на дату свого вікна.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Усі порушення ВСІХ рівнів (<c>Info</c>, <c>Warning</c>, <c>Error</c>): код правила, рівень, Id і
    /// код запису, <c>messageKey</c>, параметри. Порожньо — правил немає або всі виконані.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Для кого: ручні шляхи запису (<c>UpsertRegistryEntryHandler</c>, <c>RegistryBatchHandler</c>,
    /// <c>ImportRegistryEntriesHandler</c>) — вони кличуть <see cref="RegistryRuleCheck.ThrowIfErrors"/>
    /// поверх результату; і синк довідника (<c>RegistrySyncJob</c>, зона «Аналізу»), де джерело — правда:
    /// рядок записується завжди, а порушення фіксуються подією журналу.
    /// </para>
    /// <para>
    /// ⛔ НЕ кидає <c>ECR-REG-4221</c> ні на якому рівні. Транзакцією керує той, хто викликає: метод
    /// кличуть ПІСЛЯ <c>SaveChanges</c> у транзакції запису — знімок довідників читає той самий
    /// контекст і бачить стан після запису; метод сам нічого не зберігає й не відкочує.
    /// </para>
    /// </remarks>
    public Task<RegistryRuleCheck> EvaluateAsync(
        RegistryDef definition,
        IReadOnlyCollection<long> changed,
        IReadOnlyCollection<long> removed,
        DateOnly? businessDate,
        CancellationToken ct);
}
