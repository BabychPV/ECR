using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ecr.TestKit;

/// <summary>
/// Сторож попереджень EF, які в продукті лише пишуться в журнал, а в тестах
/// мають ПАДАТИ.
/// </summary>
/// <remarks>
/// ⛔ Знайдено в журналі сервера під час <c>tools/smoke.ps1</c>, коли всі
/// кроки були зелені — тобто жоден тест цього не бачив:
/// <list type="bullet">
/// <item><c>[20601]</c> <see cref="RelationalEventId.BoolWithDefaultWarning"/> —
/// перелік/bool із default constraint, але без sentinel: явно задане CLR-
/// замовчування (0) EF не надсилає, і в базу мовчки лягає замовчування
/// СХЕМИ.</item>
/// </list>
/// ⚠ Двох механізмів два навмисно. <see cref="Apply{TContext}"/> —
/// <c>ConfigureWarnings(Throw)</c> для контекстів фікстури. Але контекст у
/// тестах будують і в півсотні інших місць (кожен клас тестів API, хост
/// <c>WebApplicationFactory</c> із продуктивною конфігурацією), і пропущене
/// місце означало б сторожа з діркою. Тому <see cref="Install"/> підписується
/// на <see cref="DiagnosticListener"/> EF на рівні ПРОЦЕСУ: EF пише туди
/// кожну подію синхронно, і виняток зі спостерігача виходить із самого
/// запиту чи побудови моделі — незалежно від того, хто і як створив контекст.
/// Продуктивна конфігурація не змінюється ні на рядок.
/// </remarks>
public static class EfWarningGuard
{
    private static readonly EventId[] Guarded =
    [
        RelationalEventId.BoolWithDefaultWarning,
    ];

    private static readonly HashSet<string> GuardedNames =
        new(Guarded.Select(e => e.Name!), StringComparer.Ordinal);

    private static int _installed;

    /// <summary>Вмикає сторожа для всього тестового процесу. Ідемпотентний.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        DiagnosticListener.AllListeners.Subscribe(new AllListenersObserver());
    }

    /// <summary>Робить охоронювані попередження винятком для цього контексту.</summary>
    /// <param name="builder">Будівник опцій.</param>
    /// <typeparam name="TContext">Тип контексту.</typeparam>
    /// <returns>Той самий будівник.</returns>
    public static DbContextOptionsBuilder<TContext> Apply<TContext>(DbContextOptionsBuilder<TContext> builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.ConfigureWarnings(w => w.Throw(Guarded));
    }

    private sealed class AllListenersObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == DbLoggerCategory.Name)
            {
                value.Subscribe(new EventObserver(), (name, _, _) => GuardedNames.Contains(name));
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (GuardedNames.Contains(value.Key))
            {
                throw new InvalidOperationException(
                    $"EF попередження '{value.Key}' у тестах — дефект, а не шум " +
                    $"(див. {nameof(EfWarningGuard)}): {value.Value}");
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
