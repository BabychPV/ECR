// src/Ecr.Application/Permits/Bindings/PermitBindingFacility.cs
namespace Ecr.Application.Permits.Bindings;

/// <summary>
/// Правило «дозвіл за об'єктом і кінцем періоду» (<c>ByFacilityPeriodEnd</c>): де шукати об'єкт документа (Permits, PM-09).
/// </summary>
/// <remarks>
/// ⚠ РОБОЧЕ ПРИПУЩЕННЯ (FEATURE-PERMITS R6, підтвердження замовника — до PM-10). Шляху документ → об'єкт у моделі
/// немає: <c>Document</c> має лише <c>ProjectId</c> і <c>BusinessKey</c>. Тому об'єкт документа - це поле шапки версії
/// шаблону з КОНСТАНТНИМ кодом <see cref="HeaderFieldCode"/> (<c>String</c> або <c>Lookup</c>), а дозвіл знаходиться
/// порівнянням його значення (для <c>Lookup</c> - назви обраного запису) з полем <c>Location</c> шапки дозволу без
/// урахування регістру. Код живе тільки тут: зміна рішення про об'єкт - це правка одного рядка.
/// </remarks>
public static class PermitBindingFacility
{
    /// <summary>Код поля шапки шаблону, що називає об'єкт документа.</summary>
    public const string HeaderFieldCode = "Facility";

    /// <summary>Код поля шапки дозволу, з яким порівнюється об'єкт.</summary>
    public const string PermitLocationFieldCode = "Location";

    /// <summary>Чи збігаються назви об'єкта документа й розташування дозволу (без регістру, з обрізанням пробілів).</summary>
    /// <param name="facility">Об'єкт документа.</param>
    /// <param name="location">Розташування з шапки дозволу.</param>
    public static bool Matches(string? facility, string? location)
        => !string.IsNullOrWhiteSpace(facility)
           && string.Equals(facility.Trim(), location?.Trim(), StringComparison.OrdinalIgnoreCase);
}
