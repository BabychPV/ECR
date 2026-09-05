using Microsoft.Extensions.Logging;
using Quartz.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Пере'єднує внутрішній журнал Quartz із журналом поточного хоста.
/// </summary>
/// <remarks>
/// ⚠ Quartz тримає постачальника журналу в <b>статичному</b> полі
/// (<see cref="LogProvider"/>), і при першому зверненні запам'ятовує
/// <c>ILoggerFactory</c> того хоста, який підняли першим. Це нешкідливо в
/// продуктиві, де хост один, і смертельно в тестах: <c>WebApplicationFactory</c>
/// будує хост двічі, перший закриває — і кожне наступне звернення Quartz до
/// журналу падає <c>ObjectDisposedException</c> ще на етапі побудови
/// контейнера. Застосунок при цьому не стартує взагалі, і причина виглядає як
/// «щось із DI».
/// <para>
/// ⛔ Вимкнути журнал Quartz замість цього не можна: саме він повідомляє про
/// misfire — задачу, яка не запустилася вчасно. Мовчазний misfire означає, що
/// нічна перевірка не відбулася, і дізнаються про це не з журналу.
/// </para>
/// </remarks>
public static class QuartzLogging
{
    /// <summary>Прив'язує журнал Quartz до фабрики цього хоста.</summary>
    /// <param name="loggerFactory">Фабрика журналу поточного хоста.</param>
    public static void UseHost(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        LogProvider.SetCurrentLogProvider(new HostLogProvider(loggerFactory));
    }

    /// <summary>Місток <c>Quartz.Logging</c> → <c>Microsoft.Extensions.Logging</c>.</summary>
    private sealed class HostLogProvider(ILoggerFactory factory) : ILogProvider
    {
        /// <inheritdoc />
        public Logger GetLogger(string name)
        {
            var logger = factory.CreateLogger(name);

            return (level, message, exception, parameters) =>
            {
                var mapped = Map(level);

                if (!logger.IsEnabled(mapped))
                {
                    return false;
                }

                if (message is null)
                {
                    // Quartz питає «чи ввімкнено цей рівень» викликом без
                    // повідомлення. Відповідь «так» тут і є вся робота.
                    return true;
                }

                // ⚠ Повідомлення Quartz уже СФОРМОВАНЕ: воно приходить сюди
                // рядком, а не шаблоном. Тому його передаємо як єдиний
                // параметр сталого шаблону — інакше фігурні дужки у чужому
                // тексті провайдер журналу спробував би підставити як поля.
                LogQuartz(logger, mapped, exception, message(), parameters);

                return true;
            };
        }

        /// <inheritdoc />
        /// <remarks>
        /// Контексти Quartz не переносяться: у нас наскрізний контекст несе
        /// <c>CorrelationId</c>, і другий механізм із власними іменами лише
        /// подвоював би поля в журналі.
        /// </remarks>
        public IDisposable OpenNestedContext(string message) => NullScope.Instance;

        /// <inheritdoc />
        public IDisposable OpenMappedContext(string key, object value, bool destructure = false)
            => NullScope.Instance;

        private static Microsoft.Extensions.Logging.LogLevel Map(Quartz.Logging.LogLevel level) => level switch
        {
            Quartz.Logging.LogLevel.Trace => Microsoft.Extensions.Logging.LogLevel.Trace,
            Quartz.Logging.LogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
            Quartz.Logging.LogLevel.Info => Microsoft.Extensions.Logging.LogLevel.Information,
            Quartz.Logging.LogLevel.Warn => Microsoft.Extensions.Logging.LogLevel.Warning,
            Quartz.Logging.LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            Quartz.Logging.LogLevel.Fatal => Microsoft.Extensions.Logging.LogLevel.Critical,
            _ => Microsoft.Extensions.Logging.LogLevel.Debug,
        };
    }

    /// <summary>Пише готовий рядок Quartz під сталим шаблоном.</summary>
    /// <remarks>
    /// ⚠ Шаблон сталий, а текст — значення поля. Це єдиний спосіб віддати
    /// динамічне повідомлення структурованому журналу, не перетворивши чужі
    /// фігурні дужки на «поля», яких ніхто не оголошував (CA2254).
    /// </remarks>
    private static void LogQuartz(
        ILogger logger,
        Microsoft.Extensions.Logging.LogLevel level,
        Exception? exception,
        string message,
        object?[] parameters)
    {
        var text = parameters.Length == 0
            ? message
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, message, parameters);

#pragma warning disable CA1848, CA1873
        // CA1848: [LoggerMessage] вимагає сталого рівня, а рівень тут задає Quartz.
        // CA1873: рівень уже перевірено викликачем (IsEnabled) — обчислення
        //         тексту не відбувається, коли журнал вимкнено.
        logger.Log(level, exception, "Quartz: {Message}", text);
#pragma warning restore CA1848, CA1873
    }

    /// <summary>Область, яка нічого не робить і нічого не тримає.</summary>
    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
            // Порожньо навмисно: області Quartz не переносяться.
        }
    }
}
