using System.Collections.Concurrent;
using System.Data.Common;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
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
