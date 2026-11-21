// src/Ecr.Infrastructure/Persistence/StaleMapQueryHintInterceptor.cs
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Дописує <c>OPTION (MAXDOP n)</c> до запитів карти застарілості (<c>-- StaleMap:*</c>, <see cref="StaleResultsMapCache"/>):
/// ВЕРХНЯ МЕЖА паралелізму, що ніколи не піднімає його вище за налаштування сервера.
/// </summary>
/// <remarks>
/// ⛔ Чому не в самому SQL: запит <see cref="StaleResultsQuery"/> — сирий SQL, який EF компонує в підзапит
/// (<c>SELECT TOP(@p) … FROM (…) AS e</c>), а <c>OPTION</c> у похідній таблиці недопустимий. Тег <c>TagWith</c> уже стоїть у
/// тексті команди, тож перехоплювач дописує підказку лише тим запитам, що його несуть (<c>full</c>, <c>authors</c>,
/// <c>delta</c>, <c>direct</c>).
/// <para>
/// ⛔ Підказка запиту <c>MAXDOP</c> ПЕРЕКРИВАЄ і серверний <c>max degree of parallelism</c>, і <c>MAXDOP</c> бази
/// (database scoped) — у бік більшого теж: на сервері з MAXDOP 1 голе <c>OPTION (MAXDOP 4)</c> ПІДНЯЛО б паралелізм.
/// Тому дописується <c>min(налаштоване, межа сервера)</c>, де межа сервера — MAXDOP бази, якщо він &gt; 0, інакше
/// серверний (0 — «без межі»). Межа читається з того самого з'єднання раз на <see cref="ServerDopTtl"/> на сервер і базу;
/// прочитати не вдалося — підказки немає зовсім (не знаючи межі, не піднімаємо).
/// </para>
/// <para>
/// ⚠ Навіщо: на стенді 20.11 (Land, 1,2 млн результатів періоду, пул пам'яті SQL Server 112 МБ) запит ішов паралельним
/// планом DOP 6 з мінімальним грантом (<c>RequiredMemory</c>) ≈ 81 МБ, хоча фактично використовувалось 3–56 МБ, і стояв у
/// черзі <c>RESOURCE_SEMAPHORE</c> понад 60 с, блокуючи всіх позаду (FIFO). Мінімальний грант росте з DOP. Замір на
/// машині з 8 ГБ (EcrLandMsLegacy3, 1,4 млн результатів): DOP 6 ≈ 0,5 с (грант 135 МБ), DOP 2 ≈ 1,1 с (95 МБ),
/// DOP 1 ≈ 2 с (84 МБ). Тому типово — <see cref="DefaultMaxDop"/> = 4 (помірна межа для робочого сервера);
/// стенду з малою пам'яттю SQL Server — 2 (<c>docs/admin/operations-runbook.md</c>). Карта живе в кеші SWR (15 хв).
/// </para>
/// <para>
/// Значення — <c>Documents:StaleMap:MaxDop</c> (типово <see cref="DefaultMaxDop"/>); 0 вимикає підказку.
/// </para>
/// </remarks>
/// <param name="maxDop">Верхня межа паралелізму; ≤ 0 — не втручатися.</param>
public sealed class StaleMapQueryHintInterceptor(int maxDop) : DbCommandInterceptor
{
    /// <summary>Ключ конфігурації верхньої межі паралелізму.</summary>
    public const string MaxDopKey = "Documents:StaleMap:MaxDop";

    /// <summary>Типова верхня межа: 4 потоки (див. зауваження до типу).</summary>
    public const int DefaultMaxDop = 4;

    /// <summary>Початок рядка-тегу в тексті команди (<c>TagWith("StaleMap:…")</c>).</summary>
    internal const string TagMarker = "-- StaleMap:";

    /// <summary>Як довго вважати прочитану межу сервера чинною (зміна <c>sp_configure</c> підхоплюється за цей час).</summary>
    internal static readonly TimeSpan ServerDopTtl = TimeSpan.FromMinutes(10);

    /// <summary>Межа сервера «сервер|база» → (межа; <c>null</c> — невідома) і коли прочитано.</summary>
    private static readonly ConcurrentDictionary<string, (ServerDop? Dop, long ReadAt)> ServerDops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Читання межі паралелізму сервера й бази одним запитом.</summary>
    internal const string ServerDopSql = """
        SELECT CAST((SELECT value_in_use FROM sys.configurations WHERE name = N'max degree of parallelism') AS int),
               CAST((SELECT value FROM sys.database_scoped_configurations WHERE name = N'MAXDOP') AS int);
        """;

