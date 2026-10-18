// src/Ecr.Application/Permits/Bindings/IPermitResolver.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Запит на вибір дозволу для прив'язки.</summary>
/// <param name="PermitTypeDefId">Тип дозволу прив'язки.</param>
/// <param name="Rule">Правило вибору.</param>
/// <param name="PeriodEnd">Останній день періоду документа: на нього перевіряється чинність.</param>
/// <param name="FacilityName">Об'єкт документа (поле шапки <see cref="PermitBindingFacility.HeaderFieldCode"/>) - для <c>ByFacilityPeriodEnd</c>.</param>
/// <param name="HeaderEntryId">Запис шапки дозволу, обраний у полі шапки - для <c>FromHeaderLookup</c>.</param>
public sealed record PermitResolveRequest(
    int PermitTypeDefId, PermitSelectionRule Rule, DateOnly PeriodEnd, string? FacilityName, long? HeaderEntryId);

/// <summary>Результат вибору дозволу.</summary>
/// <param name="EntryId">Запис-шапка дозволу; <c>null</c> - дозволу немає.</param>
/// <param name="Number">Номер дозволу.</param>
/// <param name="ValidFrom">Початок чинності.</param>
/// <param name="ValidTo">Кінець чинності (виключно).</param>
/// <param name="ReasonIfNone">Чому дозволу немає: <c>noFacility</c>, <c>noPermit</c>, <c>headerEmpty</c>, <c>headerUnknown</c>.</param>
/// <param name="Warnings">Попередження: дозвіл із шапки нечинний на кінець періоду тощо.</param>
public sealed record PermitResolution(
    long? EntryId,
    string? Number,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? ReasonIfNone,
    IReadOnlyList<PermitBindingIssue> Warnings);

/// <summary>
/// Вибір дозволу для аркуша за правилом прив'язки (Permits, PM-09; споживачі - перегляд PM-09 і рантайм PM-10).
/// </summary>
/// <remarks>
/// ⚠ Заглушки-запасного дозволу немає: не знайдено - <see cref="PermitResolution.EntryId"/> порожній, а причину
/// каже <see cref="PermitResolution.ReasonIfNone"/>. Підставити «найближчий» дозвіл означало б показати ліміти не того
/// об'єкта в звітній формі.
/// </remarks>
public interface IPermitResolver
{
    /// <summary>Обирає дозвіл.</summary>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PermitResolution> ResolveAsync(PermitResolveRequest request, CancellationToken ct);
}
