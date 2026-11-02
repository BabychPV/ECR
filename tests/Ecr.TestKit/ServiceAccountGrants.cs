using System.Text;

namespace Ecr.TestKit;

/// <summary>
/// Права облікового запису служби в базі (DB-1) — єдине джерело для тесту найменших прав
/// (<c>ServiceAccountLeastPrivilegeTests</c>) і сторожа розбіжності з runbook
/// (<c>ServiceAccountGrantsRunbookTests</c>, Ecr.Architecture.Tests).
/// </summary>
/// <remarks>
/// ⚠ Скрипти розгортання цих прав НЕ видають (D-66: DDL і права — DBA). Тому перелік тут — це те, що
/// runbook (<c>docs/admin/operations-runbook.md</c>, абзац «Права облікового запису служби») просить
/// видати, і рівно те, під чим тест ганяє реальні шляхи застосунку. Новий табличний тип чи процедура, яку
/// застосунок викликає, без рядка тут і в runbook — червоний сторож.
/// </remarks>
public static class ServiceAccountGrants
{
    /// <summary>Фіксовані ролі бази: читання й запис усіх таблиць (сід виконується на кожному старті).</summary>
    public static readonly IReadOnlyList<string> Roles = ["db_datareader", "db_datawriter"];

    /// <summary>Послідовності (<c>NEXT VALUE FOR</c>, <c>sp_sequence_get_range</c>) — право <c>UPDATE</c>.</summary>
    public static readonly IReadOnlyList<string> Sequences =
        ["doc.TableInstanceSeq", "doc.TableRowSeq", "calc.CalculationResultSeq", "dic.RegistryEntryCodeSeq"];

    /// <summary>Табличні типи (TVP) — право <c>EXECUTE</c>.</summary>
    public static readonly IReadOnlyList<string> TableTypes = ["doc.CellValueTvp", "aud.CellChangeTvp"];

    /// <summary>Процедури (<c>WITH EXECUTE AS OWNER</c>) — право <c>EXECUTE</c>.</summary>
    public static readonly IReadOnlyList<string> Procedures =
        ["arc.usp_ArchiveYear", "arc.usp_EnsureAuditPartitions", "arc.usp_ArchiveAudit"];

    /// <summary>Скалярні функції, які застосунок викликає у власних запитах (не через вʼюху) — право <c>EXECUTE</c>.</summary>
    public static readonly IReadOnlyList<string> Functions = [];

    /// <summary>Скрипт видачі прав користувачу бази <paramref name="principal"/> — без будь-чого понад перелік.</summary>
    /// <param name="principal">Ім'я користувача бази (без дужок).</param>
    /// <param name="except">Об'єкт, право на який пропустити (для доказу «без цього права шлях падає»).</param>
    /// <returns>T-SQL, один пакет.</returns>
    public static string Script(string principal, string? except = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        var who = "[" + principal.Replace("]", "]]", StringComparison.Ordinal) + "]";
        var sql = new StringBuilder();
        foreach (var role in Roles)
        {
            sql.Append("ALTER ROLE ").Append(role).Append(" ADD MEMBER ").Append(who).AppendLine(";");
        }

        foreach (var sequence in Sequences.Where(s => s != except))
        {
            sql.Append("GRANT UPDATE ON OBJECT::").Append(sequence).Append(" TO ").Append(who).AppendLine(";");
        }

        foreach (var type in TableTypes.Where(t => t != except))
        {
            sql.Append("GRANT EXECUTE ON TYPE::").Append(type).Append(" TO ").Append(who).AppendLine(";");
        }

        foreach (var procedure in Procedures.Concat(Functions).Where(p => p != except))
        {
            sql.Append("GRANT EXECUTE ON OBJECT::").Append(procedure).Append(" TO ").Append(who).AppendLine(";");
        }

        return sql.ToString();
    }
}
