// tests/Ecr.Infrastructure.Tests/Persistence/RegistryCompositionSchemaTests.cs
using System.Data.Common;
using System.Globalization;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>RK02RegistryComposition</c> на РЕАЛЬНОМУ SQL Server (RT-03,
/// FEATURE-REGISTRY-TABLES §3.2, §4.8, §5.10): композиція як ознака
/// Lookup-поля, режим коду, послідовність кодів і мітка зміни даних.
/// </summary>
/// <remarks>
/// ⛔ Обмеження перевіряється вставкою ПОВЗ домен (сирий SQL): домен і так не
/// пускає композицію на не-Lookup (<c>RegistryCompositionTests</c>), тож
/// перевірка через сутність довела б лише домен. База має тримати інваріант
/// для імпорту й скриптів.
///
/// Мутаційні докази (RT-03, §9.2): прибрати <c>CK_RegField_Composition</c> з
/// міграції й конфігурації — червоніє <see cref="Композиція_лише_на_Lookup"/>;
/// не викликати <c>StampRegistryDataChanges</c> в <c>UnitOfWork</c> — червоніє
/// <see cref="Мітка_зміни_росте_з_ревізією"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryCompositionSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-03")]
    public async Task Композиція_лише_на_Lookup()
    {
        var setup = await ArrangeAsync();
        await using var db = sql.CreateContext();

        // Lookup-поле стає композицією — дозволено.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.RegistryFieldDef SET RelationKind = 1, OnParentDelete = 1 WHERE Id = {setup.LookupFieldId}");

        // Те саме для рядкового поля — база відмовляє сама, без домену.
        var error = await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.RegistryFieldDef SET RelationKind = 1 WHERE Id = {setup.StringFieldId}"));
        Assert.Contains("CK_RegField_Composition", error.Message, StringComparison.Ordinal);

        var kinds = await db.RegistryFieldDefs.AsNoTracking()
                            .Where(f => f.RegistryDefId == setup.ChildRegistryId)
                            .ToDictionaryAsync(f => f.Code, f => f.RelationKind);
        Assert.Equal(RegistryRelationKind.Composition, kinds["CASE"]);
        Assert.Equal(RegistryRelationKind.Reference, kinds["NOTE"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-03")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Відношення_і_політика_поза_переліком_відхиляються(bool relation)
    {
        var setup = await ArrangeAsync();
        await using var db = sql.CreateContext();
        var id = setup.LookupFieldId;

        Func<Task<int>> update = relation
            ? () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.RegistryFieldDef SET RelationKind = 2 WHERE Id = {id}")
            : () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.RegistryFieldDef SET OnParentDelete = 2 WHERE Id = {id}");

        var error = await Assert.ThrowsAnyAsync<DbException>(update);
        Assert.Contains("CK_RegField_Rel", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-03")]
    public async Task Композиція_режим_коду_і_політика_зберігаються_як_задано()
    {
        var tag = Tag();
        int registryId;
        int fieldId;

        await using (var db = sql.CreateContext())
        {
            var parent = new RegistryDef(EcrCode.Create($"RC_P_{tag}"), Text("Case"), isTemporal: false);
            var child = new RegistryDef(EcrCode.Create($"RC_C_{tag}"), Text("Composition"), isTemporal: false);
            child.UseCodeMode(RegistryCodeMode.Auto);
            db.RegistryDefs.AddRange(parent, child);
            await db.SaveChangesAsync();

            var caseField = new RegistryFieldDef(child.Id, EcrCode.Create("CASE"), Text("Case"), CellDataType.Lookup, 1);
            caseField.PointTo(parent.Id);
            caseField.ComposeInto(ParentDeletePolicy.Cascade);
            db.RegistryFieldDefs.Add(caseField);
            await db.SaveChangesAsync();

            registryId = child.Id;
            fieldId = caseField.Id;
        }

        await using var read = sql.CreateContext();
        var registry = await read.RegistryDefs.AsNoTracking().SingleAsync(r => r.Id == registryId);
        var field = await read.RegistryFieldDefs.AsNoTracking().SingleAsync(f => f.Id == fieldId);

        Assert.Equal(RegistryCodeMode.Auto, registry.CodeMode);
        Assert.Null(registry.DataChangedAt);
        Assert.Equal(RegistryRelationKind.Composition, field.RelationKind);
        Assert.Equal(ParentDeletePolicy.Cascade, field.OnParentDelete);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-03")]
    public async Task Мітка_зміни_росте_з_ревізією()
    {
        var clock = new TestClock(Now);
        int registryId;

        await using (var db = sql.CreateContext())
        {
            var registry = new RegistryDef(EcrCode.Create($"RC_S_{Tag()}"), Text("Stamp probe"), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await new UnitOfWork(db, clock).SaveChangesAsync(CancellationToken.None);
            registryId = registry.Id;

            // Нова ревізія даних — мітка ставиться моментом збереження.
            clock.Advance(TimeSpan.FromMinutes(5));
            registry.BumpDataRevision();
            await new UnitOfWork(db, clock).SaveChangesAsync(CancellationToken.None);
        }

        var firstStamp = Now.AddMinutes(5);
        Assert.Equal(firstStamp, await StampAsync(registryId));

        await using (var db = sql.CreateContext())
        {
            var registry = await db.RegistryDefs.SingleAsync(r => r.Id == registryId);

            // Зміна ОПИСУ того самого рядка без ревізії даних — мітка стоїть.
            clock.Advance(TimeSpan.FromMinutes(5));
            registry.BumpDefinitionVersion();
            await new UnitOfWork(db, clock).SaveChangesAsync(CancellationToken.None);
            Assert.Equal(firstStamp, await StampAsync(registryId));

            // Знову дані — мітка переходить на новий момент.
            clock.Advance(TimeSpan.FromMinutes(5));
            registry.BumpDataRevision();
            await new UnitOfWork(db, clock).SaveChangesAsync(CancellationToken.None);
        }

        Assert.Equal(Now.AddMinutes(15), await StampAsync(registryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-03")]
    public async Task Мітку_не_ставить_збереження_без_зміни_ревізії()
    {
        var clock = new TestClock(Now);
        await using var db = sql.CreateContext();

        // Новий довідник і довідник, чиє поле змінено, — ревізія даних та сама.
        var registry = new RegistryDef(EcrCode.Create($"RC_N_{Tag()}"), Text("No stamp"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await new UnitOfWork(db, clock).SaveChangesAsync(CancellationToken.None);

        var field = new RegistryFieldDef(registry.Id, EcrCode.Create("NOTE"), Text("Note"), CellDataType.String, 1);
        db.RegistryFieldDefs.Add(field);
        registry.BumpDefinitionVersion();
        await new UnitOfWork(db, clock).SaveChangesAsync(CancellationToken.None);

        Assert.Null(await StampAsync(registry.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-03")]
    public async Task Послідовність_кодів_записів_існує_і_видає_bigint_що_росте()
    {
        await using var db = sql.CreateContext();

        var type = await db.Database.SqlQueryRaw<string>(
            """
            SELECT TYPE_NAME(s.system_type_id) AS Value
            FROM sys.sequences s
            WHERE s.object_id = OBJECT_ID(N'dic.RegistryEntryCodeSeq')
            """).SingleAsync();
        Assert.Equal("bigint", type);

        var first = await NextCodeAsync(db);
        var second = await NextCodeAsync(db);
        Assert.True(second > first, $"{second} > {first}");

        // Формат коду `E` + 9 цифр (§4.8) задовольняє `EcrCode`.
        Assert.Equal(10, EcrCode.Create("E" + second.ToString("D9", CultureInfo.InvariantCulture)).Value.Length);
    }

    // ⚠ `ToListAsync`, а не `SingleAsync`: той обгортає запит у підзапит
    // `SELECT TOP(2)`, а `NEXT VALUE FOR` у підзапиті SQL Server забороняє.
    private static async Task<long> NextCodeAsync(EcrDbContext db)
        => Assert.Single(await db.Database
            .SqlQueryRaw<long>("SELECT NEXT VALUE FOR dic.RegistryEntryCodeSeq AS Value")
            .ToListAsync());

    private async Task<DateTime?> StampAsync(int registryId)
    {
        await using var read = sql.CreateContext();
        return await read.RegistryDefs.AsNoTracking()
                         .Where(r => r.Id == registryId)
                         .Select(r => r.DataChangedAt)
                         .SingleAsync();
    }

    /// <summary>Батьківський довідник і дочірній із полями Lookup (CASE) і String (NOTE).</summary>
    private async Task<(int ChildRegistryId, int LookupFieldId, int StringFieldId)> ArrangeAsync()
    {
        var tag = Tag();
        await using var db = sql.CreateContext();

        var parent = new RegistryDef(EcrCode.Create($"RC_P_{tag}"), Text("Case"), isTemporal: false);
        var child = new RegistryDef(EcrCode.Create($"RC_C_{tag}"), Text("Composition"), isTemporal: false);
        db.RegistryDefs.AddRange(parent, child);
        await db.SaveChangesAsync();

        var caseField = new RegistryFieldDef(child.Id, EcrCode.Create("CASE"), Text("Case"), CellDataType.Lookup, 1);
        caseField.PointTo(parent.Id);
        var note = new RegistryFieldDef(child.Id, EcrCode.Create("NOTE"), Text("Note"), CellDataType.String, 2);
        db.RegistryFieldDefs.AddRange(caseField, note);
        await db.SaveChangesAsync();

        return (child.Id, caseField.Id, note.Id);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
