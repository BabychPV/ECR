using Microsoft.Extensions.Logging;

namespace Ecr.TestKit;

/// <summary>Один запис журналу — рівень і вже відформатований текст.</summary>
/// <param name="Level">Рівень запису.</param>
/// <param name="Message">Текст, як його побачить оператор.</param>
public sealed record LogRecord(LogLevel Level, string Message);

/// <summary>
/// Журнал, який запам'ятовує записи.
/// </summary>
/// <remarks>
/// ⛔ Потрібен там, де ЖУРНАЛ і є поведінкою, а не її побічним слідом. Вхід
/// без жодного збігу за групами (`H-21`) нічого не змінює в базі й нічого не
/// повертає користувачеві: єдиний його наслідок — рядок рівня
/// <c>Warning</c>. Перевіряти такий механізм <c>NullLogger</c> означає не
/// перевіряти його зовсім.
///
/// ⚠ Зберігається САМЕ ВІДФОРМАТОВАНИЙ текст: перелік SID має дійти до
/// оператора, а не лишитися структурованим полем, яке шаблон повідомлення
/// забув згадати.
/// </remarks>
/// <typeparam name="T">Категорія журналу.</typeparam>
public sealed class RecordingLogger<T> : ILogger<T>
{
    /// <summary>Усе, що записали.</summary>
    public List<LogRecord> Records { get; } = [];

    /// <summary>Записи заданого рівня.</summary>
    /// <param name="level">Рівень.</param>
    public IReadOnlyList<LogRecord> OfLevel(LogLevel level)
        => [.. Records.Where(r => r.Level == level)];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        Records.Add(new LogRecord(logLevel, formatter(state, exception)));
    }
}
