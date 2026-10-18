// src/Ecr.Application/Permits/PermitInput.cs
using Ecr.Application.Permits.Dto;

namespace Ecr.Application.Permits;

/// <summary>Перевірка й переклад полів шапки дозволу у значення реєстру (Permits, PM-04).</summary>
internal static class PermitInput
{
    /// <summary>Найбільша довжина номера.</summary>
    internal const int MaxNumber = 200;

    /// <summary>Найбільша довжина розташування.</summary>
    internal const int MaxLocation = 500;

    /// <summary>Номер без крайніх пробілів.</summary>
    /// <param name="raw">Номер із запиту.</param>
    internal static string Number(string? raw)
    {
        var number = raw?.Trim() ?? string.Empty;
        if (number.Length == 0)
        {
            throw PermitErrors.NumberRequired();
        }

        return number.Length > MaxNumber ? throw PermitErrors.NumberTooLong(MaxNumber) : number;
    }

    /// <summary>Розташування без крайніх пробілів; порожнє - <c>null</c>.</summary>
    /// <param name="raw">Розташування із запиту.</param>
    internal static string? Location(string? raw)
    {
        var location = raw?.Trim();
        if (string.IsNullOrEmpty(location))
        {
            return null;
        }

        return location.Length > MaxLocation ? throw PermitErrors.LocationTooLong(MaxLocation) : location;
    }

    /// <summary>Кінець суворо пізніший за початок (порожнє вікно не чинне ніколи).</summary>
    /// <param name="from">Початок.</param>
    /// <param name="to">Кінець (виключний).</param>
    internal static void Validity(DateOnly from, DateOnly to)
    {
        if (to <= from)
        {
            throw PermitErrors.ValidityOrder(from, to);
        }
    }

    /// <summary>Значення полів шапки за кодами полів реєстру.</summary>
    /// <param name="number">Номер.</param>
    /// <param name="location">Розташування.</param>
    /// <param name="issueDate">Дата видачі.</param>
    /// <param name="actualEnd">Фактична дата закінчення.</param>
    /// <param name="flags">Прапорці звітних форм.</param>
    /// <param name="clearEmpty"><c>true</c> - порожнє очищає поле (зміна); <c>false</c> - порожнє не пишеться (створення).</param>
    internal static Dictionary<string, object?> HeaderValues(
        string number, string? location, DateOnly? issueDate, DateOnly? actualEnd, PermitReportingFlags? flags, bool clearEmpty)
    {
        var values = new Dictionary<string, object?> { ["Permit_Number"] = number };

        void Put(string code, object? value)
        {
            if (value is not null || clearEmpty)
            {
                values[code] = value;
            }
        }

        Put("Location", location);
        Put("Issue_Date", issueDate);
        Put("Actual_End_Date", actualEnd);
        values["Form_300"] = flags?.Form300 ?? false;
        values["Form_301"] = flags?.Form301 ?? false;
        values["Form_302"] = flags?.Form302 ?? false;
        values["TOx"] = flags?.Tox ?? false;
        return values;
    }
}
