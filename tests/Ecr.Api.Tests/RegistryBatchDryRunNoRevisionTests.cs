using System.Collections.Concurrent;
using System.Data.Common;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Keys;
using Ecr.Application.Registries.Rows;
using Ecr.Application.Registries.Rules;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// L5-13 (аудит 2026-10-03): прогін сітки з заглушковими кодами (<c>dryRun</c>) не робить справжнього
/// <c>UPDATE cfg.RegistryDef</c> — інакше кожна жива перевірка (раз на ~600 мс) тримала б блокування
/// рядка довідника до відкату.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: у <c>RegistryEntryWriter.WriteTargetsAsync</c> передати <c>bumpRevision: true</c>
/// (або прибрати параметр) — і <see cref="DryRun_не_оновлює_cfg_RegistryDef"/> червоніє. Контрольний тест
/// (<see cref="Звичайний_запис_оновлює_cfg_RegistryDef"/>) доводить, що перехоплювач справді бачить такі
/// команди, тож зелений «жодного UPDATE» не є сліпотою спостерігача.
///
/// ⛔ L5-13 (друга половина, аудит 2026-10-09): те саме через <see cref="RegistryBatchHandler"/> з
/// <c>op:"delete"</c> і активним ключем. Мутації: у <c>DeleteRegistryEntryHandler</c> прибрати
/// <c>if (!dryRun)</c> перед <c>BumpDataRevision</c> — червоніє <see cref="Пакет_dryRun_з_видаленням_не_оновлює_cfg_RegistryDef_і_не_бере_UPDLOCK"/>
/// (UPDATE cfg.RegistryDef); у <c>RegistryEntryWriter.WriteTargetsAsync</c> прибрати
/// <c>&amp;&amp; !placeholderAutoCodes</c> — той самий тест червоніє на <c>UPDLOCK, HOLDLOCK</c> служби ключів.
/// Контроль — <see cref="Пакет_без_dryRun_з_видаленням_оновлює_cfg_RegistryDef_і_бере_UPDLOCK"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryBatchDryRunNoRevisionTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task DryRun_не_оновлює_cfg_RegistryDef()
    {
        var (commands, applied, revisionBefore, revisionAfter) = await RunAsync(placeholder: true);

        Assert.True(applied);
        Assert.DoesNotContain(commands, c => IsRegistryDefUpdate(c));
        Assert.Equal(revisionBefore, revisionAfter);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звичайний_запис_оновлює_cfg_RegistryDef()
    {
        var (commands, applied, revisionBefore, revisionAfter) = await RunAsync(placeholder: false);

        Assert.True(applied);
        Assert.Contains(commands, c => IsRegistryDefUpdate(c));
        Assert.Equal(revisionBefore + 1, revisionAfter);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакет_dryRun_з_видаленням_не_оновлює_cfg_RegistryDef_і_не_бере_UPDLOCK()
    {
        var run = await RunBatchAsync(dryRun: true);

        Assert.True(run.Report.DryRun);
        Assert.False(run.Report.Applied);
        Assert.Equal(1, run.Report.Deleted);
        Assert.Equal(1, run.Report.Added);
        Assert.DoesNotContain(run.Commands, IsRegistryDefUpdate);
        Assert.DoesNotContain(run.Commands, c => c.Contains("UPDLOCK", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(run.RevisionBefore, run.RevisionAfter);

        // Відкат: запис, який «видалили» у прогоні, лишився живим.
        Assert.False(run.DeletedEntryIsDeleted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакет_без_dryRun_з_видаленням_оновлює_cfg_RegistryDef_і_бере_UPDLOCK()
    {
        var run = await RunBatchAsync(dryRun: false);

        Assert.True(run.Report.Applied);
        Assert.Contains(run.Commands, IsRegistryDefUpdate);
        Assert.Contains(run.Commands, c => c.Contains("UPDLOCK", StringComparison.OrdinalIgnoreCase));
        Assert.True(run.RevisionAfter > run.RevisionBefore);
        Assert.True(run.DeletedEntryIsDeleted);
    }

    private static bool IsRegistryDefUpdate(string sqlText)
        => sqlText.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase)
           && sqlText.Contains("[cfg].[RegistryDef]", StringComparison.OrdinalIgnoreCase);

    private async Task<(List<string> Commands, bool Applied, int Before, int After)> RunAsync(bool placeholder)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        int defId;
        await using (var seed = Context(null))
        {
            var def = new RegistryDef(EcrCode.Create($"DRYR{tag}"), Text($"Dry {tag}"), isTemporal: false);
            seed.RegistryDefs.Add(def);
            await seed.SaveChangesAsync();
            seed.RegistryFieldDefs.Add(new RegistryFieldDef(def.Id, EcrCode.Create("Name"), Text("Name"), CellDataType.String, 0));
            await seed.SaveChangesAsync();
            defId = def.Id;
        }

        var before = await RevisionAsync(defId);
        var recorder = new CommandRecorder();
        await using var db = Context(recorder);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        var registries = new RegistryStore(db);
        var writer = new RegistryEntryWriter(registries, new UnitOfWork(db, clock, user), Substitute.For<IAuditWriter>(), user, clock);

        var result = await writer.WriteAsync(
            new RegistryEntryWriteBatch(
                defId,
                [new RegistryEntryWrite($"N{tag}", new Dictionary<string, object?> { ["Name"] = "x" })])
            {
                PlaceholderAutoCodes = placeholder,
            },
            CancellationToken.None);

        Assert.Empty(result.Errors);
        return ([.. recorder.Commands], result.Applied, before, await RevisionAsync(defId));
    }

    private sealed record BatchRun(
        RegistryBatchResult Report, List<string> Commands, int RevisionBefore, int RevisionAfter, bool DeletedEntryIsDeleted);

    /// <summary>
    /// Довідник з первинним ключем по полю Name і двома записами; пакет — видалити перший і додати новий.
    /// Команди записуються лише під час самого пакета (посів — окремим контекстом без спостерігача).
    /// </summary>
    private async Task<BatchRun> RunBatchAsync(bool dryRun)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        int defId;
        await using (var seed = Context(null))
        {
            var def = new RegistryDef(EcrCode.Create($"DRYB{tag}"), Text($"DryB {tag}"), isTemporal: false);
            seed.RegistryDefs.Add(def);
            await seed.SaveChangesAsync();
            var name = new RegistryFieldDef(def.Id, EcrCode.Create("Name"), Text("Name"), CellDataType.String, 1);
            name.Update(Text("Name"), 1, isRequired: true);
            seed.RegistryFieldDefs.Add(name);
            await seed.SaveChangesAsync();
            seed.RegistryKeyDefs.Add(new RegistryKeyDef(
                def.Id, EcrCode.Create("PK"), Text("Primary"), [name],
                isPrimary: true, ignoreCase: true, createdByUserId: 0, DateTime.UtcNow));
            await seed.SaveChangesAsync();
            defId = def.Id;
        }

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns("en");
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        // Посів двох записів із живими рядками ключа — звичайним записом, без спостерігача.
        await using (var seedDb = Context(null))
        {
            var seedUow = new UnitOfWork(seedDb, clock, user);
            var seedWriter = new RegistryEntryWriter(
                new RegistryStore(seedDb), seedUow, Substitute.For<IAuditWriter>(), user, clock,
                new RegistryKeyService(new RegistryKeyStore(seedDb), seedUow));
            var seeded = await seedWriter.WriteAsync(
                new RegistryEntryWriteBatch(
                    defId,
                    [
                        new RegistryEntryWrite($"A{tag}", new Dictionary<string, object?> { ["Name"] = $"a{tag}" }),
                        new RegistryEntryWrite($"B{tag}", new Dictionary<string, object?> { ["Name"] = $"b{tag}" }),
                    ]),
                CancellationToken.None);
            Assert.Empty(seeded.Errors);
            Assert.True(seeded.Applied);
        }

        long doomedId;
        await using (var read = Context(null))
        {
            doomedId = await read.RegistryEntries.AsNoTracking()
                .Where(e => e.RegistryDefId == defId && e.Code == $"A{tag}").Select(e => e.Id).SingleAsync();
        }

        var before = await RevisionAsync(defId);
        var recorder = new CommandRecorder();
        await using var db = Context(recorder);

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
        var handler = new RegistryBatchHandler(registries, new RegistryRowsQuery(db), uow, writer, deleter, access, user, rules);

        var report = await handler.HandleAsync(
            $"DRYB{tag}",
            new RegistryBatchRequest(
            [
                new RegistryBatchItemDto("d", "delete", doomedId, null, null, null),
                new RegistryBatchItemDto("n", "upsert", null, $"N{tag}", null, new Dictionary<string, object?> { ["Name"] = $"n{tag}" }),
            ]),
            dryRun,
            CancellationToken.None);

        await using var check = Context(null);
        var deleted = await check.RegistryEntries.AsNoTracking().Where(e => e.Id == doomedId).Select(e => e.IsDeleted).SingleAsync();
        return new BatchRun(report, [.. recorder.Commands], before, await RevisionAsync(defId), deleted);
    }

    private async Task<int> RevisionAsync(int id)
    {
        await using var db = Context(null);
        return await db.RegistryDefs.AsNoTracking().Where(d => d.Id == id).Select(d => d.DataRevision).SingleAsync();
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context(CommandRecorder? recorder)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString);
        if (recorder is not null)
        {
            options.AddInterceptors(recorder);
        }

        return new EcrDbContext(options.Options);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
