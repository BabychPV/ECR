// src/Ecr.Application/Permits/PermitLifecycle.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>Стани дозволу в контракті (рядки, як у решти перелічень API).</summary>
public static class PermitStatuses
{
    /// <summary>Чернетка: запис шапки вимкнений (<c>IsActive = 0</c>); аркуші дозвіл не читають.</summary>
    public const string Draft = "Draft";

    /// <summary>Активований і ще не закінчився на дату.</summary>
    public const string Active = "Active";

    /// <summary>Активований, але <c>ValidTo</c> або <c>Actual end</c> уже настали.</summary>
    public const string Expired = "Expired";
}

/// <summary>
/// Життєвий цикл дозволу (FEATURE-PERMITS §1c): <c>Draft → Active → Expired</c>, де перші два стани зберігаються
/// (<c>IsActive</c> запису шапки), а <c>Expired</c> ПОХІДНИЙ від дат - окремого прапорця немає.
/// </summary>
/// <remarks>
/// ⚠ Межі - півінтервал: <c>ValidTo</c> і <c>Actual end</c> - перший НЕчинний день, тож у цей день дозвіл уже <c>Expired</c>.
/// Дозвіл, чинний у майбутньому (<c>ValidFrom &gt; asOf</c>), лишається <c>Active</c>: «ще не почався» окремого стану не має.
/// </remarks>
public static class PermitLifecycle
{
    /// <summary>Стан на дату.</summary>
    /// <param name="isActive">Запис шапки активований.</param>
    /// <param name="validTo">Перший нечинний день.</param>
    /// <param name="actualEnd">Фактична дата закінчення.</param>
    /// <param name="asOf">Дата, на яку питають.</param>
    public static string StatusOf(bool isActive, DateOnly? validTo, DateOnly? actualEnd, DateOnly asOf)
    {
        if (!isActive)
        {
            return PermitStatuses.Draft;
        }

        return EndOf(validTo, actualEnd) is { } end && end <= asOf ? PermitStatuses.Expired : PermitStatuses.Active;
    }

    /// <summary>Днів до закінчення; лише для <c>Active</c> із відомою датою закінчення.</summary>
    /// <param name="status">Стан на дату.</param>
    /// <param name="validTo">Перший нечинний день.</param>
    /// <param name="actualEnd">Фактична дата закінчення.</param>
    /// <param name="asOf">Дата, на яку питають.</param>
    public static int? EndsInDays(string status, DateOnly? validTo, DateOnly? actualEnd, DateOnly asOf)
        => status == PermitStatuses.Active && EndOf(validTo, actualEnd) is { } end ? end.DayNumber - asOf.DayNumber : null;

    /// <summary>Чи перетинаються вікна чинності (напівінтервали, порожня межа - без обмеження).</summary>
    /// <param name="a">Перше вікно.</param>
    /// <param name="b">Друге вікно.</param>
    public static bool Overlaps(ValidityWindow a, ValidityWindow b) => Registries.Keys.RegistryKeyService.Overlaps(a, b);

    /// <summary>Раніша з двох дат закінчення.</summary>
    private static DateOnly? EndOf(DateOnly? validTo, DateOnly? actualEnd)
        => (validTo, actualEnd) switch
        {
            (null, null) => null,
            ({ } to, null) => to,
            (null, { } actual) => actual,
            ({ } to, { } actual) => to < actual ? to : actual,
        };
}
