using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <see cref="SeedRunner"/> виконує батчі сирими <c>DbCommand</c>, які НЕ
/// успадковують таймаут EF: без явного присвоєння кожен батч мав дефолтні 30 с
/// замість <c>Database:CommandTimeoutSeconds</c> — і під навантаженням падав
/// старт фікстури (а міг би й старт застосунку).
/// </summary>
/// <remarks>
/// Інтерцептор EF сирих команд не бачить, тому команди ловимо на рівні
/// SqlClient (<c>WriteCommandBefore</c>) і фільтруємо за власним з'єднанням —
/// паралельні тести в тому ж процесі не домішуються.
/// </remarks>
[Collection("SqlServer")]
public sealed class SeedRunnerTimeoutTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кожен_батч_сіду_отримує_налаштований_таймаут_а_не_дефолтні_30_с()
    {
        // ⚠ Мутаційний доказ: прибрати `cmd.CommandTimeout = seconds` у
        // SeedRunner → усі батчі мають 30, тест червоний.
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(137))
            .Options);
        var connection = db.Database.GetDbConnection();

        var seen = new ConcurrentQueue<int>();
        using var observer = new SqlCommandObserver(connection, seen);
        using (DiagnosticListener.AllListeners.Subscribe(observer))
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.True(seen.Count > 1, $"перехоплено батчів: {seen.Count}");
        Assert.All(seen, t => Assert.Equal(137, t));
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
            if (!evt.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal) || evt.Value is null)
            {
                return;
            }

            if (evt.Value.GetType().GetProperty("Command")?.GetValue(evt.Value) is DbCommand cmd
                && ReferenceEquals(cmd.Connection, connection))
            {
                seen.Enqueue(cmd.CommandTimeout);
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                _subscriptions.ForEach(s => s.Dispose());
            }
        }
    }
}
