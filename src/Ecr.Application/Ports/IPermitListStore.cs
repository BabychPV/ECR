// src/Ecr.Application/Ports/IPermitListStore.cs
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Стовпець опублікованого типу дозволу, що бере значення зі списку (Permits, PM-08e).
/// </summary>
/// <param name="TypeId">Тип.</param>
/// <param name="TypeCode">Код типу.</param>
/// <param name="TypeName">Назва типу.</param>
/// <param name="HeaderRegistryDefId">Реєстр шапки типу (для заборони доступу до типу).</param>
/// <param name="TableCode">Код таблиці типу.</param>
/// <param name="TableName">Назва таблиці.</param>
/// <param name="FieldCode">Код стовпця.</param>
/// <param name="FieldName">Назва стовпця.</param>
/// <param name="Scope">Область списку.</param>
/// <param name="ListRegistryDefId">Реєстр списку (<c>SharedRegistry</c>, <c>TypeOnly</c>); інакше <c>null</c>.</param>
/// <param name="ListRegistryCode">Код цього реєстру.</param>
/// <param name="ListRegistryName">Назва цього реєстру.</param>
/// <param name="SiblingTableCode">Код сестринської таблиці (<c>SamePermitTable</c>).</param>
public sealed record PermitListUsage(
    int TypeId,
    string TypeCode,
    LocalizedText TypeName,
    int HeaderRegistryDefId,
    string TableCode,
    LocalizedText TableName,
    string FieldCode,
    LocalizedText FieldName,
    PermitListScope Scope,
    int? ListRegistryDefId,
    string? ListRegistryCode,
    LocalizedText? ListRegistryName,
    string? SiblingTableCode);

/// <summary>
/// Читання списків вибору дозволів проєкцією (Permits, PM-08e): без <c>DraftJson</c>, без вибірки визначень по одному.
/// </summary>
/// <remarks>
/// ⚠ Окремий порт від <see cref="IPermitTypeStore"/>: у тому живе <c>ListAsync</c>, який тягне чернетки, і його правка
/// належить іншій лінії. Тут лише нові запити.
/// </remarks>
public interface IPermitListStore
{
    /// <summary>
    /// Усі стовпці ОПУБЛІКОВАНИХ типів зі списком: одним запитом, джойном надбудови полів із таблицями типів і реєстрами.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitListUsage>> ListUsagesAsync(CancellationToken ct);

    /// <summary>
    /// Скільки чинних (активних, не видалених) записів у кожному з переданих реєстрів; реєстр без записів у словнику
    /// відсутній. Один запит на весь набір.
    /// </summary>
    /// <param name="registryDefIds">Реєстри списків.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<int, int>> CountActiveEntriesAsync(IReadOnlyCollection<int> registryDefIds, CancellationToken ct);
}
