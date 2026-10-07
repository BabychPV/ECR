// src/Ecr.Infrastructure/Persistence/HealthCountCache.cs

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Короткий кеш одного числа фактів здоров'я (<c>sources.failed</c>): запит по
/// <c>itg.CollectionRun</c> дорогий (на мільйоні запусків ~0.3–0.9 с), а число —
/// спільне для всіх, хто має <c>System.ViewHealth</c>, і не залежить від ролі.
/// </summary>
/// <remarks>
/// ⚠ Час бере від викликача (<c>utcNow</c> із <c>IClock</c> обробника), а не зі
/// <c>DateTime.UtcNow</c>, — тест керує ним без годинника-обгортки. Кеш
/// реєструється синглтоном (на хост), НЕ статично: окремі хости й тестові фабрики
/// не діляться значенням. Виняток обчислення нічого не кешує — наступний виклик
/// пробує знову. Двоє одночасних викликів можуть обчислити двічі — це лише зайвий
/// запит, не хибне число.
/// </remarks>
public sealed class HealthCountCache
{
    /// <summary>Скільки живе значення.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private sealed record Entry(DateTime At, int Value);

    private Entry? _entry;

    /// <summary>Свіже значення з кешу або обчислене й запам'ятоване.</summary>
    /// <param name="utcNow">Поточний момент (UTC).</param>
    /// <param name="compute">Обчислення на промах.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Число.</returns>
    public async Task<int> GetOrComputeAsync(DateTime utcNow, Func<CancellationToken, Task<int>> compute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compute);

        var hit = Volatile.Read(ref _entry);
        if (hit is { } e && utcNow >= e.At && utcNow - e.At < Ttl)
        {
            return e.Value;
        }

        var value = await compute(ct).ConfigureAwait(false);
        Volatile.Write(ref _entry, new Entry(utcNow, value));

        return value;
    }

    /// <summary>Скидає значення (тести; після зміни даних, яку треба побачити одразу).</summary>
    public void Invalidate() => Volatile.Write(ref _entry, null);
}
