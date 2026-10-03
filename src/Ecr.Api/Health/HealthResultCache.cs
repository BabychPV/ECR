using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Короткий TTL-кеш результатів важких health-перевірок: анонімний <c>/health/ready</c>
/// не має ходити в сховище на кожну пробу. Кешується будь-який статус, але не довше
/// <see cref="Ttl"/> — стан, що змінився, видно за ≤ 10 с.
/// </summary>
public sealed class HealthResultCache(TimeProvider? time = null, TimeSpan? ttl = null)
{
    /// <summary>TTL за замовчуванням.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, HealthCheckResult Value)> _entries = new();

    /// <summary>
    /// Стеля кількості записів (L1-02): ключі нормалізовані, тож їх стільки,
    /// скільки мов у реєстрі; стеля — другий рубіж на випадок нового ключа з
    /// даних запиту.
    /// </summary>
    public const int MaxEntries = 32;

    /// <summary>Скільки живе запис.</summary>
    public TimeSpan Ttl { get; } = ttl ?? DefaultTtl;

    /// <summary>Кількість записів (для тестів стелі).</summary>
    public int Count => _entries.Count;

    /// <summary>Повертає свіжий запис або обчислює й запам'ятовує новий.</summary>
    public async Task<HealthCheckResult> GetOrAddAsync(string key, Func<CancellationToken, Task<HealthCheckResult>> compute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compute);

        var now = _time.GetUtcNow();
        if (_entries.TryGetValue(key, out var hit) && now - hit.At < Ttl)
        {
            return hit.Value;
        }

        var result = await compute(ct).ConfigureAwait(false);

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
        {
            foreach (var stale in _entries.Where(e => now - e.Value.At >= Ttl).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(stale, out _);
            }

            if (_entries.Count >= MaxEntries)
            {
                _entries.Clear();
            }
        }

        _entries[key] = (now, result);
        return result;
    }
}
