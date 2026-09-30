// tests/Ecr.Api.Tests/StandaloneQuartz.cs
using Microsoft.Extensions.Logging.Abstractions;
using Quartz.Impl;
using Quartz.Logging;

namespace Ecr.Api.Tests;

/// <summary>
/// Справжній Quartz у пам'яті для тестів, що обходяться без хоста.
/// </summary>
/// <remarks>
/// ⛔ Журнал Quartz — <b>статичний</b> на процес. Кожен тестовий хост
/// (<c>WebApplicationFactory&lt;Program&gt;</c>) під час першого розв'язання
/// <c>ISchedulerFactory</c> прив'язує його до СВОЄЇ <c>LoggerFactory</c>
/// (<c>AddQuartz</c> → <c>LogContext.SetCurrentLogProvider</c>) і, закриваючись,
/// лишає посилання на вже утилізовану фабрику. Наступний
/// <c>StdSchedulerFactory.GetScheduler()</c> у тому ж процесі падає
/// <c>ObjectDisposedException: LoggerFactory</c> у <c>LogProvider.GetLogger</c>.
/// Поодинці такі тести зелені, у повному прогоні — залежно від порядку класів
/// (відтворюється стабільно з <c>xUnit.MaxParallelThreads=1</c>).
/// <para>
/// Тому перед побудовою планувальника журнал Quartz відв'язується на
/// <see cref="NullLoggerFactory"/> — фабрику, яку ніхто не утилізує. А класи,
/// що цим користуються, стоять у колекції <c>SqlServer</c>: там живуть усі
/// хости Api, і колекція йде послідовно, тож жоден хост не може перев'язати
/// журнал посеред тесту.
/// </para>
/// <para>
/// Мутаційний доказ: прибрати виклик <c>LogContext.SetCurrentLogProvider</c>
/// нижче → <c>HealthTests</c> + <c>StoppingInterruptsJobsTests</c> із
/// <c>-- xUnit.MaxParallelThreads=1</c> знову червоні: хост першого утилізує
/// фабрику журналу, другий падає на ній (прогнано).
/// </para>
/// </remarks>
internal static class StandaloneQuartz
{
    /// <summary>Фабрика планувальника з унікальним іменем і одним потоком.</summary>
    /// <param name="namePrefix">Префікс імені інстанса планувальника.</param>
    public static StdSchedulerFactory Factory(string namePrefix)
    {
        LogContext.SetCurrentLogProvider(NullLoggerFactory.Instance);

        return new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"{namePrefix}-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
    }
}
