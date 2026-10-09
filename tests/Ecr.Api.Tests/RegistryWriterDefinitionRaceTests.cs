using System.Data.Common;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Keys;
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
/// D1-05 (R5-D1): опис довідника, збережений МІЖ читанням опису писачем і його транзакцією, дає писачу
/// <c>409 ECR-REG-0409 definitionChanged</c>, а не тихий запис за старим описом.
/// </summary>
/// <remarks>
/// ⛔ Дефект: <c>RegistryEntryWriter</c> читав активні ключі й поля до транзакції без блокування і не
/// звіряв <c>DefinitionVersion</c>. Адміністратор, що паралельно вмикав ключ, публікував рядки ключа лише
/// для закомічених записів, і записи пакета комітилися без рядка нового ключа — дублі під унікальним
/// ключем. Тест моделює «паралельне збереження опису» детерміновано: щойно писач прочитав опис (перша
/// вибірка з <c>[cfg].[RegistryDef]</c>), окреме з'єднання піднімає <c>DefinitionVersion</c> і комітить.
/// До виправлення запис проходив (тест червоніє на <c>ThrowsAsync</c>), після — 409 і жодного запису.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryWriterDefinitionRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збереження_опису_під_час_запису_пакета_дає_409_пакету_а_не_запис_за_старим_описом()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        int defId;
        await using (var seed = Context(null))
        {
            var def = new RegistryDef(EcrCode.Create($"DRACE{tag}"), Text($"Race {tag}"), isTemporal: false);
            seed.RegistryDefs.Add(def);
            await seed.SaveChangesAsync();
            seed.RegistryFieldDefs.Add(new RegistryFieldDef(def.Id, EcrCode.Create("Name"), Text("Name"), CellDataType.String, 1));
            await seed.SaveChangesAsync();
            defId = def.Id;
        }

        var concurrentSave = new ConcurrentDefinitionSave(sql.ConnectionString, defId);
        await using var db = Context(concurrentSave);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns("en");
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        var uow = new UnitOfWork(db, clock, user);
        var writer = new RegistryEntryWriter(
            new RegistryStore(db), uow, Substitute.For<IAuditWriter>(), user, clock,
            new RegistryKeyService(new RegistryKeyStore(db), uow));

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => writer.WriteAsync(
            new RegistryEntryWriteBatch(defId, [new RegistryEntryWrite($"N{tag}", new Dictionary<string, object?> { ["Name"] = "x" })]),
            CancellationToken.None));

        Assert.True(concurrentSave.Fired);
        Assert.Equal("ECR-REG-0409", conflict.ErrorCode);

        await using var check = Context(null);
        Assert.False(await check.RegistryEntries.AsNoTracking().AnyAsync(e => e.RegistryDefId == defId));
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context(IInterceptor? interceptor)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new EcrDbContext(options.Options);
    }

    /// <summary>
    /// Після ПЕРШОГО читання опису писачем — «паралельне збереження опису»: окреме з'єднання піднімає
    /// <c>DefinitionVersion</c> і комітить (як <c>SaveRegistryDefinitionHandler</c>).
    /// </summary>
    private sealed class ConcurrentDefinitionSave(string connectionString, int registryDefId) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [cfg].[RegistryDef]", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                await using var admin = new EcrDbContext(
                    new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connectionString).Options);
                await admin.RegistryDefs
                    .Where(d => d.Id == registryDefId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.DefinitionVersion, d => d.DefinitionVersion + 1), cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
