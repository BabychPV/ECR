using Ecr.Infrastructure.Caching;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// <c>RD-05</c>: одна побудова на ключ, скільки б не було одночасних
/// викликачів — і жодної закешованої помилки.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Чому тест саме такий.</b> «50 паралельних викликів → одна побудова» —
/// найлегше зробити ХИБНОЗЕЛЕНИМ у цьому проєкті: якщо перший виклик устигне
/// завершитися до того, як стартує решта, «одна побудова» вийде й БЕЗ злиття.
/// Тому тут побудова тримається <see cref="TaskCompletionSource"/> і не може
/// завершитися, поки всі п'ятдесят не стали в чергу: у
/// <c>П_ятдесят_одночасних_викликів_дають_одну_побудову</c> — за побудовою
/// (виклики йдуть одним потоком, доки побудова висить на I/O-подобі), у
/// <c>П_ятдесят_потоків_дають_одну_побудову</c> — ще й через бар'єр потоків.
/// </para>
/// <para>
/// ⚠ <b>Мутація, що валить обидва:</b> у <c>SingleFlight.RunAsync</c> замінити
/// тіло на <c>return build(ct);</c> (тобто прибрати словник польотів) —
/// лічильник побудов стає 50 замість 1. Перевірено; у .NET мутацію треба
/// ПЕРЕЗБИРАТИ, інакше тест іде по старій DLL і лишається зеленим.
/// </para>
/// </remarks>
public sealed class SingleFlightTests
{
    private const int Parallel = 50;

    /// <summary>
    /// П'ятдесят викликачів на холодному ключі — рівно одна побудова.
    /// </summary>
    /// <remarks>
    /// ⛔ Порядок дій несучий і не є стилем: усі 50 <c>RunAsync</c> виконуються
    /// ДО <c>release.SetResult()</c>. Побудова висить на <c>release</c>, тобто
    /// запис у польоті фізично не може зникнути раніше, ніж останній викликач
    /// його знайде. Жодної залежності від планувальника — «одна побудова» тут
    /// не може вийти випадково.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task П_ятдесят_одночасних_викликів_дають_одну_побудову()
    {
        var flight = new SingleFlight<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;

        async Task<int> Build(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            await release.Task.ConfigureAwait(false);
            return 42;
        }

        var tasks = new Task<int>[Parallel];
        for (var i = 0; i < Parallel; i++)
        {
            tasks[i] = flight.RunAsync("k", Build, CancellationToken.None);
        }

        // Санітарна перевірка ПЕРЕД звільненням: побудова ще йде, тобто всі 50
        // справді стали в чергу до її завершення. Без неї тест був би зеленим
        // і в разі, якби кожен виклик устиг завершитися сам по собі.
        Assert.Equal(1, Volatile.Read(ref builds));
        Assert.Equal(1, flight.Pending);
        Assert.All(tasks, t => Assert.False(t.IsCompleted));

        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, Volatile.Read(ref builds));
        Assert.All(results, r => Assert.Equal(42, r));

