// src/Ecr.Infrastructure/Persistence/SqlStallClassifier.cs
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Розпізнає «зупинку» бази — клієнтський таймаут команди (<c>SqlException -2</c>) і жертву дедлока
/// (<c>1205</c>) — у ланцюгу чи дереві винятку.
/// </summary>
/// <remarks>
/// ⛔ Чому окремо від <see cref="LockWaitGuard"/>. Той охоронець навмисно ловить лише 1222 (спрацював
/// <c>SET LOCK_TIMEOUT</c> запису комірок): -2 там — «будь-який повільний запит», і під 409 його ховати не
/// можна. Тут питання інше: чи варто ЗАДАЧІ перерахунку піти в відкладення замість <c>JobRetryPolicy</c>
/// (3 ретраї 30/60/120 с). Зупинка запису в БД (автоприріст файлу, довга чужа транзакція) минає сама, а
/// ретраї задачі лишали по собі <c>Failed</c>-прогони (15.10, три прогони EcrLandUi). Стеля відкладень —
/// <c>JobDeferral.MaxDeferral</c>: систематична проблема після неї знову стає <c>Failed</c> із причиною.
/// <para>
/// ⚠ Дерево, а не лише <c>InnerException</c>: паралельні гілки оркестратора віддають
/// <see cref="AggregateException"/>, EF — <c>DbUpdateException</c> і <c>RetryLimitExceededException</c>.
/// </para>
/// </remarks>
public static class SqlStallClassifier
{
    /// <summary>Клієнтський Execution Timeout (CommandTimeout вичерпано).</summary>
    public const int ClientTimeoutNumber = -2;

    /// <summary>Вибрано жертвою дедлока.</summary>
    public const int DeadlockVictimNumber = 1205;

    /// <summary>Глибина обходу дерева винятків.</summary>
    private const int MaxDepth = 32;

    /// <summary>Чи <see cref="SqlException"/> — таймаут команди або дедлок.</summary>
    /// <param name="ex">Виняток SqlClient.</param>
    public static bool IsStall(SqlException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Number is ClientTimeoutNumber or DeadlockVictimNumber;
    }

    /// <summary>Знаходить таймаут команди/дедлок будь-де в дереві винятку; <c>null</c> — немає.</summary>
    /// <param name="exception">Виняток задачі.</param>
    public static SqlException? Find(Exception? exception) => Find(exception, depth: 0);

    /// <summary>Чи в дереві винятку є таймаут команди або дедлок.</summary>
    /// <param name="exception">Виняток задачі.</param>
    public static bool IsStall(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Find(exception) is not null;
    }

    private static SqlException? Find(Exception? current, int depth)
    {
        if (current is null || depth > MaxDepth)
        {
            return null;
        }

        if (current is SqlException sql && IsStall(sql))
        {
            return sql;
        }

        if (current is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (Find(inner, depth + 1) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        return Find(current.InnerException, depth + 1);
    }
}
