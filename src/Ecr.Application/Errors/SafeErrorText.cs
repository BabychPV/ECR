// src/Ecr.Application/Errors/SafeErrorText.cs
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Errors;

/// <summary>
/// Текст помилки, який МОЖНА віддати за межі сервера: клієнту, у поле БД, що читає
/// клієнт, у лист-сповіщення (SEC, TIER2).
/// </summary>
/// <remarks>
/// ⛔ Сирий <c>Exception.Message</c> довільного винятку (рядок підключення з
/// <c>Password=</c>, ім'я сервера чи хоста, URL, шлях файлу, фрагмент SQL) назовні
/// не йде. Назовні — лише текст власних винятків продукту (<see cref="EcrException"/>,
/// <see cref="DomainException"/>: його пише розробник, і він однаково їде клієнтові
/// через <c>ExceptionHandlingMiddleware</c>), а для решти — код каталогу плюс
/// кореляція; повний виняток разом із кореляцією пишеться в журнал сервера.
/// </remarks>
public static class SafeErrorText
{
    /// <summary>Чи текст винятку написаний розробником продукту і безпечний для показу.</summary>
    public static bool IsOwn(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is EcrException or DomainException;
    }

    /// <summary>Код каталогу: власний код помилки, інакше <see cref="ErrorCodes.Internal"/>.</summary>
    public static string CodeOf(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            EcrException e => e.ErrorCode,
            DomainException d => d.ErrorCode,
            InsufficientExecutionStackException => ErrorCodes.ExpressionTooComplex,
            _ => ErrorCodes.Internal,
        };
    }

    /// <summary>Чи є в ланцюгу (дереві) винятку помилка бази даних.</summary>
    /// <remarks>
    /// ⛔ Раніше йшов лише по <c>InnerException</c>: <c>AggregateException</c> (паралельні гілки
    /// оркестратора, <c>Parallel.ForEachAsync</c>) тримає причини в <c>InnerExceptions</c>, а
    /// вичерпані повтори EF (<c>RetryLimitExceededException</c>) і клієнтський таймаут команди
    /// (<c>TimeoutException</c>) не є <see cref="DbException"/> — такі збої БД читались
    /// як «unexpected error». Імена типів, а не посилання: Application не залежить від EF.
    /// </remarks>
    public static bool IsDatabaseError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ContainsDatabaseError(error, depth: 0);
    }

    private static bool ContainsDatabaseError(Exception? current, int depth)
    {
        if (current is null || depth > MaxExceptionTreeDepth)
        {
            return false;
        }

        if (current is DbException or TimeoutException
            || current.GetType().Name is "DbUpdateException" or "RetryLimitExceededException")
        {
            return true;
        }

        if (current is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (ContainsDatabaseError(inner, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        return ContainsDatabaseError(current.InnerException, depth + 1);
    }

    /// <summary>Глибина обходу дерева винятків (захист від патологічних ланцюгів).</summary>
    private const int MaxExceptionTreeDepth = 32;

    /// <summary>Нова кореляція для запису в журнал і в текст помилки.</summary>
    public static string NewCorrelationId()
        => Activity.Current?.TraceId.ToString()
           ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..16];

    /// <summary>
    /// Безпечний текст: власний виняток — його текст як є; решта — код і кореляція.
    /// </summary>
    /// <param name="error">Виняток.</param>
    /// <param name="correlationId">Кореляція, під якою повний виняток лежить у журналі.</param>
    /// <param name="what">Що не вдалося («The collection run», «The job»), англійською.</param>
    public static string For(Exception error, string correlationId, string what)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(correlationId);

        if (IsOwn(error))
        {
            return error.Message;
        }

        return IsDatabaseError(error)
            ? $"A database error interrupted {what}. The details are in the server log (correlation {correlationId})."
            : $"{Capitalized(what)} failed with an unexpected error ({CodeOf(error)}). "
              + $"The details are in the server log (correlation {correlationId}).";
    }

    private static string Capitalized(string what)
        => what.Length == 0 ? what : char.ToUpperInvariant(what[0]) + what[1..];
}
