// tests/Ecr.Infrastructure.Tests/Persistence/PermitHeaderLookupTests.cs
using System.Collections.Concurrent;
using System.Data.Common;
using Ecr.Application.Registries.Keys;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// PM-04b: вимкнений запис ЗВИЧАЙНОГО довідника не платить запитом до <c>cfg.PermitTypeDef</c> (чи довідник - шапка типу
/// дозволу): шапка завжди має код <c>PRM_&lt;тип&gt;</c>, тож для решти відповідь відома без бази.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати з <c>RegistryKeyService.ApplyAsync</c> умову <c>StartsWith(PRM_)</c> - червоніє
/// «Вимкнений_запис_звичайного_довідника_не_ходить_у_PermitTypeDef» (1 запит замість 0); контрольний тест із шапкою
/// <c>PRM_*</c> доводить, що лічильник бачить такий запит.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitHeaderLookupTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимкнений_запис_звичайного_довідника_не_ходить_у_PermitTypeDef()
        => Assert.Empty(await PermitTypeQueriesAsync($"PM04B_{_tag}"));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимкнений_запис_шапки_PRM_ходить_у_PermitTypeDef_рівно_раз()
        => Assert.Single(await PermitTypeQueriesAsync($"PRM_B{_tag}"));

    private async Task<List<string>> PermitTypeQueriesAsync(string registryCode)
    {
        await using var seed = sql.CreateContext();
        var registry = new RegistryDef(EcrCode.Create(registryCode), Text("probe"), isTemporal: false);
        seed.RegistryDefs.Add(registry);
        await seed.SaveChangesAsync();
        var field = new RegistryFieldDef(registry.Id, EcrCode.Create("K"), Text("K"), CellDataType.String, 1);
        field.Update(Text("K"), 1, isRequired: true);
        seed.RegistryFieldDefs.Add(field);
        await seed.SaveChangesAsync();
        seed.RegistryKeyDefs.Add(new RegistryKeyDef(registry.Id, EcrCode.Create("PK"), Text("PK"), [field], true, ignoreCase: true, createdByUserId: 0, Now));
        await seed.SaveChangesAsync();

        var recorder = new CommandRecorder();
        await using var db = new EcrDbContext(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(recorder)).Options);
        await using var transaction = await db.Database.BeginTransactionAsync();

        var definition = await db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Id == registry.Id);
        var service = new RegistryKeyService(new RegistryKeyStore(db), new UnitOfWork(db));
        var keys = await service.ListActiveKeysAsync(registry.Id, default);

        var entry = new RegistryEntry(registry.Id, EcrCode.Create("E1"), Text("E1"), 0, Now);
        entry.Deactivate();
        db.RegistryEntries.Add(entry);
        var value = new RegistryValue(entry, field.Id);
        value.Set(CellDataType.String, "k-1", unitId: null);
        db.RegistryValues.Add(value);

        recorder.Commands.Clear();
        await service.ApplyAsync(definition, keys, [entry], default);
        var seen = recorder.Commands.Where(c => c.Contains("PermitTypeDef", StringComparison.OrdinalIgnoreCase)).ToList();

        await transaction.RollbackAsync();
        return seen;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
