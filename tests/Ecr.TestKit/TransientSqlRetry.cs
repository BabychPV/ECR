using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Ecr.TestKit;

/// <summary>
/// Повтор операції тестової інфраструктури на ПЕРЕХІДНИХ помилках SQL Server — з обмеженою кількістю спроб,
/// експоненційною паузою з джитером і загальною стелею часу.
/// </summary>
/// <remarks>
/// ⛔ B166. Під навантаженням машини фікстура падала, не дійшовши до жодного тесту: «ALTER DATABASE failed because a lock
/// could not be placed on database EcrTest_…» на перемиканні RCSI (16/16 тестів червоні за ~86 мс), «Could not obtain
/// exclusive lock on database 'model'» на CREATE DATABASE, тайм-аут сіду на старті хоста тестів. Жоден асерт не впав —
/// це середовище, а не продукт, тож повтор ТУТ (у тестовій інфраструктурі), а не в продуктивному коді.
///
/// ⚠ Повторюються ЛИШЕ названі номери (<see cref="DdlLockErrors"/>, <see cref="SeedErrors"/>); будь-яка інша помилка летить
/// одразу, без жодної паузи, — інакше справжній дефект схеми ховався б за хвилиною повторів. Супутні повідомлення
/// (<see cref="Companions"/>: 1802 за 1807, 5069 за 5061) самі повтору не дають — лише разом із перехідною причиною.
/// </remarks>
public static class TransientSqlRetry
{
    /// <summary>
    /// Перехідні помилки DDL рівня бази: 5061 (замок на базу не встановлено), 1807 (немає ексклюзивного замка на
    /// <c>model</c>), 5069 (ALTER DATABASE не виконано), 1222 (тайм-аут замка), 1205 (жертва взаємоблокування).
    /// </summary>
    public static readonly IReadOnlySet<int> DdlLockErrors = new HashSet<int> { 5061, 1807, 5069, 1222, 1205 };

    /// <summary>Сід на старті хоста тестів: перехідні помилки DDL плюс тайм-аут команди клієнта (<c>-2</c>).</summary>
    public static readonly IReadOnlySet<int> SeedErrors = new HashSet<int>(DdlLockErrors) { -2 };

    /// <summary>
    /// Супутні повідомлення, що йдуть у тому самому винятку за перехідною причиною: 1802 («CREATE DATABASE failed») за
    /// 1807, 5069 за 5061. Самі по собі повтору не дають (1802 без 1807 — це, наприклад, вичерпаний диск).
    /// </summary>
    public static readonly IReadOnlySet<int> Companions = new HashSet<int> { 1802, 5069 };

    /// <summary>DDL фікстури: до 6 спроб, пауза 1 → 2 → 4 → 8 → 16 с (±25 %), загалом не довше 60 с.</summary>
    public static readonly RetryPolicy Ddl = new(
        MaxAttempts: 6, BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(16), Budget: TimeSpan.FromSeconds(60));

