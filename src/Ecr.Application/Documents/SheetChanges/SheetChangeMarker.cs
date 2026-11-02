// src/Ecr.Application/Documents/SheetChanges/SheetChangeMarker.cs
using System.Globalization;

namespace Ecr.Application.Documents.SheetChanges;

/// <summary>
/// Непрозорий маркер опитування змін аркуша: <c>rowversion</c>-межа і момент видачі (секунди Unix), обидва — hex.
/// </summary>
/// <remarks>
/// ⚠ Момент видачі потрібен лише для того, щоб обмежити знизу вікно журналу <c>aud.CellChange</c> (партиції за
/// <c>ChangedAt</c>) при пошуку авторства; змінене визначає САМЕ <c>rowversion</c>. Зіпсований маркер — <c>null</c>
/// (клієнт отримує свіжий маркер, як на першому опитуванні), так само як зіпсований курсор журналу аркуша.
/// </remarks>
public static class SheetChangeMarker
{
    /// <summary>Кодує маркер.</summary>
    public static string Encode(ulong version, DateTime issuedAtUtc)
        => version.ToString("x16", CultureInfo.InvariantCulture) + "-"
           + new DateTimeOffset(DateTime.SpecifyKind(issuedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()
               .ToString("x", CultureInfo.InvariantCulture);

    /// <summary>Декодує; зіпсований чи порожній — <c>null</c>.</summary>
    public static (ulong Version, DateTime IssuedAtUtc)? Decode(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Length > 40)
        {
            return null;
        }

        var dash = marker.IndexOf('-', StringComparison.Ordinal);
        if (dash != 16
            || !ulong.TryParse(marker.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var version)
            || !long.TryParse(marker.AsSpan(17), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 0 or > 253_402_300_799)
        {
            return null;
        }

        return (version, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime);
    }
}
