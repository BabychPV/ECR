// src/Ecr.Application/Ports/IMethodologyVersionDeletionStore.cs
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Ports;

/// <summary>
/// Видалення версії-чернетки методології (<c>BE-25</c>).
/// </summary>
/// <remarks>
/// Окремий порт, а не методи в <see cref="IMethodologyDraftStore"/>: той має тестові
/// підробки, і кожна мусила б реалізовувати дію, якої не торкається. Обидва методи
/// викликаються в ОДНІЙ транзакції: версія читається під блокуванням, і паралельна
/// публікація не проскочить між перевіркою домену й видаленням.
/// </remarks>
public interface IMethodologyVersionDeletionStore
{
    /// <summary>Версія під <c>UPDLOCK</c> і факт, що нею вже рахували.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>null</c>, якщо версії немає.</returns>
    public Task<(MethodologyVersion Version, bool UsedInCalculations)?> LockAsync(
        int methodologyVersionId, CancellationToken ct);

    /// <summary>Видаляє версію разом із усім її вмістом.</summary>
    /// <param name="methodologyVersionId">Версія, яку дозволив домен.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки дочірніх записів видалено (формули, константи, тести…).</returns>
    public Task<int> DeleteAsync(int methodologyVersionId, CancellationToken ct);
}
