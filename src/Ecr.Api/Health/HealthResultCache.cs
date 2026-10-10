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

    /// <summary>Обчислення, що вже йдуть, — для single-flight (P1-03).</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<HealthCheckResult>>> _inflight = new();

    /// <summary>
    /// Стеля кількості записів (L1-02): ключі нормалізовані, тож їх стільки,
    /// скільки мов у реєстрі, помножених на кешовані перевірки (P1-03: jobs, sources,
    /// db, worker); стеля — другий рубіж на випадок нового ключа з
    /// даних запиту.
    /// </summary>
    public const int MaxEntries = 32;

    /// <summary>Скільки живе запис.</summary>
    public TimeSpan Ttl { get; } = ttl ?? DefaultTtl;

    /// <summary>Кількість записів (для тестів стелі).</summary>
    public int Count => _entries.Count;

    /// <summary>Повертає свіжий запис або обчислює й запам'ятовує новий.</summary>
    /// <remarks>
    /// ⛔ P1-03 (AUDIT-2026-10-09b): single-flight. Без нього після спливу TTL кожна
    /// одночасна проба <c>/health/ready</c> обчислювала перевірку сама (stampede): сто
    /// анонімних проб — сто прогонів важких запитів і сто з'єднань із пулу. Тепер
    /// одночасні промахи за одним ключем чекають ОДНОГО обчислення.
    ///
    /// ⚠ Обчислення веде перший («ведучий») виклик і на СВОЄМУ токені та у своєму
    /// DI-scope: <c>compute</c> замикає scoped-служби (DbContext) того, хто прийшов
    /// першим. Ведучий чекає свого обчислення без <c>WaitAsync</c>, тож його scope живе
    /// до кінця обчислення. Решта чекають з <c>WaitAsync(ct)</c> — скасування проби
    /// не зачіпає чужого обчислення. Якщо ж ведучого скасували (його запит обірвався)
    /// або його scope уже звільнено, решта не отримують чужого скасування, а
    /// обчислюють самі. Справжній збій (база недоступна) ділиться з усіма, а не
    /// множиться повторами.
    /// </remarks>
    public async Task<HealthCheckResult> GetOrAddAsync(string key, Func<CancellationToken, Task<HealthCheckResult>> compute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compute);

        var now = _time.GetUtcNow();
        if (_entries.TryGetValue(key, out var hit) && now - hit.At < Ttl)
        {
            return hit.Value;
        }

        var mine = new Lazy<Task<HealthCheckResult>>(
            () => compute(ct), LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = _inflight.GetOrAdd(key, mine);
        var leader = ReferenceEquals(shared, mine);

        try
        {
            if (leader)
            {
                var result = await shared.Value.ConfigureAwait(false);

                // ⚠ Запис — ДО зняття з `_inflight`: інакше проба, що прийшла між ними,
                // не застала б ні запису, ні обчислення й рахувала б удруге.
                //
                // ⛔ R2-06: мітка — момент ЗАВЕРШЕННЯ обчислення, а не його початку (`now` вище). Довга перевірка
                // (база відповідає повільно) з початковою міткою застарівала б ще до запису: результат, який
                // рахували довше за TTL, жив би «мінус скільки рахували» і кожна наступна проба рахувала б знову —
                // рівно той шторм, який кеш і single-flight мали прибрати.
                Store(key, _time.GetUtcNow(), result);
                return result;
            }

            try
            {
                return await shared.Value.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Скасували ведучого, а не цю пробу: рахуємо самі, у своєму scope.
                return await compute(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (!ct.IsCancellationRequested)
            {
                // Scope ведучого звільнено посеред обчислення: те саме — рахуємо самі.
                return await compute(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (leader)
            {
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<HealthCheckResult>>>(key, mine));
            }
        }
    }

    private void Store(string key, DateTimeOffset now, HealthCheckResult result)
    {
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
    }
}
