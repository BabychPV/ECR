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
    /// <summary>Кодує маркер; третій сегмент — ревізія стану погодження аркуша (<c>rowversion</c> <c>wf.ApprovalState</c>).</summary>
    /// <remarks>
    /// ✎ LIVE-STATUS: ревізія стану — у маркері, тож КОЖЕН перехід погодження (подання, відхилення, затвердження й крок
    /// маршруту, повернення в роботу, відкликання) змінює маркер, і наступне опитування з ним каже <c>statusChanged</c>.
    /// </remarks>
    public static string Encode(ulong version, DateTime issuedAtUtc, ulong statusRevision)
        => version.ToString("x16", CultureInfo.InvariantCulture) + "-"
           + new DateTimeOffset(DateTime.SpecifyKind(issuedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()
               .ToString("x", CultureInfo.InvariantCulture) + "-"
           + statusRevision.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>
    /// Декодує; зіпсований чи порожній — <c>null</c>. Маркер старого формату (без ревізії стану, клієнт до LIVE-STATUS)
    /// — <c>StatusRevision = null</c>: «чи змінився стан» тоді невідомо, і обробник не будить.
    /// </summary>
    public static (ulong Version, DateTime IssuedAtUtc, ulong? StatusRevision)? Decode(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Length > 64)
        {
            return null;
        }

        var parts = marker.Split('-');
        if (parts.Length is not (2 or 3)
            || parts[0].Length != 16
            || !ulong.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var version)
            || !long.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 0 or > 253_402_300_799)
        {
            return null;
        }

        ulong? statusRevision = null;
        if (parts.Length == 3)
        {
            if (parts[2].Length != 16
                || !ulong.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var revision))
            {
                return null;
            }

            statusRevision = revision;
        }

        return (version, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime, statusRevision);
    }
}