    /// <summary>Чи текст — запит карти без власного <c>OPTION</c> (кандидат на підказку).</summary>
    /// <param name="commandText">Текст команди.</param>
    internal static bool IsCandidate(string commandText)
        => commandText.Contains(TagMarker, StringComparison.Ordinal)
           && !commandText.Contains("OPTION (", StringComparison.OrdinalIgnoreCase)
           && !commandText.Contains("OPTION(", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Чинна межа: <c>min(налаштоване, межа сервера)</c>; <c>null</c> — підказки не дописувати.
    /// </summary>
    /// <param name="configured">Налаштоване (<see cref="MaxDopKey"/>); ≤ 0 — вимкнено.</param>
    /// <param name="server">Межа сервера й бази; <c>null</c> — невідома (тоді не піднімаємо — підказки немає).</param>
    internal static int? EffectiveMaxDop(int configured, ServerDop? server)
    {
        if (configured <= 0 || server is not { } dop)
        {
            return null;
        }

        // MAXDOP бази (database scoped), якщо заданий, перекриває серверний; 0 — «без межі».
        var cap = dop.Database > 0 ? dop.Database : dop.Server;
        return cap > 0 ? Math.Min(configured, cap) : configured;
    }

    /// <summary>Повертає текст із підказкою <c>MAXDOP <paramref name="effective"/></c> або той самий.</summary>
    /// <param name="commandText">Текст команди.</param>
    /// <param name="effective">Чинна межа (<see cref="EffectiveMaxDop"/>); <c>null</c> чи ≤ 0 — не втручатися.</param>
    internal static string Apply(string commandText, int? effective)
    {
        if (effective is not > 0 || !IsCandidate(commandText))
        {
            return commandText;
        }

        return commandText.TrimEnd().TrimEnd(';')
               + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\nOPTION (MAXDOP {effective.Value})");
    }

    /// <summary>Читає межу паралелізму сервера й бази на відкритому з'єднанні команди.</summary>
    /// <param name="connection">Відкрите з'єднання.</param>
    /// <param name="transaction">Транзакція з'єднання, якщо є (інакше SqlClient відмовить виконати команду).</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Межа; <c>null</c> — не прочитано (немає права, не SQL Server).</returns>
    internal static async Task<ServerDop?> ReadServerDopAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            await using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = ServerDopSql;
            await using var reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ToDop(reader) : null;
        }
        catch (DbException)
        {
            return null;
        }
    }

    private static ServerDop? ReadServerDop(DbConnection connection, DbTransaction? transaction)
    {
        try
        {
            using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = ServerDopSql;
            using var reader = probe.ExecuteReader();
            return reader.Read() ? ToDop(reader) : null;
        }
        catch (DbException)
        {
            return null;
        }
    }

    /// <summary>Серверний не прочитався (каталог не видно) — межа невідома; MAXDOP бази без рядка — «не задано».</summary>
    private static ServerDop? ToDop(DbDataReader reader)
        => reader.IsDBNull(0) ? null : new ServerDop(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1));

    private static string KeyOf(DbConnection connection) => $"{connection.DataSource}|{connection.Database}";

    private static bool TryCached(string key, out ServerDop? dop)
    {
        if (ServerDops.TryGetValue(key, out var cached) && Stopwatch.GetElapsedTime(cached.ReadAt) < ServerDopTtl)
        {
            dop = cached.Dop;
            return true;
        }

        dop = null;
        return false;
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (maxDop > 0 && IsCandidate(command.CommandText) && command.Connection is { } connection)
        {
            var key = KeyOf(connection);
            if (!TryCached(key, out var dop))
            {
                dop = ReadServerDop(connection, command.Transaction);
                ServerDops[key] = (dop, Stopwatch.GetTimestamp());
            }

            command.CommandText = Apply(command.CommandText, EffectiveMaxDop(maxDop, dop));
        }

        return base.ReaderExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (maxDop > 0 && IsCandidate(command.CommandText) && command.Connection is { } connection)
        {
            var key = KeyOf(connection);
            if (!TryCached(key, out var dop))
            {
                dop = await ReadServerDopAsync(connection, command.Transaction, cancellationToken).ConfigureAwait(false);
                ServerDops[key] = (dop, Stopwatch.GetTimestamp());
            }

            command.CommandText = Apply(command.CommandText, EffectiveMaxDop(maxDop, dop));
        }

        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Межа паралелізму без підказки: серверний <c>max degree of parallelism</c> і <c>MAXDOP</c> бази (0 — не задано).</summary>
/// <param name="Server">Серверне значення (<c>sys.configurations</c>); 0 — усі процесори.</param>
/// <param name="Database">Значення бази (<c>sys.database_scoped_configurations</c>); 0 — як на сервері.</param>
internal readonly record struct ServerDop(int Server, int Database);