    /// <summary>
    /// Чи перехідна помилка з такими номерами: є хоч одна з <paramref name="retryable"/>, і ВСІ решта — з
    /// <paramref name="retryable"/> або <see cref="Companions"/>.
    /// </summary>
    /// <param name="errorNumbers">Номери помилок одного винятку.</param>
    /// <param name="retryable">Номери, що дають повтор.</param>
    /// <returns><c>true</c> — повторювати.</returns>
    public static bool IsTransient(IEnumerable<int> errorNumbers, IReadOnlySet<int> retryable)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);
        ArgumentNullException.ThrowIfNull(retryable);

        var cause = false;
        foreach (var number in errorNumbers)
        {
            if (retryable.Contains(number))
            {
                cause = true;
            }
            else if (!Companions.Contains(number))
            {
                return false;
            }
        }

        return cause;
    }

    /// <summary>Класифікатор <see cref="SqlException"/> за номерами її помилок (лише клас &gt; 10, тобто помилки, не PRINT).</summary>
    /// <param name="retryable">Номери, що дають повтор.</param>
    /// <returns>Класифікатор для <see cref="RunAsync"/>.</returns>
    public static Func<Exception, bool> SqlClassifier(IReadOnlySet<int> retryable)
    {
        ArgumentNullException.ThrowIfNull(retryable);
        return ex => ex is SqlException sql && IsTransient(ErrorNumbers(sql), retryable);
    }

    /// <summary>Номери помилок винятку; порожній перелік — номер самого винятку.</summary>
    /// <param name="exception">Виняток SqlClient.</param>
    /// <returns>Номери помилок.</returns>
    public static IReadOnlyList<int> ErrorNumbers(SqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var numbers = exception.Errors.Cast<SqlError>().Where(e => e.Class > 10).Select(e => e.Number).ToList();
        return numbers.Count > 0 ? numbers : [exception.Number];
    }

    /// <summary>
    /// Виконує <paramref name="attempt"/>; на перехідній помилці — пауза й нова спроба, доки не вичерпано
    /// <see cref="RetryPolicy.MaxAttempts"/> чи <see cref="RetryPolicy.Budget"/>.
    /// </summary>
    /// <param name="operation">Назва операції — для повідомлення відмови.</param>
    /// <param name="attempt">Одна спроба (номер спроби з 1).</param>
    /// <param name="isTransient">Класифікатор: <c>true</c> — повторювати.</param>
    /// <param name="policy">Межі повтору.</param>
    /// <param name="delay">Пауза; <c>null</c> — <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. Шов для тестів.</param>
    /// <param name="elapsed">Час від початку; <c>null</c> — <see cref="Stopwatch"/>. Шов для тестів.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="TransientSqlRetryExhaustedException">Перехідна помилка не минула за межі політики.</exception>
    public static async Task RunAsync(
        string operation,
        Func<int, Task> attempt,
        Func<Exception, bool> isTransient,
        RetryPolicy policy,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan>? elapsed = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(isTransient);
        ArgumentNullException.ThrowIfNull(policy);

        delay ??= Task.Delay;
        if (elapsed is null)
        {
            var clock = Stopwatch.StartNew();
            elapsed = () => clock.Elapsed;
        }

        for (var number = 1; ; number++)
        {
            try
            {
                await attempt(number).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (isTransient(ex))
            {
                var pause = policy.DelayBefore(number + 1, RandomNumberGenerator.GetInt32(0, 1001) / 1000d);
                if (number >= policy.MaxAttempts || elapsed() + pause > policy.Budget)
                {
                    throw new TransientSqlRetryExhaustedException(
                        $"{operation}: перехідна помилка SQL не минула за {number} спроб(и) і {elapsed().TotalSeconds:F1} с "
                        + $"(межі: {policy.MaxAttempts} спроб, {policy.Budget.TotalSeconds:F0} с) — останню див. InnerException.",
                        ex);
                }

                await delay(pause, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Межі повтору <see cref="TransientSqlRetry.RunAsync"/>.</summary>
/// <param name="MaxAttempts">Найбільше спроб, включно з першою.</param>
/// <param name="BaseDelay">Пауза перед другою спробою; далі подвоюється.</param>
/// <param name="MaxDelay">Стеля однієї паузи (до джитера).</param>
/// <param name="Budget">Стеля всього часу: пауза, що виходить за неї, не робиться — спроби припиняються.</param>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay, TimeSpan Budget)
{
    /// <summary>Пауза перед спробою <paramref name="attempt"/> (з 2): експонента зі стелею, джитер ±25 %.</summary>
    /// <param name="attempt">Номер наступної спроби.</param>
    /// <param name="jitter">Випадкове число з [0; 1].</param>
    /// <returns>Пауза.</returns>
    public TimeSpan DelayBefore(int attempt, double jitter)
    {
        var exponent = Math.Clamp(attempt - 2, 0, 30);
        var raw = Math.Min(MaxDelay.TotalMilliseconds, BaseDelay.TotalMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(raw * (0.75 + (0.5 * Math.Clamp(jitter, 0d, 1d))));
    }
}

/// <summary>Перехідна помилка SQL не минула в межах <see cref="RetryPolicy"/>; остання — у <see cref="Exception.InnerException"/>.</summary>
public sealed class TransientSqlRetryExhaustedException : Exception
{
    /// <summary>Створює виняток.</summary>
    public TransientSqlRetryExhaustedException()
    {
    }

    /// <summary>Створює виняток.</summary>
    /// <param name="message">Повідомлення.</param>
    public TransientSqlRetryExhaustedException(string message)
        : base(message)
    {
    }

    /// <summary>Створює виняток.</summary>
    /// <param name="message">Повідомлення.</param>
    /// <param name="innerException">Остання перехідна помилка.</param>
    public TransientSqlRetryExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
