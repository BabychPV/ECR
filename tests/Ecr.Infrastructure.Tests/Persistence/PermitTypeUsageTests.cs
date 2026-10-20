// tests/Ecr.Infrastructure.Tests/Persistence/PermitTypeUsageTests.cs
using Ecr.Application.Common;
using Ecr.Application.Permits;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Кількості по таблицях типу дозволу на справжній SQL Server (Permits, PM-03b2): живі дозволи і живі рядки таблиць,
/// видалені логічно не рахуються, а відповідь будується ОДНИМ запитом до записів реєстрів.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>PermitTypeStore.CountPermitsAsync</c> прибрати <c>&amp;&amp; !e.IsDeleted</c> - червоні тести
/// «Рахує_лише_живі…» (видалені дозволи й рядки потрапляють у число); у <c>GetPermitTypeUsageHandler</c> замінити один
/// виклик <c>CountPermitsAsync</c> на цикл по таблицях - червоний «Рахує_одним_запитом…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitTypeUsageTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рахує_лише_живі_дозволи_і_живі_рядки_кожної_таблиці()
    {
        var ids = await SeedTypeAsync();
        await AddEntriesAsync(ids.Header, "P", live: 3, deleted: 1);
        await AddEntriesAsync(ids.Table1, "A", live: 4, deleted: 2);

        await using var db = sql.CreateContext();
        var usage = await Handler(db).HandleAsync($"T{_tag}", CancellationToken.None);

        Assert.Equal(3, usage.InstanceCount);
        Assert.Equal([("TBL1", 4), ("TBL2", 0)], usage.Tables.Select(t => (t.Code, t.RowCount)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тип_без_дозволів_дає_нулі_а_не_відсутні_таблиці()
    {
        await SeedTypeAsync();

        await using var db = sql.CreateContext();
        var usage = await Handler(db).HandleAsync($"T{_tag}", CancellationToken.None);

        Assert.Equal(0, usage.InstanceCount);
        Assert.Equal([("TBL1", 0), ("TBL2", 0)], usage.Tables.Select(t => (t.Code, t.RowCount)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рахує_одним_запитом_до_записів_реєстрів()
    {
        var ids = await SeedTypeAsync();
        await AddEntriesAsync(ids.Header, "P", live: 2, deleted: 0);
        await AddEntriesAsync(ids.Table2, "B", live: 5, deleted: 0);

        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)).Options);

        var usage = await Handler(db).HandleAsync($"T{_tag}", CancellationToken.None);
        var seen = counter.Tally.Snapshot();

        Assert.Equal(2, usage.InstanceCount);
        Assert.Equal(5, usage.Tables.Single(t => t.Code == "TBL2").RowCount);
        // Один згрупований запит на всі реєстри типу (шапка + таблиці), а не по запиту на таблицю.
        Assert.True(seen["SELECT dic.RegistryEntry"] == 1, seen.Format());
    }

    private static GetPermitTypeUsageHandler Handler(EcrDbContext db)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.View").Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        return new GetPermitTypeUsageHandler(new PermitTypeStore(db), access, user);
    }

    private async Task AddEntriesAsync(int registryDefId, string prefix, int live, int deleted)
    {
        await using var db = sql.CreateContext();
        for (var i = 0; i < live + deleted; i++)
        {
            var entry = new RegistryEntry(registryDefId, EcrCode.Create($"{prefix}{i}_{_tag}"), Text($"{prefix}{i}"), 1, DateTime.UnixEpoch);
            if (i >= live)
            {
                entry.SoftDelete();
            }

            db.RegistryEntries.Add(entry);
        }

        await db.SaveChangesAsync();
    }

    private async Task<(int Header, int Table1, int Table2)> SeedTypeAsync()
    {
        await using var db = sql.CreateContext();

        var header = new RegistryDef(EcrCode.Create($"PRM_U{_tag}"), Text("Header"), isTemporal: true);
        var table1 = new RegistryDef(EcrCode.Create($"PRM_U{_tag}_T1"), Text("Table 1"), isTemporal: false);
        var table2 = new RegistryDef(EcrCode.Create($"PRM_U{_tag}_T2"), Text("Table 2"), isTemporal: false);
        db.RegistryDefs.AddRange(header, table1, table2);
        await db.SaveChangesAsync();

        var type = new PermitTypeDef(EcrCode.Create($"T{_tag}"), Text("Type"), PermitCategory.Air, header.Id);
        db.PermitTypeDefs.Add(type);
        await db.SaveChangesAsync();

        db.PermitTypeTables.AddRange(
            new PermitTypeTable(type.Id, EcrCode.Create("TBL1"), Text("Table 1"), 1, table1.Id),
            new PermitTypeTable(type.Id, EcrCode.Create("TBL2"), Text("Table 2"), 2, table2.Id));
        await db.SaveChangesAsync();

        return (header.Id, table1.Id, table2.Id);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
