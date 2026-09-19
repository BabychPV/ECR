using System.Collections.Concurrent;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Одна побудова на ключ: N одночасних викликачів на холодному ключі дістають
/// результат ОДНІЄЇ побудови, а не запускають N штук.
/// </summary>
/// <typeparam name="T">Що будується (знімок, профіль, список довідника).</typeparam>
/// <remarks>
/// <para>
/// ⛔ Предмет `RD-05`. Класична пара «<c>TryGetValue</c> → будувати →
/// <c>Set</c>» не має між першим і третім кроком НІЧОГО: на холодному ключі
/// сто одночасних запитів дають сто побудов, кожна зі своїми запитами до бази,
/// і лише остання виграє <c>Set</c>. Кеш при цьому «працює» — на другому
/// запиті; ціною першого є рівно той пік, заради згладжування якого кеш і
/// ставили.
/// </para>
/// <para>
/// ⚠ <b>Головна пастка прийому — виняток.</b> <see cref="Lazy{T}"/> у режимі
/// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> кешує ВИНЯТОК
/// фабрики назавжди, а запис у словнику, що лишився після невдалої побудови,
/// віддавав би цей виняток усім наступним викликачам до перезапуску процесу.
/// Тобто одна тимчасова помилка бази перетворилася б на постійну відмову по
/// цьому ключу. Тому запис знімається у <c>finally</c> — і саме на це
/// спрямований окремий тест
/// (<c>Кинута_побудова_не_кешується_наступний_виклик_пробує_знову</c>).
/// </para>
/// <para>
/// ⚠ <b>Скасування.</b> Спільна побудова йде з
/// <see cref="CancellationToken.None"/> навмисно. Токен ПЕРШОГО викликача тут
/// був би спільною долею: аборт одного браузера валив би решту сорока дев'яти
/// запитів, які про нього нічого не знають. Натомість власник (той, чий запис
/// ліг у словник) чекає побудову до кінця — це тримає живим його ж
/// <c>DbContext</c>, на якому побудова і йде, — а решта чекають із ВЛАСНИМ
/// токеном і вільні піти, нічого не ламаючи. Ціна: покинута побудова
/// дочитує свої кілька запитів. Вона обмежена й наповнює кеш, тобто робота не
/// марна.
/// </para>
/// </remarks>
public sealed class SingleFlight<T>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Скільки побудов зараз у польоті.
    /// </summary>
    /// <remarks>
    /// Потрібне тестам: «побудова завершилася» і «запис прибрано» — різні
    /// твердження, і саме друге ламається непомітно.
    /// </remarks>
    public int Pending => _inFlight.Count;

    /// <summary>Виконує побудову рівно один раз на ключ.</summary>
    /// <param name="key">Ключ побудови — той самий, під яким ляже результат.</param>
    /// <param name="build">Побудова; викликається щонайбільше один раз на політ.</param>
    /// <param name="ct">Токен ЦЬОГО викликача, не спільний.</param>
    /// <returns>Результат спільної побудови.</returns>
    public Task<T> RunAsync(string key, Func<CancellationToken, Task<T>> build, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(build);

        // ⚠ `Lazy` створюється ДО `GetOrAdd` і з перевантаженням за значенням,
        // а не за фабрикою: `ConcurrentDictionary` має право викликати фабрику
        // кількох потоків одночасно, і тоді «хто власник» стало б питанням
        // без відповіді. Створення `Lazy` нічого не запускає — побудова
        // починається лише на `.Value`.
        var mine = new Lazy<Task<T>>(
            () => BuildAndReleaseAsync(key, build),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var shared = _inFlight.GetOrAdd(key, mine);

        if (ReferenceEquals(shared, mine))
        {
            // Власник чекає без свого токена: його scope (а з ним і DbContext,
            // на якому йде побудова) мусить дожити до кінця побудови.
            return shared.Value;
        }

        return ct.CanBeCanceled ? shared.Value.WaitAsync(ct) : shared.Value;
    }

    /// <summary>Побудова, після якої запис у польоті зникає — хоч успіх, хоч виняток.</summary>
    private async Task<T> BuildAndReleaseAsync(string key, Func<CancellationToken, Task<T>> build)
    {
        try
        {
            return await build(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // ⚠ Зняття без звірки на тотожність безпечне: новий `Lazy` може
            // лягти під цей ключ лише ПІСЛЯ того, як зник попередній, а
            // попередній зникає саме тут. Викликач, що встиг узяти цей же
            // запис із уже завершеним завданням, свій результат дістане —
            // він тримає посилання, а не ключ.
            _inFlight.TryRemove(key, out _);
        }
    }
}
