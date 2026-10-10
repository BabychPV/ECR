// tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkExternalKeyDuplicateTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// L4-08 (аудит 2026-10-09, AN-76): порушення <c>UQ_RegistryExternalKey</c> у
/// <c>UnitOfWork.SaveChangesAsync</c> — чиста відмова <c>ECR-REG-0409</c>, а не сирий виняток бази.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>TryMapDuplicateKey</c> не мав гілки для <c>RegistryExternalKey</c>: програш гонитви за
/// GUID джерела (ручна прив'язка проти синку) доходив необробленим <c>DbUpdateException</c> — для синку
/// це збій усього прогону, бо такий виняток не входить в його <c>IsBatchFailure</c>.
///
/// ⚠ Гілку вибирає ІНДЕКС, який порушено, а не перша додана сутність пакета: разом із ключем у
/// збереженні їде новий запис довідника, і гілка «код запису зайнято» назвала б чужу причину.
/// ⛔ Мутація: прибрати перевірку <c>UQ_RegistryExternalKey</c> на початку <c>TryMapDuplicateKey</c> —
/// обидва тести червоні (сирий <c>DbUpdateException</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitOfWorkExternalKeyDuplicateTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L4-08")]
    public async Task Дубль_зовнішнього_ключа_джерела_дає_ECR_REG_0409_а_не_сирий_виняток_бази()
    {
        var (dataSourceId, holder, other) = await ArrangeAsync();
        var externalId = Guid.NewGuid().ToString("D");

        await using (var winner = Context())
        {
            winner.RegistryExternalKeys.Add(new RegistryExternalKey(holder, dataSourceId, externalId));
            await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
        }

        await using var loser = Context();
        loser.RegistryExternalKeys.Add(new RegistryExternalKey(other, dataSourceId, externalId));

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.RegistryEntryInUse, thrown.ErrorCode);
        Assert.Equal("err.ECR-REG-0409.externalKeyTakenConcurrently", thrown.Details!["messageKey"]);
        Assert.Equal(externalId, thrown.Details["externalId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L4-08")]
    public async Task Дубль_ключа_в_пакеті_з_новим_записом_мапиться_за_індексом_а_не_за_першою_доданою_сутністю()
    {
        var (dataSourceId, holder, other) = await ArrangeAsync();
        var registryId = await RegistryOfAsync(other);
        var externalId = Guid.NewGuid().ToString("D");

        await using (var winner = Context())
        {
            winner.RegistryExternalKeys.Add(new RegistryExternalKey(holder, dataSourceId, externalId));
            await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
        }

        // Пакет, як у синку: новий запис довідника ПОРУЧ із ключем (цей запис сам по собі унікальний).
        await using var loser = Context();
        loser.RegistryEntries.Add(new RegistryEntry(registryId, EcrCode.Create($"N{_tag}"), Text("New")));
        loser.RegistryExternalKeys.Add(new RegistryExternalKey(other, dataSourceId, externalId));

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.RegistryEntryInUse, thrown.ErrorCode);
        Assert.Equal("err.ECR-REG-0409.externalKeyTakenConcurrently", thrown.Details!["messageKey"]);
    }

    /// <summary>Джерело й два записи одного довідника — «той, хто тримає GUID» і «той, хто програє».</summary>
    private async Task<(int DataSourceId, long Holder, long Other)> ArrangeAsync()
    {
        await using var db = Context();

        var registry = new RegistryDef(EcrCode.Create($"L408_{_tag}"), Text("L4-08"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var holder = new RegistryEntry(registry.Id, EcrCode.Create($"H{_tag}"), Text("Holder"));
        var other = new RegistryEntry(registry.Id, EcrCode.Create($"O{_tag}"), Text("Other"));
        db.RegistryEntries.AddRange(holder, other);

        var dataSource = new DataSource(
            EcrCode.Create($"L408S_{_tag}"), Text("PI AF"), ExternalTransport.PiWebApi, "https://af.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        return (dataSource.Id, holder.Id, other.Id);
    }

    private async Task<int> RegistryOfAsync(long entryId)
    {
        await using var db = Context();
        return await db.RegistryEntries.AsNoTracking().Where(e => e.Id == entryId).Select(e => e.RegistryDefId).SingleAsync();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