        // Запис зник: інакше наступний холодний ключ уперся б у сміття, а
        // кинута побудова закешувалася б назавжди (див. тест нижче).
        Assert.Equal(0, flight.Pending);
    }

    /// <summary>
    /// Те саме, але викликачі — справжні потоки, що стартують одночасно.
    /// </summary>
    /// <remarks>
    /// ⚠ Попередній тест доводить злиття детерміновано, але одним потоком.
    /// Цей додає те, чого той не перевіряє: <c>ConcurrentDictionary.GetOrAdd</c>
    /// і <see cref="Lazy{T}"/> під справжньою гонкою.
    /// <para>
    /// ⛔ Попередня редакція стверджувала, що «планувальник на результат не
    /// впливає», і це було НЕПРАВДОЮ — на CI тест упав із
    /// <c>Expected: 1 / Actual: 50</c>, тобто злиття не сталося ЖОДНОГО разу.
    /// Причина не в <see cref="SingleFlight{T}"/>: бар'єр <c>atGate</c>
    /// дочікувався лише того, що всі 50 задач ПОЧАЛИ виконуватися, а
    /// звільнення спрацьовувало, щойно стартувала ПЕРША побудова. На
    /// навантаженому раннері пул вводить потоки поступово: перша побудова
    /// встигала завершитися й прибрати запис зі словника ще до того, як решта
    /// 49 доходили до <c>RunAsync</c> — і кожна будувала своє. Тест міряв
    /// швидкість планувальника CI, а не поведінку коду.
    /// </para>
    /// <para>
    /// ⚠ Тепер звільнення чекає, доки всі 50 ВВІЙДУТЬ у <c>RunAsync</c>
    /// (<c>entered</c>), і доки запис справді один (<c>Pending == 1</c>).
    /// Залишкове вікно назване, а не заметене: потік, витіснений між
    /// <c>Interlocked.Increment</c> і <c>GetOrAdd</c> довше, ніж триває цикл
    /// опитування (≥ 10 мс), збудує вдруге. Це кілька сусідніх інструкцій
    /// проти десятків мілісекунд — на порядки вужче за попереднє «майже
    /// гарантовано впаде під навантаженням».
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task П_ятдесят_потоків_дають_одну_побудову()
    {
        var flight = new SingleFlight<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var atGate = new CountdownEvent(Parallel);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        var entered = 0;

        async Task<int> Build(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            await release.Task.ConfigureAwait(false);
            return 7;
        }

        var tasks = Enumerable.Range(0, Parallel).Select(index => Task.Run(async () =>
        {
            atGate.Signal();
            await gate.Task.ConfigureAwait(false);

            /*
             * ⛔ Розбіг приходу — не завада тесту, а його зміст. Саме
             * нерівномірний прихід викликачів і валив його на CI; на швидкій
             * машині без розбігу всі 50 приходять майже одночасно, і тест
             * зелений незалежно від того, полагоджено бар'єр чи ні. Тобто без
             * цього рядка він не перевіряє ту умову, під якою падав.
             *
             * ⚠ Крок фіксований (0–98 мс), а не випадковий: відмову має бути
             * видно однаково на кожному прогоні, інакше наступний автор
             * побачить «іноді червоне» й піде піднімати межу.
             */
            await Task.Delay(index * 2).ConfigureAwait(false);

            // ⚠ Лічильник ПЕРЕД викликом, а не всередині `Build`: усередину
            // заходить лише переможець гонки, а міряти треба саме тих, хто
            // прийшов і мав злитися з ним.
            Interlocked.Increment(ref entered);

            return await flight.RunAsync("k", Build, CancellationToken.None).ConfigureAwait(false);
        })).ToArray();

        Assert.True(atGate.Wait(TimeSpan.FromSeconds(30)), "не всі потоки дійшли до бар'єра");
        gate.SetResult();

        /*
         * ⛔ Звільняти можна лише тоді, коли ВСІ викликачі вже в `RunAsync` і
         * запис у словнику один. Звільнення «щойно почалася перша побудова»
         * (так було) на повільному раннері випереджало решту викликачів:
         * перша побудова завершувалася, `finally` прибирав запис, і кожен, хто
         * прийшов потім, будував своє — 50 побудов замість однієї.
         */
        for (var waited = 0; Volatile.Read(ref entered) < Parallel; waited++)
        {
            Assert.True(waited < 3_000, "не всі викликачі дійшли до RunAsync — міряти нічого");
            await Task.Delay(10);
        }

        for (var waited = 0; Volatile.Read(ref builds) == 0 || flight.Pending != 1; waited++)
        {
            Assert.True(waited < 3_000, "побудова так і не почалася — міряти нічого");
            await Task.Delay(10);
        }

        // Санітарна перевірка ДО звільнення: 50 викликачів, запис один.
        Assert.Equal(1, Volatile.Read(ref builds));
        Assert.Equal(1, flight.Pending);

        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, Volatile.Read(ref builds));
        Assert.All(results, r => Assert.Equal(7, r));
        Assert.Equal(0, flight.Pending);
    }

    /// <summary>
    /// Кидок не кешується: наступний виклик пробує знову.
    /// </summary>
    /// <remarks>
    /// ⛔ Головна пастка прийому, і вона тиха. <see cref="Lazy{T}"/> у режимі
    /// <c>ExecutionAndPublication</c> кешує ВИНЯТОК, а запис, що лишився в
    /// словнику після невдалої побудови, віддавав би цей самий виняток усім
    /// наступним викликачам ДО ПЕРЕЗАПУСКУ ПРОЦЕСУ. Тобто один таймаут бази
    /// перетворювався б на постійну відмову по цьому ключу, і в логах не було б
    /// нічого, крім повторюваної старої помилки.
    ///
    /// ⚠ Мутація: прибрати <c>finally { _inFlight.TryRemove(...) }</c> — другий
    /// виклик отримує той самий <c>InvalidOperationException</c>, лічильник
    /// лишається 1, тест червоніє на обох твердженнях.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Кинута_побудова_не_кешується_наступний_виклик_пробує_знову()
    {
        var flight = new SingleFlight<int>();
        var attempts = 0;

        Task<int> Build(CancellationToken _)
        {
            var attempt = Interlocked.Increment(ref attempts);

            return attempt == 1
                ? Task.FromException<int>(new InvalidOperationException("база лягла"))
                : Task.FromResult(99);
        }

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flight.RunAsync("k", Build, CancellationToken.None));
        Assert.Equal("база лягла", first.Message);

        // ⛔ Твердження ПРО ПОВЕДІНКУ йде першим, а не про внутрішній стан:
        // під мутацією (зняття лише на успіх) саме тут видно шкоду — другий
        // виклик отримує ТОЙ САМИЙ виняток «база лягла» замість того, щоб
        // спробувати знову. Перевірено: без `finally` тест падає з
        // `InvalidOperationException` рівно на цьому рядку.
        var second = await flight.RunAsync("k", Build, CancellationToken.None);

        Assert.Equal(99, second);
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(0, flight.Pending);
    }

    /// <summary>
    /// Синхронний кидок фабрики — той самий випадок, і теж не липкий.
    /// </summary>
    /// <remarks>
    /// ⚠ Окремий тест, бо шлях інший: виняток, кинутий ДО першого <c>await</c>,
    /// у наївній реалізації летить із самого <c>Lazy.Value</c>, повз усякий
    /// <c>finally</c> у продовженні. Тут його ловить <c>try</c> всередині
    /// <c>async</c>-методу, тому запис знімається так само.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Синхронний_кидок_фабрики_теж_не_лишає_запису()
    {
        var flight = new SingleFlight<int>();
        var attempts = 0;

        Task<int> Build(CancellationToken _)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("одразу");
            }

            return Task.FromResult(5);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => flight.RunAsync("k", Build, CancellationToken.None));

        Assert.Equal(0, flight.Pending);
        Assert.Equal(5, await flight.RunAsync("k", Build, CancellationToken.None));
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    /// <summary>
    /// Кидок дістається ВСІМ, хто чекав цю саму побудову, — і на цьому все.
    /// </summary>
    /// <remarks>
    /// Перевірка того самого інваріанта з другого боку: спільна невдача не
    /// перетворюється на спільну вічну невдачу.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Кидок_дістається_всім_учасникам_польоту()
    {
        var flight = new SingleFlight<int>();
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Build(CancellationToken _)
        {
            await fail.Task.ConfigureAwait(false);
            return 0;
        }

        var tasks = new Task<int>[Parallel];
        for (var i = 0; i < Parallel; i++)
        {
            tasks[i] = flight.RunAsync("k", Build, CancellationToken.None);
        }

        fail.SetException(new InvalidOperationException("спільна невдача"));

        foreach (var task in tasks)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }

        Assert.Equal(0, flight.Pending);
    }

    /// <summary>
    /// Скасування одного з тих, хто ЧЕКАЄ, не валить решту.
    /// </summary>
    /// <remarks>
    /// ⛔ Це і є причина, чому спільна побудова йде з
    /// <see cref="CancellationToken.None"/>, а не з токеном першого викликача.
    /// Інакше аборт одного браузера означав би 500 для всіх, хто стояв у тій
    /// самій черзі й про нього нічого не знає.
    ///
    /// ⚠ Мутація: у <c>RunAsync</c> віддати всім <c>shared.Value</c> без
    /// <c>WaitAsync(ct)</c> — скасований викликач чекатиме результату, і перше
    /// твердження (<c>ThrowsAnyAsync&lt;OperationCanceledException&gt;</c>)
    /// впаде.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Скасування_того_хто_чекає_не_валить_спільну_побудову()
    {
        var flight = new SingleFlight<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        async Task<int> Build(CancellationToken _)
        {
            await release.Task.ConfigureAwait(false);
            return 11;
        }

        var owner = flight.RunAsync("k", Build, CancellationToken.None);
        var waiter = flight.RunAsync("k", Build, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        release.SetResult();

        Assert.Equal(11, await owner);
        Assert.Equal(0, flight.Pending);
    }

    /// <summary>Різні ключі не зливаються між собою.</summary>
    /// <remarks>
    /// Без цього «одна побудова» можна було б отримати найгіршим способом —
    /// віддаючи всім результат чужого ключа.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Різні_ключі_будуються_окремо()
    {
        var flight = new SingleFlight<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Build(string key)
        {
            await release.Task.ConfigureAwait(false);
            return key;
        }

        var a = flight.RunAsync("a", _ => Build("a"), CancellationToken.None);
        var b = flight.RunAsync("b", _ => Build("b"), CancellationToken.None);

        Assert.Equal(2, flight.Pending);
        release.SetResult();

        Assert.Equal("a", await a);
        Assert.Equal("b", await b);
        Assert.Equal(0, flight.Pending);
    }
}
