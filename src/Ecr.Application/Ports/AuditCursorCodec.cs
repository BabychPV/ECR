using System.Globalization;
using System.Text;

namespace Ecr.Application.Ports;

/// <summary>
/// Формат курсора журналів аудиту: <c>v2|порядок|ticks|id|userId|ім'я</c> у base64 (ім'я — останнє, може містити <c>|</c>).
/// </summary>
/// <remarks>
/// ⛔ Курсор несе порядок: читач відхиляє курсор чужого порядку (<c>auditCursorSortMismatch</c>), а не читає мовчки.
/// Живе в Application, бо його будує не лише читач (<c>SheetActivityQuery</c> передає читачу позицію після рядка
/// іншого джерела). Зіпсований або старий (лише <c>Id</c> / <c>ticks:id</c>) курсор — <see langword="null"/> (початок).
/// </remarks>
public static class AuditCursorCodec
{
    private const string Prefix = "v2";

    /// <summary>Кодує позицію.</summary>
    /// <param name="sort">Порядок, у якому курсор виданий.</param>
    /// <param name="at">Момент останнього рядка (UTC).</param>
    /// <param name="id">Його <c>Id</c>.</param>
    /// <param name="userId">Автор (0 — немає); потрібен порядкам «за користувачем».</param>
    /// <param name="name">Ключ імені автора; <see langword="null"/> — порожній.</param>
    public static string Encode(AuditSort sort, DateTime at, long id, int userId, string? name)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(
            '|',
            Prefix,
            ((int)sort).ToString(CultureInfo.InvariantCulture),
            at.Ticks.ToString(CultureInfo.InvariantCulture),
            id.ToString(CultureInfo.InvariantCulture),
            userId.ToString(CultureInfo.InvariantCulture),
            name ?? string.Empty)));

    /// <summary>Розбирає курсор; <see langword="null"/> — порожній, зіпсований або старий формат.</summary>
    /// <param name="cursor">Значення від клієнта.</param>
    public static (AuditSort Sort, DateTime At, long Id, int UserId, string Name)? TryDecode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException)
        {
            return null;
        }

        var parts = text.Split('|', 6);
        if (parts.Length != 6
            || parts[0] != Prefix
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sortCode)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            || ticks > DateTime.MaxValue.Ticks
            || !Enum.IsDefined(typeof(AuditSort), sortCode))
        {
            return null;
        }

        return ((AuditSort)sortCode, new DateTime(ticks, DateTimeKind.Utc), id, userId, parts[5]);
    }
}
