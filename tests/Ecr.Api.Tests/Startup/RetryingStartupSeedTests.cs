using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Ecr.Api.Startup;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B166: seed на старті хоста тестів іде через <see cref="IStartupSeedRunner"/> (<see cref="RetryingStartupSeed"/>) —
/// таймаут фікстури (300 с) лише на час сіду, з повтором на перехідних помилках.
/// </summary>
/// <remarks>
/// Мутація (див. коміт): у <c>StartupSequence</c> завжди <c>new SeedRunner(...)</c>, без <c>GetService&lt;IStartupSeedRunner&gt;</c> —
/// червоний <see cref="Старт_хоста_кличе_зареєстрований_IStartupSeedRunner_рівно_раз"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RetryingStartupSeedTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старт_хоста_кличе_зареєстрований_IStartupSeedRunner_рівно_раз()
    {
        var counting = new CountingSeed();
        using var app = new EcrApiFactory(sql) { ExtraServices = s => s.AddSingleton<IStartupSeedRunner>(counting) };
        using var client = app.CreateClient();

        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.True(live.IsSuccessStatusCode, app.ErrorsText);
        Assert.Equal(1, counting.Calls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Типова_фабрика_тестів_реєструє_RetryingStartupSeed()
    {
        using var app = new EcrApiFactory(sql);

        Assert.IsType<RetryingStartupSeed>(app.Services.GetService<IStartupSeedRunner>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Батчі_сіду_мають_300_с_а_після_сіду_контекст_повертається_до_свого_таймауту()
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(137))
            .Options);
        var connection = db.Database.GetDbConnection();

        var seen = new ConcurrentQueue<int>();
        using var observer = new SqlCommandObserver(connection, seen);
        using (DiagnosticListener.AllListeners.Subscribe(observer))
        {
            await new RetryingStartupSeed().RunAsync(db, NullLogger.Instance, CancellationToken.None);
        }

        Assert.True(seen.Count > 1, $"перехоплено батчів: {seen.Count}");
        Assert.All(seen, t => Assert.Equal(RetryingStartupSeed.CommandTimeoutSeconds, t));
        Assert.Equal(137, db.Database.GetCommandTimeout());
    }

    private sealed class CountingSeed : IStartupSeedRunner
    {
        private int _calls;

        public int Calls => _calls;

        public Task RunAsync(EcrDbContext db, ILogger logger, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return new RetryingStartupSeed().RunAsync(db, logger, ct);
        }
    }

    private sealed class SqlCommandObserver(DbConnection connection, ConcurrentQueue<int> seen)
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "SqlClientDiagnosticListener")
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> evt)
        {
            if (evt.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal)
                && evt.Value?.GetType().GetProperty("Command")?.GetValue(evt.Value) is DbCommand cmd
                && ReferenceEquals(cmd.Connection, connection))
            {
                seen.Enqueue(cmd.CommandTimeout);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                _subscriptions.ForEach(s => s.Dispose());
            }
        }
    }
}
