// src/Ecr.Infrastructure/Persistence/StaleMapQueryHintInterceptor.cs
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Дописує <c>OPTION (MAXDOP n)</c> до запитів карти застарілості (<c>-- StaleMap:*</c>, <see cref="StaleResultsMapCache"/>).
/// </summary>
/// <remarks>
/// ⛔ Чому не в самому SQL: запит <see cref="StaleResultsQuery"/> — сирий SQL, який EF компонує в підзапит
/// (<c>SELECT TOP(@p) … FROM (…) AS e</c>), а <c>OPTION</c> у похідній таблиці недопустимий. Тег <c>TagWith</c> уже стоїть у
/// тексті команди, тож перехоплювач дописує підказку лише тим запитам, що його несуть.
/// <para>
/// ⚠ Навіщо: на стенді 20.11 (Land, 1,2 млн результатів періоду) запит ішов паралельним планом DOP 6 з мінімальним
/// грантом (<c>RequiredMemory</c>) ≈ 81 МБ — це ≈ 13 МБ на потік за ~9 хеш-операторами, хоча фактично використовувалось
/// 3–56 МБ. На машині з малим пулом пам'яті SQL Server (112 МБ) такий запит стояв у черзі <c>RESOURCE_SEMAPHORE</c> понад
/// 60 с і блокував усіх позаду (FIFO). Мінімальний грант росте лінійно з DOP: DOP 2 дає ≈ 25 МБ, DOP 1 ≈ 13 МБ; карта
/// живе в кеші SWR (15 хв), тож повільніший холодний прогін (≈ 1–1,7 с проти 0,5 с) прийнятний.
/// </para>
/// <para>
/// Значення — <c>Documents:StaleMap:MaxDop</c> (типово <see cref="DefaultMaxDop"/>); 0 вимикає підказку.
/// </para>
/// </remarks>
/// <param name="maxDop">Стеля паралелізму; ≤ 0 — не втручатися.</param>
public sealed class StaleMapQueryHintInterceptor(int maxDop) : DbCommandInterceptor
{
    /// <summary>Ключ конфігурації стелі паралелізму.</summary>
    public const string MaxDopKey = "Documents:StaleMap:MaxDop";

    /// <summary>Типова стеля: 2 потоки (див. зауваження до типу).</summary>
    public const int DefaultMaxDop = 2;

    /// <summary>Початок рядка-тегу в тексті команди (<c>TagWith("StaleMap:…")</c>).</summary>
    internal const string TagMarker = "-- StaleMap:";

    /// <summary>Повертає текст із підказкою або той самий, коли втручатися не треба.</summary>
    /// <param name="commandText">Текст команди.</param>
    /// <param name="maxDop">Стеля паралелізму.</param>
    internal static string Apply(string commandText, int maxDop)
    {
        if (maxDop <= 0
            || !commandText.Contains(TagMarker, StringComparison.Ordinal)
            || commandText.Contains("OPTION (", StringComparison.OrdinalIgnoreCase)
            || commandText.Contains("OPTION(", StringComparison.OrdinalIgnoreCase))
        {
            return commandText;
        }

        return commandText.TrimEnd().TrimEnd(';')
               + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\nOPTION (MAXDOP {maxDop})");
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.CommandText = Apply(command.CommandText, maxDop);
        return base.ReaderExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.CommandText = Apply(command.CommandText, maxDop);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
