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

    /// <summary>Кодує позицію пари «момент + ідентифікатор» — кластерного ключа <c>(ChangedAt, Id)</c> журналу.</summary>
    /// <param name="at">Момент <c>ChangedAt</c> останнього прочитаного рядка (UTC).</param>
    /// <param name="id">Його ідентифікатор.</param>
    /// <remarks>
    /// ⚠ P1-07: журнал змін комірок сортується за кластерним ключем <c>(ChangedAt, Id)</c>, тож курсор мусить нести
    /// ОБИДВА складники: за одного <c>ChangedAt</c> (пакет пишеться одним моментом) лише <c>Id</c> розрізняє рядки,
    /// а лише <c>ChangedAt</c> загубив би або задублював решту пакета на межі сторінки. Формат відрізняється від
    /// <see cref="Encode(long)"/> роздільником, тож старий курсор (лише <c>Id</c>) нову форму не імітує.
    /// </remarks>
    public static string Encode(DateTime at, long id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            at.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Декодує пару «момент + ідентифікатор»; зіпсований або старий (лише <c>Id</c>) курсор — це початок.</summary>
    /// <param name="cursor">Значення від клієнта.</param>
    /// <returns>Позиція або <c>null</c> — читати з початку.</returns>
    public static (DateTime At, long Id)? DecodeAt(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[64];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(buffer[..written]);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0
            || !long.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return (new DateTime(ticks, DateTimeKind.Utc), id);
    }
}
