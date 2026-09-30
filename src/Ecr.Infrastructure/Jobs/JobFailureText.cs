// src/Ecr.Infrastructure/Jobs/JobFailureText.cs
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Що з провалу задачі можна показати людині в <c>/jobs</c>, і чи варто його повторювати.
/// </summary>
/// <remarks>
/// ⛔ V-03 (UX-прохід 2026-09-24): задача перерахунку впала на
/// <c>Violation of PRIMARY KEY constraint 'PK__#BF40…'. Cannot insert duplicate
/// key in object 'dbo.@cells'</c>, і рівно цей текст стояв у <c>/jobs</c> —
/// імена об'єктів бази й значення ключа, тобто те саме, що ФВ-6.11 уже
/// ховає від 500 на HTTP (<c>ExceptionHandlingMiddleware.Map</c>). Текст
/// винятку бази — для журналу, не для екрана; журнал отримує його повністю.
///
/// ⚠ Решта винятків лишається як є — і це свідомо: причина збору («PI Web
/// API відповів відмовою…», <c>QuartzJobFailureMessageTests</c>) написана
/// для оператора і є єдиним, що каже йому, що робити далі.
///
/// ⛔ І друга половина того самого інциденту: порушення обмеження
/// (первинний/унікальний ключ, зовнішній ключ, CHECK, обрізання) повтором не
/// лікується — той самий вхід дає той самий конфлікт. Задача ретраїла його
/// хвилинами (30 + 60 + 120 с), і весь цей час <c>/jobs</c> показував
/// «виконується», хоча результат був відомий із першої спроби.
/// </remarks>
internal static class JobFailureText
{
    /// <summary>Номери помилок SQL Server, що означають порушення обмеження.</summary>
    /// <remarks>
    /// 2627 — PRIMARY KEY / UNIQUE constraint, 2601 — унікальний індекс,
    /// 547 — FOREIGN KEY / CHECK, 2628 і 8152 — обрізання рядка.
    /// </remarks>
    private static readonly HashSet<int> ConstraintViolations = [2627, 2601, 547, 2628, 8152];

    /// <summary>Текст провалу для <c>/jobs</c>.</summary>
    /// <param name="error">Виняток задачі.</param>
    /// <param name="correlationId">Кореляція — за нею запис знаходиться в журналі.</param>
    public static string For(Exception error, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(error);

        return IsDatabaseError(error)
            ? $"A database error interrupted the job. The details are in the server log (correlation {correlationId})."
            : error.Message;
    }

    /// <summary>Порушення обмеження бази — повтор нічого не змінить.</summary>
    public static bool IsConstraintViolation(Exception error)
        => Chain(error).OfType<SqlException>().Any(e => ConstraintViolations.Contains(e.Number));

    private static bool IsDatabaseError(Exception error)
        => Chain(error).Any(e => e is DbException or DbUpdateException);

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
