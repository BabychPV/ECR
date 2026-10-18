// src/Ecr.Application/Permits/Bindings/PermitSelection.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Дозвіл-кандидат: шапка з розташуванням і вікном чинності.</summary>
/// <param name="EntryId">Запис-шапка.</param>
/// <param name="Number">Номер дозволу.</param>
/// <param name="Location">Розташування (поле <c>Location</c> шапки).</param>
/// <param name="ValidFrom">Перший чинний день; <c>null</c> - від початку.</param>
/// <param name="ValidTo">Перший НЕчинний день (виключно); <c>null</c> - без обмеження.</param>
/// <param name="IsActive">Чи активний (чернетка - неактивний).</param>
/// <param name="IsDeleted">Чи видалений логічно.</param>
public sealed record PermitCandidate(
    long EntryId, string? Number, string? Location, DateOnly? ValidFrom, DateOnly? ValidTo, bool IsActive, bool IsDeleted)
{
    /// <summary>Живий і чинний на дату: півінтервал <c>[from, to)</c> - та сама умова, що в пікері довідників.</summary>
    /// <param name="date">Дата.</param>
    public bool IsLiveOn(DateOnly date)
        => IsActive && !IsDeleted && new ValidityWindow(ValidFrom, ValidTo).Contains(date);
}

/// <summary>Чиста частина вибору дозволу за об'єктом (Permits, PM-09).</summary>
public static class PermitSelection
{
    /// <summary>
    /// Дозвіл об'єкта, чинний на кінець періоду: розташування збігається з назвою об'єкта (без регістру), дозвіл
    /// активний, не видалений і чинний на дату; з кількох - з найпізнішим <c>ValidFrom</c> (порожній - найраніший), а за
    /// рівності - з найбільшим <c>Id</c>.
    /// </summary>
    /// <param name="candidates">Усі дозволи типу.</param>
    /// <param name="facilityNames">Назви об'єкта документа; збіг за будь-якою.</param>
    /// <param name="periodEnd">Останній день періоду.</param>
    /// <returns>Дозвіл або <c>null</c>: запасного «найближчого» немає навмисно.</returns>
    public static PermitCandidate? ChooseByFacility(
        IEnumerable<PermitCandidate> candidates, IReadOnlyCollection<string> facilityNames, DateOnly periodEnd)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(facilityNames);

        return candidates
            .Where(c => c.IsLiveOn(periodEnd) && facilityNames.Any(name => PermitBindingFacility.Matches(name, c.Location)))
            .OrderByDescending(c => c.ValidFrom ?? DateOnly.MinValue)
            .ThenByDescending(c => c.EntryId)
            .FirstOrDefault();
    }
}
