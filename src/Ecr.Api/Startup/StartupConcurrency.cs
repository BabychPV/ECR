namespace Ecr.Api.Startup;

/// <summary>
/// Допоміжне для послідовності старту (AN-156): одночасне виконання незалежних кроків.
/// </summary>
public static class StartupConcurrency
{
    /// <summary>
    /// Запускає всі кроки одночасно й чекає на всі. Кожен крок мусить мати власний scope/DbContext —
    /// <c>DbContext</c> не потокобезпечний. Помилка будь-якого кроку летить далі після завершення решти
    /// (як і в послідовному варіанті, старт зупиняється), тож жоден крок не лишається «в польоті» позаду
    /// кинутого винятку.
    /// </summary>
    /// <param name="steps">Незалежні кроки.</param>
    public static Task RunAllAsync(params Func<Task>[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return Task.WhenAll(steps.Select(step => Task.Run(step)));
    }
}
