using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Курсор пагінації: непрозорий для клієнта рядок замість offset.
/// </summary>
/// <remarks>
/// ⚠ Offset на великих таблицях означає, що сторінка 500 коштує читання
/// 500×limit рядків, і — гірше — що вставка під час гортання зсуває межі:
/// частина записів показується двічі, частина не показується взагалі.
/// Курсор за первинним ключем не має ні того, ні іншого.
///
/// Base64 тут **не захист**, а сигнал: значення непрозоре, покладатися на
/// його вміст не можна. Клієнт має віддати те, що отримав, а не конструювати
/// своє.
/// </remarks>
public static class Cursor
{
    /// <summary>Кодує позицію.</summary>
    /// <param name="id">Останній прочитаний ідентифікатор.</param>
    public static string Encode(long id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Декодує позицію; зіпсований курсор — це початок, а не помилка.</summary>
    /// <param name="cursor">Значення від клієнта.</param>
    /// <remarks>
    /// Падати на зіпсованому курсорі означало б, що збережене посилання
    /// перестає працювати назавжди. Початок списку — гірша, але робоча
    /// відповідь.
    /// </remarks>
    public static long Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        Span<byte> buffer = stackalloc byte[64];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return 0;
        }

        return long.TryParse(
            Encoding.UTF8.GetString(buffer[..written]),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var id)
            ? id
            : 0;
    }
}
