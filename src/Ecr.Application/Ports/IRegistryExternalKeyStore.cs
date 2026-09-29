// src/Ecr.Application/Ports/IRegistryExternalKeyStore.cs
using Ecr.Application.Common;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Ports;

/// <summary>
/// Зовнішні ідентифікатори записів довідника — <c>dic.RegistryExternalKey</c>
/// (<c>ФВ-8.10</c>, FEATURE-REGISTRY-SYNC S2).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не ще три методи <see cref="IRegistryStore"/>: той реалізують
/// і підміняють десятки тестів, а зв'язок «запис × джерело» — власна сутність зі
/// своєю унікальністю <c>(DataSourceId, ExternalId)</c>. Читає ці зв'язки ще й
/// <c>RegistrySyncJob.LinksAsync</c> — напряму з контексту, як і решту знімка.
/// </remarks>
public interface IRegistryExternalKeyStore
{
    /// <summary>Сторінка зв'язків записів довідника, за зростанням <c>Id</c>.</summary>
    /// <param name="filter">Довідник (обов'язково), запис і джерело (необов'язково).</param>
    /// <param name="page">Розмір сторінки й курсор.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PagedResult<RegistryExternalKeyView>> ListAsync(
        RegistryExternalKeyFilter filter, CursorRequest page, CancellationToken ct);

    /// <summary>Зв'язок за парою «джерело + зовнішній Id»; <c>null</c> — вільна.</summary>
    /// <param name="dataSourceId">Джерело.</param>
    /// <param name="externalId">Ідентифікатор у джерелі.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RegistryExternalKeyView?> FindByExternalIdAsync(
        int dataSourceId, string externalId, CancellationToken ct);

    /// <summary>Зв'язок за <c>Id</c>, з відстеженням; <c>null</c> — немає.</summary>
    /// <param name="id">Ідентифікатор зв'язку.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RegistryExternalKey?> FindAsync(long id, CancellationToken ct);

    /// <summary>Додає й зберігає зв'язок; <c>Id</c> з'являється після виклику.</summary>
    /// <param name="key">Новий зв'язок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// Пару щойно зайняв інший запит (<c>UQ_RegistryExternalKey</c>) — <c>ECR-REG-0409</c>.
    /// </exception>
    public Task AddAsync(RegistryExternalKey key, CancellationToken ct);

    /// <summary>Видаляє й зберігає.</summary>
    /// <param name="key">Зв'язок, прочитаний <see cref="FindAsync"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RemoveAsync(RegistryExternalKey key, CancellationToken ct);
}

/// <summary>Фільтр переліку зв'язків.</summary>
/// <param name="RegistryDefId">Довідник — межа переліку, завжди.</param>
/// <param name="RegistryEntryId">Лише зв'язки цього запису.</param>
/// <param name="DataSourceId">Лише зв'язки цього джерела.</param>
public sealed record RegistryExternalKeyFilter(int RegistryDefId, long? RegistryEntryId, int? DataSourceId);

/// <summary>Зв'язок запису довідника з елементом зовнішнього джерела.</summary>
/// <param name="Id">Ідентифікатор зв'язку.</param>
/// <param name="RegistryEntryId">Запис довідника.</param>
/// <param name="EntryCode">Код запису.</param>
/// <param name="DataSourceId">Джерело (<c>ext.DataSource</c>).</param>
/// <param name="DataSourceCode">Код джерела.</param>
/// <param name="ExternalId">Ідентифікатор у джерелі (WebId/GUID).</param>
/// <param name="ExternalPath">Шлях у джерелі; ставить синк (<c>MarkSynced</c>).</param>
/// <param name="LastSyncedAt">Коли востаннє зіставлено з джерелом (UTC).</param>
public sealed record RegistryExternalKeyView(
    long Id,
    long RegistryEntryId,
    string EntryCode,
    int DataSourceId,
    string DataSourceCode,
    string ExternalId,
    string? ExternalPath,
    DateTime? LastSyncedAt);
