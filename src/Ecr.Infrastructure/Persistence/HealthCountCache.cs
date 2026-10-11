// src/Ecr.Infrastructure/Persistence/HealthCountCache.cs


using Ecr.Application.Ports;

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
// SemaphoreSlim без AvailableWaitHandle не тримає некерованих ресурсів — Dispose не потрібен.
#pragma warning disable CA1001
public sealed class HealthCountCache
{
    /// <summary>Скільки живе значення.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private sealed record Entry(DateTime At, int Value);

    private Entry? _entry;

    /// <summary>Скільки живе знімок усіх фактів здоров'я (а не лише <c>sources.failed</c>).</summary>
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);

    private sealed record SnapshotEntry(DateTime At, SystemHealthSnapshot Value);

    private readonly SemaphoreSlim _snapshotGate = new(1, 1);

    private SnapshotEntry? _snapshot;

    /// <summary>
    /// Знімок фактів здоров'я: свіжий із кешу, інакше обчислений ОДНИМ викликачем (решта чекає
    /// на замку й бере його результат — «холодний» запит не множиться). Знімок не залежить від
    /// користувача: право <c>System.ViewHealth</c> перевіряє обробник ДО читання. Виняток
    /// обчислення за наявності старого знімка повертає старий (краще застарілі числа, ніж 500);
    /// скасування викликача проходить як є.
    /// </summary>
    /// <param name="utcNow">Поточний момент (UTC).</param>
    /// <param name="compute">Обчислення на промах.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Знімок.</returns>
    public async Task<SystemHealthSnapshot> GetOrComputeSnapshotAsync(
        DateTime utcNow,
        Func<CancellationToken, Task<SystemHealthSnapshot>> compute,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compute);

        static bool Fresh(SnapshotEntry? e, DateTime now)
            => e is not null && now >= e.At && now - e.At < SnapshotTtl;

        var hit = Volatile.Read(ref _snapshot);
        if (Fresh(hit, utcNow))
        {
            return hit!.Value;
        }

        await _snapshotGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            hit = Volatile.Read(ref _snapshot);
            if (Fresh(hit, utcNow))
            {
                return hit!.Value;
            }

            try
            {
                var value = await compute(ct).ConfigureAwait(false);
                Volatile.Write(ref _snapshot, new SnapshotEntry(utcNow, value));

                return value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && hit is not null)
            {
                return hit.Value;
            }
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

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
    public void Invalidate()
    {
        Volatile.Write(ref _entry, null);
        Volatile.Write(ref _snapshot, null);
    }
}
#pragma warning restore CA1001
