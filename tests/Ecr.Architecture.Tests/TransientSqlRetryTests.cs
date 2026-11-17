using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B166: повтор DDL фікстури й сіду хоста тестів на ПЕРЕХІДНИХ помилках SQL Server (<see cref="TransientSqlRetry"/>) —
/// повторює лише названі номери, не довше за межі політики, і віддає справжню помилку одразу.
/// </summary>
/// <remarks>
/// <c>SqlException</c> не має публічного конструктора, тож тут помилка — <see cref="FakeSqlError"/> із номерами, а класифікатор
/// — той самий <see cref="TransientSqlRetry.IsTransient"/>, що й у <see cref="TransientSqlRetry.SqlClassifier"/>. Справжню
/// <c>SqlException</c> (1222 від живого замка, 8134) перевіряє <c>TransientSqlRetryRealSqlTests</c> (Infrastructure.Tests).
/// Мутації (див. коміт): <c>Ddl.MaxAttempts = 1</c> — червоний тест повтору; <c>IsTransient</c> → <c>true</c> для всього —
/// червоний тест неперехідної помилки.
/// </remarks>
public sealed class TransientSqlRetryTests
{
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(new[] { 5061 }, true)]
    [InlineData(new[] { 5061, 5069 }, true)]
    [InlineData(new[] { 1807, 1802 }, true)]
    [InlineData(new[] { 5069 }, true)]
    [InlineData(new[] { 1222 }, true)]
    [InlineData(new[] { 1205 }, true)]
    [InlineData(new[] { 1802 }, false)]
    [InlineData(new[] { 5011, 5069 }, false)]
    [InlineData(new[] { 8134 }, false)]
    [InlineData(new[] { 2627 }, false)]
    [InlineData(new[] { -2 }, false)]
    [InlineData(new int[0], false)]
    public void DDL_повторює_лише_названі_номери_а_супутні_лише_разом_із_причиною(int[] numbers, bool transient)
        => Assert.Equal(transient, TransientSqlRetry.IsTransient(numbers, TransientSqlRetry.DdlLockErrors));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сід_хоста_тестів_повторює_ще_й_тайм_аут_клієнта_а_DDL_ні()
    {
        Assert.True(TransientSqlRetry.IsTransient([-2], TransientSqlRetry.SeedErrors));
        Assert.True(TransientSqlRetry.IsTransient([1205], TransientSqlRetry.SeedErrors));
        Assert.False(TransientSqlRetry.IsTransient([-2], TransientSqlRetry.DdlLockErrors));
        Assert.False(TransientSqlRetry.IsTransient([547], TransientSqlRetry.SeedErrors));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public async Task Перехідна_помилка_двічі_третя_спроба_проходить_паузи_експоненційні()
    {
        var run = new FakeRun(failures: 2, new FakeSqlError(5061, 5069));

        await run.ExecuteAsync(TransientSqlRetry.Ddl);

        Assert.Equal(3, run.Attempts);
        Assert.Equal(2, run.Pauses.Count);
        Assert.InRange(run.Pauses[0].TotalMilliseconds, 750, 1250);
        Assert.InRange(run.Pauses[1].TotalMilliseconds, 1500, 2500);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public async Task Замок_на_model_при_CREATE_DATABASE_повторюється()
    {
        var run = new FakeRun(failures: 1, new FakeSqlError(1807, 1802));

        await run.ExecuteAsync(TransientSqlRetry.Ddl);

        Assert.Equal(2, run.Attempts);
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(new[] { 5011, 5069 })]
    [InlineData(new[] { 1802 })]
    [InlineData(new[] { 8134 })]
    public async Task Неперехідна_помилка_летить_одразу_без_повтору_й_без_паузи(int[] numbers)
    {
        var error = new FakeSqlError(numbers);
        var run = new FakeRun(failures: int.MaxValue, error);

        var thrown = await Assert.ThrowsAsync<FakeSqlError>(() => run.ExecuteAsync(TransientSqlRetry.Ddl));

        Assert.Same(error, thrown);
        Assert.Equal(1, run.Attempts);
        Assert.Empty(run.Pauses);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public async Task Стеля_спроб_після_MaxAttempts_відмова_з_контекстом_і_останньою_помилкою()
    {
        var run = new FakeRun(failures: int.MaxValue, new FakeSqlError(5061));

        var thrown = await Assert.ThrowsAsync<TransientSqlRetryExhaustedException>(() => run.ExecuteAsync(TransientSqlRetry.Ddl));

        Assert.Equal(6, TransientSqlRetry.Ddl.MaxAttempts);
        Assert.Equal(TransientSqlRetry.Ddl.MaxAttempts, run.Attempts);
        Assert.Equal(TransientSqlRetry.Ddl.MaxAttempts - 1, run.Pauses.Count);
        Assert.Same(run.Last, thrown.InnerException);
        Assert.Contains("ALTER DATABASE EcrTest_x", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("6 спроб", thrown.Message, StringComparison.Ordinal);

        // Сума пауз (з джитером) — у межах стелі часу політики.
        Assert.True(run.Pauses.Sum(p => p.TotalSeconds) <= TransientSqlRetry.Ddl.Budget.TotalSeconds);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public async Task Стеля_часу_повільні_спроби_зупиняють_повтор_раніше_за_MaxAttempts()
    {
        // Кожна спроба «йде» 20 с: 20 → пауза ~1 → 41 → пауза ~2 → 63 > 60 — третя спроба остання.
        var run = new FakeRun(failures: int.MaxValue, new FakeSqlError(1222)) { AttemptCost = TimeSpan.FromSeconds(20) };

        await Assert.ThrowsAsync<TransientSqlRetryExhaustedException>(() => run.ExecuteAsync(TransientSqlRetry.Ddl));

        Assert.Equal(3, run.Attempts);
        Assert.Equal(2, run.Pauses.Count);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Пауза_подвоюється_до_стелі_і_джитер_у_межах_25_відсотків()
    {
        var policy = TransientSqlRetry.Ddl;

        Assert.Equal(TimeSpan.FromMilliseconds(750), policy.DelayBefore(2, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(1250), policy.DelayBefore(2, 1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayBefore(4, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(16), policy.DelayBefore(6, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(16), policy.DelayBefore(40, 0.5));
    }

    /// <summary>Помилка з номерами SQL Server — замість <c>SqlException</c>, яку не створити.</summary>
    private sealed class FakeSqlError(params int[] numbers) : Exception("fake SQL error " + string.Join(",", numbers))
    {
        public IReadOnlyList<int> Numbers { get; } = numbers;
    }

    /// <summary>Спроби, що падають <c>failures</c> разів; пауза не спить, а пересуває штучний годинник.</summary>
    private sealed class FakeRun(int failures, FakeSqlError error)
    {
        private TimeSpan _now;

        public int Attempts { get; private set; }

        public List<TimeSpan> Pauses { get; } = [];

        public TimeSpan AttemptCost { get; init; }

        public Exception? Last { get; private set; }

        public Task ExecuteAsync(RetryPolicy policy) => TransientSqlRetry.RunAsync(
            "ALTER DATABASE EcrTest_x",
            _ =>
            {
                Attempts++;
                _now += AttemptCost;
                if (Attempts <= failures)
                {
                    Last = error;
                    throw error;
                }

                return Task.CompletedTask;
            },
            ex => ex is FakeSqlError f && TransientSqlRetry.IsTransient(f.Numbers, TransientSqlRetry.DdlLockErrors),
            policy,
            delay: (pause, _) =>
            {
                Pauses.Add(pause);
                _now += pause;
                return Task.CompletedTask;
            },
            elapsed: () => _now);
    }
}
