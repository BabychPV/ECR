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

    /// <summary>
    /// Підказка запиту сторінки: для порядків «за користувачем» — `OPTION (RECOMPILE)`.
    /// </summary>
    /// <remarks>
    /// ⛔ З параметричним `TOP (@take)` рушій не вбачає Top-N сортування й сортує вікно повністю: виміряно ~14,7 с CPU
    /// на 3 млн рядків проти ~1,4 с з літералом `TOP (101)`. `RECOMPILE` вбудовує значення `@take` у план. Для порядків
    /// «за часом» (seek по кластерному ключу) підказка не потрібна, і вони лишаються з кешованим планом.
    /// </remarks>
    internal static string Hint(string outerOrder)
        => outerOrder.Contains("DisplayName", StringComparison.Ordinal) ? "\n OPTION (RECOMPILE)" : string.Empty;

    /// <summary>
    /// Текст `SELECT TOP (@take)` сторінки над таблицею журналу.
    /// </summary>
    /// <remarks>
    /// ⚠ «За часом» — звичайний `TOP` по кластерному ключу (як і було). «За користувачем» сортує ВІКНО, і сортувати
    /// широкі рядки (значення до 1000 знаків, JSON) дорого — виміряно ~20 с CPU на 3 млн рядків проти ~1,4 с для
    /// вузького ключа. Тому внутрішній `TOP` сортує лише ключ `(момент, Id)`, а колонки добираються приєднанням
    /// по первинному ключу — тільки для `@take` рядків.
    /// </remarks>
    internal static string TopPage(
        string columns, string table, string where, string order,
        string at = "ChangedAt", string id = "Id", string userColumn = "ChangedByUserId")
    {
        var name = NameExpr(userColumn);
        if (!order.Contains(name, StringComparison.Ordinal))
        {
            return $"SELECT TOP (@take) {columns}\n  FROM {table}\n WHERE {where}\n ORDER BY {order}";
        }

        // ⚠ Ім'я автора рахується ОДИН раз на рядок (CROSS APPLY), а не в кожному місці курсорного предиката: тричі
        // обчислений скалярний підзапит на 3 млн рядків давав ~22 с CPU на глибокій сторінці.
        return $"SELECT TOP (@take) {columns}\n"
               + $"  FROM {table} AS t\n"
               + $" INNER JOIN (SELECT TOP (@take) i.{at} AS KAt, i.{id} AS KId FROM {table} AS i\n"
               + $"             CROSS APPLY (SELECT ISNULL(MAX(usr.DisplayName), N'') AS SortName FROM sec.[User] AS usr WHERE usr.Id = i.{userColumn}) AS nm\n"
               + $"             WHERE {where.Replace(name, "nm.SortName", StringComparison.Ordinal)}\n"
               + $"             ORDER BY {order.Replace(name, "nm.SortName", StringComparison.Ordinal)}) AS k ON t.{at} = k.KAt AND t.{id} = k.KId\n"
               + $" ORDER BY {order}";
    }
    /// <summary>
    /// Вікно запиту, стиснуте позицією курсора (лише порядки «за часом»).
    /// </summary>
    /// <remarks>
    /// ⛔ Дві нижні (чи верхні) межі в одному WHERE — `ChangedAt &gt;= @from AND ChangedAt &gt;= @afterAt` — рушій не
    /// зводить в одну: seek іде за першою, друга стає залишковим предикатом, і глибока сторінка читала весь журнал від
    /// початку вікна до курсора (виміряно: 17 тис. читань сторінок проти 37 на 3 млн рядків). Тому вікно СТИСКАЄТЬСЯ в
    /// параметрах: у зростанні нижня межа — не раніше за курсор, у спаданні верхня — не пізніше за момент курсора
    /// (+1 мс, бо верхня межа виключна, а курсорний предикат `&lt;=`). Рядки поза стисненим вікном курсор і так відсікав би.
    /// </remarks>
    internal static (DateTime From, DateTime To) Window(AuditSort sort, DateTime from, DateTime to, AuditPosition? after)
    {
        if (after is not { } position || ByUser(sort))
        {
            return (from, to);
        }

        return Descending(sort)
            ? (from, position.At.AddMilliseconds(1) < to ? position.At.AddMilliseconds(1) : to)
            : (position.At > from ? position.At : from, to);
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
