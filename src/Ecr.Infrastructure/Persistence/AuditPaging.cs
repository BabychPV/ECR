using System.Data;
using System.Globalization;
using System.Text;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Позиція курсора журналу аудиту: повний ключ порядку, а не лише <c>Id</c>.</summary>
/// <param name="At">Момент <c>ChangedAt</c> останнього рядка сторінки (UTC).</param>
/// <param name="Id">Його <c>Id</c> — завершальний складник будь-якого порядку.</param>
/// <param name="UserId">Автор (0 — система/немає); використовується порядками «За користувачем».</param>
/// <param name="Name">Ключ імені автора (порожній рядок — імені немає); порядки «За користувачем».</param>
internal readonly record struct AuditPosition(DateTime At, long Id, int UserId, string Name);

/// <summary>
/// Курсор журналів аудиту, прив'язаний до порядку: <c>v2|порядок|ticks|id|userId|ім'я</c> у base64.
/// </summary>
/// <remarks>
/// ⛔ Курсор іншого порядку — не «початок списку», а відмова <c>422 auditCursorSortMismatch</c>: сторінка 3
/// порядку А не є сторінкою 3 порядку Б, і мовчазне читання з чужої позиції дало б пропуски й дублі.
/// ⚠ Зіпсований або старий (лише <c>Id</c> / <c>ticks:id</c>) курсор — як і раніше початок списку.
/// </remarks>
internal static class AuditCursor
{
    private const string Prefix = "v2";

    public static string Encode(AuditSort sort, DateTime at, long id, int userId, string? name)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(
            '|',
            Prefix,
            ((int)sort).ToString(CultureInfo.InvariantCulture),
            at.Ticks.ToString(CultureInfo.InvariantCulture),
            id.ToString(CultureInfo.InvariantCulture),
            userId.ToString(CultureInfo.InvariantCulture),
            name ?? string.Empty)));

    public static AuditPosition? Decode(string? cursor, AuditSort expected)
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

        if ((AuditSort)sortCode != expected)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Курсор належить іншому порядку сортування: почніть з першої сторінки.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.auditCursorSortMismatch" });
        }

        return new AuditPosition(new DateTime(ticks, DateTimeKind.Utc), id, userId, parts[5]);
    }
}

/// <summary>
/// Порядок і курсорний предикат сторінок журналів аудиту (один код для всіх вкладок).
/// </summary>
/// <remarks>
/// ⚠ «За часом» іде за кластерним ключем <c>(ChangedAt, Id)</c> таблиці — прямий/зворотний обхід індексу без
/// сортування вікна. «За користувачем» сортує вікно (Top-N sort): ключ — ім'я автора з <c>sec.User</c> (скалярний
/// підзапит по PK, відсутній автор — порожній рядок), далі <c>UserId</c>, момент і <c>Id</c>; вартість — читання
/// вікна (до 92 діб), а не розміру журналу.
/// </remarks>
internal static class AuditPaging
{
    internal static bool ByUser(AuditSort sort) => sort is AuditSort.UserAsc or AuditSort.UserDesc;

    internal static bool Descending(AuditSort sort) => sort is AuditSort.ChangedAtDesc or AuditSort.UserDesc;

    /// <summary>Ключ імені автора для таблиці, де колонка автора називається <paramref name="userColumn"/>.</summary>
    internal static string NameExpr(string userColumn)
        => $"ISNULL((SELECT usr.DisplayName FROM sec.[User] AS usr WHERE usr.Id = {userColumn}), N'')";

    /// <summary>Предикат «строго після позиції» у порядку <paramref name="sort"/>; починається з <c>AND</c>.</summary>
    internal static string Keyset(AuditSort sort, string at, string id, string userColumn, string userKey)
    {
        var cmp = Descending(sort) ? "<" : ">";
        if (!ByUser(sort))
        {
            return $"\n                   AND {at} {cmp}= @afterAt AND ({at} {cmp} @afterAt OR {id} {cmp} @afterId)";
        }

        var name = NameExpr(userColumn);

        return $"\n                   AND ({name} {cmp} @afterName OR ({name} = @afterName AND ({userKey} {cmp} @afterUser"
               + $" OR ({userKey} = @afterUser AND ({at} {cmp} @afterAt OR ({at} = @afterAt AND {id} {cmp} @afterId))))))";
    }

    /// <summary>Додає параметри курсора (<c>@afterAt/@afterId/@afterUser/@afterName</c>) у команду.</summary>
    internal static void AddPosition(SqlCommand command, AuditSort sort, AuditPosition position)
    {
        var moment = command.Parameters.Add("@afterAt", SqlDbType.DateTime2);
        moment.Scale = 3;
        moment.Value = position.At;
        command.Parameters.Add("@afterId", SqlDbType.BigInt).Value = position.Id;

        if (ByUser(sort))
        {
            command.Parameters.Add("@afterUser", SqlDbType.Int).Value = position.UserId;
            command.Parameters.Add("@afterName", SqlDbType.NVarChar, 400).Value = position.Name;
        }
    }

    /// <summary>ORDER BY над самою таблицею (без псевдоніма).</summary>
    internal static string Order(AuditSort sort, string at, string id, string userColumn, string userKey)
    {
        var dir = Descending(sort) ? " DESC" : string.Empty;

        return ByUser(sort)
            ? $"{NameExpr(userColumn)}{dir}, {userKey}{dir}, {at}{dir}, {id}{dir}"
            : $"{at}{dir}, {id}{dir}";
    }

    /// <summary>ORDER BY зовнішнього запиту: рядки сторінки з псевдонімом <paramref name="alias"/> і приєднаним <c>u</c>.</summary>
    internal static string OuterOrder(AuditSort sort, string alias, string at, string id, string userColumn, string userKey)
    {
        var dir = Descending(sort) ? " DESC" : string.Empty;

        return ByUser(sort)
            ? $"ISNULL(u.DisplayName, N''){dir}, {userKey.Replace(userColumn, alias + "." + userColumn, StringComparison.Ordinal)}{dir}, {alias}.{at}{dir}, {alias}.{id}{dir}"
            : $"{alias}.{at}{dir}, {alias}.{id}{dir}";
    }

    /// <summary>Складає WHERE з вікна й курсорного предиката.</summary>
    internal static void AppendKeyset(
        StringBuilder where, AuditSort sort, AuditPosition? after, SqlCommand command,
        string at, string id, string userColumn, string userKey)
    {
        if (after is not { } position)
        {
            return;
        }

        where.Append(Keyset(sort, at, id, userColumn, userKey));
        AddPosition(command, sort, position);
    }
}
