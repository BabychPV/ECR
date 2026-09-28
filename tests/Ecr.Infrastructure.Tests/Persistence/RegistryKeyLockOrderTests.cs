// tests/Ecr.Infrastructure.Tests/Persistence/RegistryKeyLockOrderTests.cs
using System.Data.Common;
using System.Diagnostics;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Пакетне блокування ключів довідника бере замки в ОДНОМУ порядку — порядку індексу
/// <c>(RegistryKeyDefId, KeyHash)</c> — незалежно від порядку, у якому хеші подано, і від плану
/// (аудит P9, зауваження інтегратора до <c>LockLiveHoldersAsync</c>). На РЕАЛЬНОМУ SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (перезбіркою) — у звіті коміту: прибрати сортування хешів у
/// <c>LockLiveHoldersAsync</c> (порції в порядку подання) разом з <c>ORDER BY</c> →
/// <see cref="Дві_транзакції_з_перетином_ключів_у_зворотному_порядку_не_дають_1205"/> і
/// <see cref="Порції_й_параметри_йдуть_у_порядку_індексу"/> червоні; прибрати лише
/// <c>ORDER BY</c>/<c>MAXDOP 1</c> чи <c>FORCESEEK</c> у поштучному запиті →
/// <see cref="Порції_й_параметри_йдуть_у_порядку_індексу"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryKeyLockOrderTests(SqlServerFixture sql)
{
    private const int Iterations = 30;

    /// <summary>Дві порції по <see cref="RegistryKeyStore.HashesPerQuery"/>: замки беруться двома операторами.</summary>
    private const int HashCount = 2 * RegistryKeyStore.HashesPerQuery;

    /// <summary>Скільки переможець тримає замки: суперник мусить чекати щонайменше стільки.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(150);

    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Дві_транзакції_з_перетином_ключів_у_зворотному_порядку_не_дають_1205()
    {
        // Порядок подання — порядок генерації, тобто випадковий щодо індексу; B — навпаки від A.
        // Без сортування перша порція A і перша порція B — непересічні половини, обидві
        // блокуються одночасно, а другі порції кожної чекають одна одну: класичне 1205.
        var (keyDefId, hashes) = await ArrangeAsync(HashCount);
        var forward = hashes;
        var backward = hashes.AsEnumerable().Reverse().ToList();

        var deadlocks = 0;
        var failures = new List<string>();
        for (var i = 0; i < Iterations; i++)
        {
            var (a, b) = await RaceAsync(keyDefId, forward, backward);
            foreach (var run in new[] { a, b }.Where(r => r.Error is not null))
            {
                var number = Chain(run.Error).OfType<SqlException>().FirstOrDefault()?.Number;
                deadlocks += number == 1205 ? 1 : 0;
                failures.Add($"ітерація {i}: {number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—"} {run.Error!.GetType().Name}: {run.Error.Message}");
            }

            if (a.Error is null && b.Error is null)
            {
                // Спільні хеші не можна тримати вдвох: той, хто взяв замки пізніше, узяв їх не
                // раніше, ніж перший відтримав свої Hold.
                var (first, second) = a.Acquired <= b.Acquired ? (a, b) : (b, a);
                if (second.Acquired < first.Acquired + Hold)
                {
                    failures.Add(
                        $"ітерація {i}: друга транзакція взяла замки через {(second.Acquired - first.Acquired).TotalMilliseconds:F0} мс після першої, а не після її {Hold.TotalMilliseconds:F0} мс утримання");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"взаємоблокувань 1205: {deadlocks} із {Iterations} ітерацій; усі відхилення:\n{string.Join('\n', failures)}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Порції_й_параметри_йдуть_у_порядку_індексу()
    {
        // Хешів більше за дві порції, подані навпаки від порядку індексу й із повтором: блокуються
        // рівно різні хеші, зростаючи побайтно — і всередині порції, і від порції до порції.
        var hashes = Enumerable.Range(0, 2 * RegistryKeyStore.HashesPerQuery + 7)
            .Select(i => Hash($"ORDER{i}"))
            .ToList();
        var given = hashes.OrderByDescending(Convert.ToHexString, StringComparer.Ordinal).ToList();
        given.Add(given[3]);

        var capture = new LockCommandCapture();
        await using var db = CapturingContext(capture);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var store = new RegistryKeyStore(db);

        await store.LockLiveHoldersAsync(-1, given, CancellationToken.None);

        Assert.Equal(3, capture.Commands.Count);
        var locked = capture.Commands.SelectMany(c => c.Hashes).ToList();
        Assert.Equal(hashes.Count, locked.Count);

        // Порядок індексу — беззнакове побайтне порівняння binary(32); hex у верхньому регістрі
        // порівнюється ординально так само.
        var expected = hashes.Select(Convert.ToHexString).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, locked.Select(Convert.ToHexString).ToList());

        foreach (var command in capture.Commands)
        {
            Assert.Contains("FORCESEEK (IX_RegistryEntryKey_Hash (RegistryKeyDefId, KeyHash))", command.Text, StringComparison.Ordinal);
            Assert.Contains("ORDER BY k.RegistryKeyDefId, k.KeyHash", command.Text, StringComparison.Ordinal);
            Assert.EndsWith("OPTION (MAXDOP 1)", command.Text.TrimEnd(), StringComparison.Ordinal);
        }

        // Поштучний шлях — той самий запит: без FORCESEEK його план блокував би інший індекс.
        capture.Commands.Clear();
        store.ForgetPreloaded();
        Assert.Empty(await store.FindLiveHoldersForUpdateAsync(-1, hashes[0], 0, CancellationToken.None));
        var single = Assert.Single(capture.Commands);
        Assert.Contains("FORCESEEK (IX_RegistryEntryKey_Hash (RegistryKeyDefId, KeyHash))", single.Text, StringComparison.Ordinal);
        Assert.Contains("ORDER BY k.RegistryKeyDefId, k.KeyHash", single.Text, StringComparison.Ordinal);
        Assert.EndsWith("OPTION (MAXDOP 1)", single.Text.TrimEnd(), StringComparison.Ordinal);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Дві транзакції одночасно блокують хеші — кожна у своєму порядку подання; переможець тримає
    /// замки <see cref="Hold"/> і відкочується.
    /// </summary>
    private async Task<(Run A, Run B)> RaceAsync(int keyDefId, IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b)
    {
        var clock = Stopwatch.StartNew();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runA = LockAsync(a, readyA);
        var runB = LockAsync(b, readyB);
        await Task.WhenAll(readyA.Task, readyB.Task);
        go.SetResult();

        return (await runA, await runB);

        async Task<Run> LockAsync(IReadOnlyList<byte[]> hashes, TaskCompletionSource ready)
        {
            await using var db = sql.CreateContext();
            await db.Database.OpenConnectionAsync();

            // Зависання замість 1205 не має тягнутися вічно: 1222 через 30 с — теж червоний тест.
            await db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 30000");
            await using var transaction = await db.Database.BeginTransactionAsync();
            ready.SetResult();
            await go.Task;

            try
            {
                await new RegistryKeyStore(db).LockLiveHoldersAsync(keyDefId, hashes, CancellationToken.None);
                var acquired = clock.Elapsed;
                await Task.Delay(Hold);
                await transaction.RollbackAsync();
                return new Run(acquired, null);
            }
            catch (Exception error) when (error is SqlException or InvalidOperationException or DbException)
            {
                return new Run(default, error);
            }
        }
    }

    /// <summary>Довідник з одним ключем і <paramref name="count"/> живими записами, кожен зі своїм хешем.</summary>
    private async Task<(int KeyDefId, List<byte[]> Hashes)> ArrangeAsync(int count)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var registry = new RegistryDef(EcrCode.Create($"KO_{tag}"), Text("Key lock order probe"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var code = new RegistryFieldDef(registry.Id, EcrCode.Create("CODE_PART"), Text("Code"), CellDataType.String, 1);
        code.Update(Text("Code"), 1, isRequired: true);
        db.RegistryFieldDefs.Add(code);
        await db.SaveChangesAsync();

        var key = new RegistryKeyDef(
            registry.Id, EcrCode.Create("PK"), Text("PK"), [code], isPrimary: true, ignoreCase: true, 0, Now);
        db.RegistryKeyDefs.Add(key);
        await db.SaveChangesAsync();

        var hashes = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = new RegistryEntry(registry.Id, EcrCode.Create($"E{i}"), Text($"E{i}"), 0, Now);
            db.RegistryEntries.Add(entry);
            var hash = Hash($"LO{i}");
            db.RegistryEntryKeys.Add(new RegistryEntryKey(entry, key.Id, hash, $"LO{i}"));
            hashes.Add(hash);
        }

        await db.SaveChangesAsync();
        return (key.Id, hashes);
    }

    private EcrDbContext CapturingContext(LockCommandCapture capture)
        => new(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(capture))
            .Options);

    private static byte[] Hash(string part)
        => RegistryKeyNormalizer.Hash(RegistryKeyNormalizer.Canonical([new RegistryKeyPart(CellDataType.String, part)])!);

    private static IEnumerable<Exception> Chain(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            yield return error;
        }
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Run(TimeSpan Acquired, Exception? Error);

    /// <summary>Оператори з <c>UPDLOCK</c>: текст і хеші-параметри <c>@h0..@hN</c> у порядку номерів.</summary>
    private sealed class LockCommandCapture : DbCommandInterceptor
    {
        public List<(string Text, List<byte[]> Hashes)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal))
            {
                var hashes = command.Parameters.Cast<DbParameter>()
                    .Where(p => p.ParameterName.StartsWith("@h", StringComparison.Ordinal))
                    .OrderBy(p => int.Parse(p.ParameterName[2..], System.Globalization.CultureInfo.InvariantCulture))
                    .Select(p => (byte[])p.Value!)
                    .ToList();
                Commands.Add((command.CommandText, hashes));
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
