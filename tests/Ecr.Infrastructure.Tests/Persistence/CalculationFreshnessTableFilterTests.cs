// tests/Ecr.Infrastructure.Tests/Persistence/CalculationFreshnessTableFilterTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Фільтр таблиць у <see cref="MethodologyStore.GetCalculationFreshnessAsync"/>
/// на РЕАЛЬНОМУ SQL Server (залишок F-05).
/// </summary>
/// <remarks>
/// ⛔ Подання аркуша питає свіжість лише по таблицях свого аркуша. Фільтр
/// живе в сирому SQL (<c>aud.CellChange</c> → <c>cfg.ColumnDef.TableDefId</c>,
/// перелік через <c>OPENJSON</c>) — підставний порт у тестах обробника цього
/// не перевіряє зовсім, тому доказ тут, на справжній базі.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationFreshnessTableFilterTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F-05")]
    public async Task Зміна_входу_в_таблиці_іншого_аркуша_не_робить_застарілими_таблиці_цього()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var periodKey = document.PeriodKey.Value;
        var tableA = document.TableDefId;

        await using var db = chain.CreateContext();

        // Другий аркуш тієї самої версії шаблону — зі своєю таблицею й колонкою.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var sheetB = new SheetDef(document.TemplateVersionId, EcrCode.Create($"SHB{tag}"), Name("Sheet B"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync(CancellationToken.None);

        var tableDefB = new TableDef(
            sheetB.Id, EcrCode.Create($"TBB{tag}"), Name("Table B"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableDefB);
        await db.SaveChangesAsync(CancellationToken.None);

        var columnB = new ColumnDef(tableDefB.Id, EcrCode.Create($"CB{tag}"), Name("Col B"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(columnB);
        await db.SaveChangesAsync(CancellationToken.None);
        var tableB = tableDefB.Id;

        // Актуальний прогін із числом цього документа.
        var methodology = new Methodology(EcrCode.Create($"MFR_{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var run = new CalculationRun(document.ProjectId, periodKey, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(CancellationToken.None);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {version.Id},
                    {periodKey}, {document.DocumentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
            """);

        // Вхід змінився ПІСЛЯ прогону — лише в таблиці аркуша B.
        var changedAt = Now.AddHours(1);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES ({changedAt}, {periodKey}, {document.DocumentId}, 1, N'R1', {columnB.Id},
                    N'1', N'2', 1, N'UserEdit', 0)
            """);

        var store = new MethodologyStore(db);

        // Увесь документ (дисплей F-05) — застаріло, як і було.
        var whole = await store.GetCalculationFreshnessAsync(document.DocumentId, periodKey, null, CancellationToken.None);
        Assert.True(whole.IsStale);
        Assert.NotNull(whole.CalculatedAt);

        // ⛔ Головна перевірка: таблиця аркуша A — свіжа, хоча документ застарів.
        var onlyA = await store.GetCalculationFreshnessAsync(document.DocumentId, periodKey, [tableA], CancellationToken.None);
        Assert.False(onlyA.IsStale);
        Assert.NotNull(onlyA.CalculatedAt);

        // Контроль: сама таблиця B і набір, що її містить, — застарілі.
        var onlyB = await store.GetCalculationFreshnessAsync(document.DocumentId, periodKey, [tableB], CancellationToken.None);
        Assert.True(onlyB.IsStale);

        var both = await store.GetCalculationFreshnessAsync(document.DocumentId, periodKey, [tableA, tableB], CancellationToken.None);
        Assert.True(both.IsStale);

        // Порожній перелік — жодна зміна не рахується.
        var none = await store.GetCalculationFreshnessAsync(document.DocumentId, periodKey, [], CancellationToken.None);
        Assert.False(none.IsStale);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
