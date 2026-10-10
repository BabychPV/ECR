using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Keys;
using Ecr.Application.Registries.Rows;
using Ecr.Application.Registries.Rules;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D1-04 (R5-D1): дві паралельні правки ОДНОГО рядка з однією <c>baseVersion</c> — друга отримує
/// <c>ECR-REG-4093 entryChanged</c>, а не мовчки затирає першу.
/// </summary>
/// <remarks>
/// ⛔ Дефект: <c>RegistryBatchHandler.CheckVersionsAsync</c> стояв ДО транзакції й без блокування.
/// Сценарій тесту: пакет B прочитав версії й чекає (гачок у <see cref="IRegistryRowsQuery.ReadRowsAsync"/>),
/// поки пакет A пройде свою перевірку версій. До виправлення A її проходив (B нічого не тримав), обидва
/// пакети застосовувалися, і значення A губилося — тест червоніє на <c>Assert.False(a.Applied)</c> (або A
/// падає 13535 на темпоральній таблиці). Після виправлення B тримає <c>UPDLOCK</c> на записі, A чекає коміту
/// B, бачить нову версію й відмовляється. Чекання A обмежене тайм-аутом, тож тест детермінований в обох
/// гілках: результат не залежить від того, хто кого обжене.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryBatchConcurrencyTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Скільки B чекає, поки A пройде перевірку версій (після виправлення — не дочекається).</summary>
    private static readonly TimeSpan RivalWindow = TimeSpan.FromSeconds(3);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дві_паралельні_правки_одного_рядка_з_тією_самою_baseVersion_дають_4093_другій()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registryCode = $"CONC{tag}";
        var (entryId, entryCode, baseVersion) = await SeedAsync(registryCode, tag);

        var rivalChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RegistryBatchResult>? rival = null;

        // Пакет A — суперник: сигналізує, щойно його перевірка версій прочитала базу.
        async Task<RegistryBatchResult> RunRivalAsync()
        {
            await using var db = Context();
            var query = new HookedRowsQuery(new RegistryRowsQuery(db), afterFirstRead: () =>
            {
                rivalChecked.TrySetResult();
                return Task.CompletedTask;
            });

            return await Handler(db, query).HandleAsync(
                registryCode, Request(entryId, entryCode, baseVersion, "from-A"), dryRun: false, CancellationToken.None);
        }

        // Пакет B — прочитав версії, запускає A і чекає, поки A пройде СВОЮ перевірку (або тайм-аут).
        RegistryBatchResult b;
        await using (var db = Context())
        {
            var query = new HookedRowsQuery(new RegistryRowsQuery(db), afterFirstRead: async () =>
            {
                rival = Task.Run(RunRivalAsync);
                await Task.WhenAny(rivalChecked.Task, Task.Delay(RivalWindow));
            });

            b = await Handler(db, query).HandleAsync(
                registryCode, Request(entryId, entryCode, baseVersion, "from-B"), dryRun: false, CancellationToken.None);
        }

        Assert.NotNull(rival);
        var a = await rival;

        Assert.True(b.Applied);
        Assert.False(a.Applied);
        var error = Assert.Single(Assert.Single(a.Rows).Errors);
        Assert.Equal("ECR-REG-4093", error.ErrorCode);

        await using var check = Context();
        var stored = await check.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryEntryId == entryId)
            .Select(v => v.ValueString)
            .SingleAsync();
        Assert.Equal("from-B", stored);
    }

    private static RegistryBatchRequest Request(long entryId, string code, string baseVersion, string name)
        => new([new RegistryBatchItemDto("r1", "upsert", entryId, code, baseVersion, new Dictionary<string, object?> { ["Name"] = name })]);

    private async Task<(long EntryId, string Code, string Version)> SeedAsync(string registryCode, string tag)
    {
        int defId;
        await using (var seed = Context())
        {
            var def = new RegistryDef(EcrCode.Create(registryCode), Text($"Conc {tag}"), isTemporal: false);
            seed.RegistryDefs.Add(def);
            await seed.SaveChangesAsync();
            seed.RegistryFieldDefs.Add(new RegistryFieldDef(def.Id, EcrCode.Create("Name"), Text("Name"), CellDataType.String, 1));
            await seed.SaveChangesAsync();
            defId = def.Id;
        }

        var entryCode = $"E{tag}";
        await using (var seedDb = Context())
        {
            var (user, clock) = Actors();
            var writer = new RegistryEntryWriter(
                new RegistryStore(seedDb), new UnitOfWork(seedDb, clock, user), Substitute.For<IAuditWriter>(), user, clock);
            var seeded = await writer.WriteAsync(
                new RegistryEntryWriteBatch(defId, [new RegistryEntryWrite(entryCode, new Dictionary<string, object?> { ["Name"] = "seed" })]),
                CancellationToken.None);
            Assert.Empty(seeded.Errors);
        }

        await using var read = Context();
        var entryId = await read.RegistryEntries.AsNoTracking()
            .Where(e => e.RegistryDefId == defId && e.Code == entryCode)
            .Select(e => e.Id)
            .SingleAsync();
        var slice = await new RegistryRowsQuery(read).ReadRowsAsync([entryId], asOfUtc: null, CancellationToken.None);

        return (entryId, entryCode, RegistryRowVersion.Encode(slice.Versions[entryId]));
    }

    private static RegistryBatchHandler Handler(EcrDbContext db, IRegistryRowsQuery rows)
    {
        var (user, clock) = Actors();
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.EditData").Build());
        var rules = Substitute.For<IRegistryRuleEngine>();
        rules.EvaluateAsync(
                Arg.Any<RegistryDef>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(RegistryRuleCheck.None);

        var uow = new UnitOfWork(db, clock, user);
        var registries = new RegistryStore(db);
        var keyService = new RegistryKeyService(new RegistryKeyStore(db), uow);
        var audit = Substitute.For<IAuditWriter>();
        var writer = new RegistryEntryWriter(registries, uow, audit, user, clock, keyService);
        var deleter = new DeleteRegistryEntryHandler(registries, uow, audit, access, user, clock, keyService);
        return new RegistryBatchHandler(registries, rows, uow, writer, deleter, access, user, rules);
    }

    private static (ICurrentUser User, IClock Clock) Actors()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns("en");
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        return (user, clock);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Справжній запит із гачком після ПЕРШОГО <see cref="ReadRowsAsync"/> (перевірка версій).</summary>
    private sealed class HookedRowsQuery(IRegistryRowsQuery inner, Func<Task> afterFirstRead) : IRegistryRowsQuery
    {
        private int _reads;

        public async Task<RegistryRowsSlice> ReadRowsAsync(
            IReadOnlyCollection<long> registryEntryIds, DateTime? asOfUtc, CancellationToken ct)
        {
            var slice = await inner.ReadRowsAsync(registryEntryIds, asOfUtc, ct);
            if (Interlocked.Increment(ref _reads) == 1)
            {
                await afterFirstRead();
            }

            return slice;
        }

        public Task LockEntriesAsync(IReadOnlyCollection<long> registryEntryIds, CancellationToken ct)
            => inner.LockEntriesAsync(registryEntryIds, ct);

        public Task<IReadOnlyList<RegistryEntry>> ListEntriesAsync(int registryDefId, DateTime? asOfUtc, CancellationToken ct)
            => inner.ListEntriesAsync(registryDefId, asOfUtc, ct);

        public Task<IReadOnlyList<RegistryRowValue>> ListFieldValuesAsync(
            IReadOnlyCollection<int> registryFieldDefIds, DateTime? asOfUtc, CancellationToken ct)
            => inner.ListFieldValuesAsync(registryFieldDefIds, asOfUtc, ct);

        public Task<RegistryEntryHistorySlice> ReadEntryHistoryAsync(long registryEntryId, CancellationToken ct)
            => inner.ReadEntryHistoryAsync(registryEntryId, ct);

        public Task<VisibleEntryPage> PageVisibleEntriesAsync(
            VisibleEntriesFilter filter, long after, int take, CancellationToken ct)
            => inner.PageVisibleEntriesAsync(filter, after, take, ct);

        public Task<IReadOnlyList<RegistryEntrySlim>> ListVisibleSlimAsync(VisibleEntriesFilter filter, CancellationToken ct)
            => inner.ListVisibleSlimAsync(filter, ct);

        public Task<IReadOnlyList<RegistryRowValue>> ListTextMatchesAsync(
            IReadOnlyCollection<int> registryFieldDefIds, string contains, DateTime? asOfUtc, CancellationToken ct)
            => inner.ListTextMatchesAsync(registryFieldDefIds, contains, asOfUtc, ct);

        public Task<IReadOnlyList<RegistryRowValue>> ListEqualMatchesAsync(
            int registryFieldDefId, CellDataType type, string canonical, DateTime? asOfUtc, CancellationToken ct)
            => inner.ListEqualMatchesAsync(registryFieldDefId, type, canonical, asOfUtc, ct);
    }
}
