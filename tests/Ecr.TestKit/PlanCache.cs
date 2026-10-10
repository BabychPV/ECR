using Microsoft.Data.SqlClient;

namespace Ecr.TestKit;

/// <summary>
/// Плани з кешу планів SQL Server — лише плани ЦІЄЇ бази (<c>Z8-01</c>).
/// </summary>
/// <remarks>
/// ⛔ Кеш планів загальносерверний, а <c>sys.dm_exec_sql_text</c> і
/// <c>sys.dm_exec_query_plan</c> для чужого плану відкривають базу, якій він
/// належить. Коли інший прогін на тому самому інстансі (другий worktree, шард
/// <c>ECR_TEST_SHARD</c>, сусідня тестова збірка) переводить свою базу в
/// <c>SINGLE_USER</c>, видаляє чи створює її, запит падає з Msg 924 «Database
/// … is already open and can only have one user» / «is in transition», хоча до
/// нашої бази не має стосунку. Фільтр за <c>dbid</c> у тому самому
/// <c>WHERE</c> не рятує: порядок обчислення <c>CROSS APPLY</c> і
/// <c>WHERE</c> не гарантований. Це вже ловили й виправили в
/// <c>WritePathPlanCacheTests.PlanCountAsync</c> (<c>204689a96</c>); тут той
/// самий прийом для запитів, яким потрібен сам план, а не кількість.
///
/// Тому відбір іде кроками через табличні змінні: спершу <c>plan_handle</c>
/// своєї бази з <c>dm_exec_cached_plans</c> + <c>dm_exec_plan_attributes</c>
/// (атрибути не відкривають базу), далі статистика лише цих планів, і тільки
/// над ними <c>dm_exec_sql_text</c>/<c>dm_exec_query_plan</c>.
///
/// ⚠ <c>dbid</c> — з атрибутів плану: для параметризованих команд
/// (<c>sp_executesql</c>) <c>dm_exec_sql_text.dbid</c> порожній.
///
/// Сторож <c>PlanCacheForeignDatabaseTests</c> в <c>Ecr.Architecture.Tests</c>
/// не пускає в <c>tests/</c> <c>APPLY</c> цих функцій над нефільтрованим кешем.
/// </remarks>
public static class PlanCache
{
    /// <summary>
    /// XML-плани запитів, текст яких містить <paramref name="marker"/>, від
    /// останнього виконаного до найдавнішого; лише з планів цієї бази.
    /// </summary>
    /// <param name="connectionString">З'єднання з тестовою базою (<c>DB_ID()</c> — вона).</param>
    /// <param name="marker">Мітка в тексті запиту; шукається як точний підрядок.</param>
    public static async Task<IReadOnlyList<string>> PlansAsync(string connectionString, string marker)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET NOCOUNT ON;
            DECLARE @own TABLE (plan_handle varbinary(64) PRIMARY KEY);
            INSERT @own
            SELECT cp.plan_handle
            FROM sys.dm_exec_cached_plans AS cp
            CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) AS a
            WHERE a.attribute = N'dbid' AND CONVERT(int, a.value) = DB_ID();

            DECLARE @stats TABLE (sql_handle varbinary(64), plan_handle varbinary(64), last_execution_time datetime);
            INSERT @stats
            SELECT qs.sql_handle, qs.plan_handle, qs.last_execution_time
            FROM sys.dm_exec_query_stats AS qs
            JOIN @own AS o ON o.plan_handle = qs.plan_handle;

            SELECT CONVERT(nvarchar(max), qp.query_plan)
            FROM @stats AS s
            CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) AS st
            CROSS APPLY sys.dm_exec_query_plan(s.plan_handle) AS qp
            WHERE CHARINDEX(@marker, st.text) > 0
              AND st.text NOT LIKE N'%dm_exec_query_stats%'
            ORDER BY s.last_execution_time DESC;
            """;
        command.Parameters.Add("@marker", System.Data.SqlDbType.NVarChar, 4000).Value = marker;

        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                plans.Add(reader.GetString(0));
            }
        }

        return plans;
    }

    /// <summary>Останній виконаний план запиту з міткою <paramref name="marker"/>; <c>null</c>, якщо в кеші цієї бази його немає.</summary>
    /// <param name="connectionString">З'єднання з тестовою базою.</param>
    /// <param name="marker">Мітка в тексті запиту.</param>
    public static async Task<string?> LatestPlanAsync(string connectionString, string marker)
    {
        var plans = await PlansAsync(connectionString, marker).ConfigureAwait(false);
        return plans.Count == 0 ? null : plans[0];
    }
}
