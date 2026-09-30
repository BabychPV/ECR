// tests/Ecr.Infrastructure.Tests/Persistence/RegistryUseSchemaTests.cs
using System.Data.Common;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>RK04RegistryUseAndRunAsOf</c> на РЕАЛЬНОМУ SQL Server (RT-05,
/// FEATURE-REGISTRY-TABLES §3.2, §3.6; <c>D-158</c>): таблиця
/// <c>cfg.RegistryUse</c> і момент знімка <c>calc.CalculationRun.RegistryAsOfUtc</c>.
/// </summary>
/// <remarks>
/// ⛔ Закритість виду перевіряється вставкою ПОВЗ домен (сирий SQL): фабрики
/// <see cref="RegistryUse"/> невідомого виду й не створять, тож перевірка через
/// сутність довела б лише домен. База тримає інваріант для скриптів та імпорту.
///
/// Мутаційні докази (RT-05, §9.2): прибрати <c>CK_RegUse_Kind</c> з міграції й
/// конфігурації — червоніє <see cref="Невідомий_вид_використання_відхиляється"/>;
/// не ставити <c>RegistryAsOfUtc</c> на старті прогону — червоніє
/// <see cref="Момент_знімка_прогону_зберігається_з_точністю_до_мілісекунди"/>
/// (і доменний <c>Прогін_фіксує_момент_знімка</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryUseSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 30, 15, 123, DateTimeKind.Utc);

    // Ключ за довідником + INCLUDE для «Де використано»; ключ за джерелом — для
    // переписування ребер під час публікації. `+` — INCLUDE-стовпець.
    private static readonly string[] ExpectedIndexColumns =
    [
        "IX_RegistryUse_Registry:+FieldPath", "IX_RegistryUse_Registry:+FormulaCode",
        "IX_RegistryUse_Registry:+SourceId", "IX_RegistryUse_Registry:+SourceKind",
        "IX_RegistryUse_Registry:RegistryDefId",
        "IX_RegistryUse_Source:SourceId", "IX_RegistryUse_Source:SourceKind",
    ];

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-05")]
    [InlineData(3)]
    [InlineData(255)]
    public async Task Невідомий_вид_використання_відхиляється(int kind)
    {
        var registryId = await RegistryAsync();
        await using var db = sql.CreateContext();

        var error = await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO cfg.RegistryUse (SourceKind, SourceId, RegistryDefId) VALUES ({kind}, 1, {registryId})"));
        Assert.Contains("CK_RegUse_Kind", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-05")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Відомий_вид_вставляється_повз_домен(int kind)
    {
        var registryId = await RegistryAsync();
        await using var db = sql.CreateContext();

        // Та сама вставка, що й вище, — CHECK не ширший за перелік видів і не вужчий.
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO cfg.RegistryUse (SourceKind, SourceId, RegistryDefId) VALUES ({kind}, 1, {registryId})");

        Assert.Equal(1, rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-05")]
    public async Task Використання_зберігається_як_задано_і_читається_за_довідником()
    {
        var registryId = await RegistryAsync();

        await using (var db = sql.CreateContext())
        {
            db.RegistryUses.AddRange(
                RegistryUse.ForTemplateFormula(101, registryId, "COMPONENT.MW"),
                RegistryUse.ForMethodologyFormula(202, "EF_CO2", registryId, fieldPath: null),
                RegistryUse.ForRegistryRule(303, registryId, "MW"));
            await db.SaveChangesAsync();
        }

        await using var read = sql.CreateContext();
        var uses = await read.RegistryUses.AsNoTracking()
                             .Where(u => u.RegistryDefId == registryId)
                             .OrderBy(u => u.SourceKind)
                             .Select(u => new { u.SourceKind, u.SourceId, u.FormulaCode, u.FieldPath })
                             .ToListAsync();

        Assert.Equal(
            new[]
            {
                new { SourceKind = RegistryUse.TemplateFormulaSource, SourceId = 101, FormulaCode = (string?)null, FieldPath = (string?)"COMPONENT.MW" },
                new { SourceKind = RegistryUse.MethodologyVersionSource, SourceId = 202, FormulaCode = (string?)"EF_CO2", FieldPath = (string?)null },
                new { SourceKind = RegistryUse.RegistryRuleSource, SourceId = 303, FormulaCode = (string?)null, FieldPath = (string?)"MW" },
            },
            uses);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-05")]
    public async Task Використання_не_може_вказувати_на_неіснуючий_довідник()
    {
        await using var db = sql.CreateContext();

        var error = await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO cfg.RegistryUse (SourceKind, SourceId, RegistryDefId) VALUES (0, 1, -1)"));
        Assert.Contains("FK_RegUse_Def", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-05")]
    public async Task Індекси_за_довідником_і_за_джерелом_існують_з_потрібним_ключем()
    {
        await using var db = sql.CreateContext();

        var columns = await db.Database.SqlQueryRaw<string>(
            """
            SELECT i.name + ':' + CASE ic.is_included_column WHEN 1 THEN '+' ELSE '' END + c.name AS Value
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'cfg.RegistryUse') AND i.name LIKE N'IX[_]%'
            """).ToListAsync();

        Assert.Equal(ExpectedIndexColumns, columns.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-05")]
    public async Task Момент_знімка_прогону_зберігається_з_точністю_до_мілісекунди()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        long runId;

        await using (var db = chain.CreateContext())
        {
            // Частка мілісекунди на старті: у базі й у пам'яті має лишитися той
            // самий момент, інакше replay читав би AS OF іншої миті.
            var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now.AddTicks(4_321));
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await using var read = chain.CreateContext();
        var stored = await read.CalculationRuns.AsNoTracking()
                               .Where(r => r.Id == runId)
                               .Select(r => r.RegistryAsOfUtc)
                               .SingleAsync();

        Assert.Equal(Now, stored);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-05")]
    public async Task Прогін_без_моменту_знімка_допустимий_як_рядок_до_міграції()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        long runId;

        await using (var db = chain.CreateContext())
        {
            var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;

            // Рядок, що існував до RK04, має NULL: стовпець мусить це дозволяти (§3.5).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE calc.CalculationRun SET RegistryAsOfUtc = NULL WHERE Id = {runId}");
        }

        await using var read = chain.CreateContext();
        Assert.Null(await read.CalculationRuns.AsNoTracking()
                              .Where(r => r.Id == runId)
                              .Select(r => r.RegistryAsOfUtc)
                              .SingleAsync());
    }

    private async Task<int> RegistryAsync()
    {
        await using var db = sql.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"RU_{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Use probe" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry.Id;
    }
}
