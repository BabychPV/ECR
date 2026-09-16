// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionStoreSplitQueryTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Читання структури версії для ПУБЛІКАЦІЇ — без декартового добутку
/// (аудит 2026-09-16, §6.3).
/// </summary>
/// <remarks>
/// ⛔ Чотири СЕСТРИНСЬКІ колекції (<c>Columns</c>, <c>Rows</c>, <c>Formulas</c>,
/// <c>ValidationRules</c>) під тим самим <c>Tables</c> в ОДНОМУ нероздільному
/// запиті дають добуток замість суми: на шаблоні з десятками колонок і рядків
/// це десятки тисяч зайвих рядків по мережі на кожну публікацію. Той самий
/// дефект уже виправлений у <c>MetadataCache.LoadAsync</c> (окремі запити на
/// колекцію), але не в цьому, другому шляху до тієї самої структури.
///
/// ⚠ Доводиться КІЛЬКІСТЮ ЗАПИТІВ, а не часом: замір часу на SQLEXPRESS
/// нестабільний, а «один запит замість п'яти» — детермінована ознака того, що
/// EF склеїв колекції в добуток.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionStoreSplitQueryTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Структура_версії_читається_роздільними_запитами_а_не_добутком()
    {
        var versionId = await ArrangeAsync();

        var executed = new List<string>();
        await using var db = CreateContext(executed);

        var version = await new TemplateVersionStore(db)
            .GetWithStructureAsync(versionId, CancellationToken.None);

        // Структура справді прочитана — інакше тест доводив би порожнечу.
        var table = Assert.Single(Assert.Single(version.Sheets).Tables);
        Assert.Equal(4, table.Columns.Count);
        Assert.Equal(3, table.Rows.Count);

        // ⛔ Головне твердження: EF розбив читання на ОКРЕМІ запити на колекцію.
        // Нероздільний запит був би РІВНО один, і в ньому — добуток
        // 4 колонки × 3 рядки × формули × правила замість 4 + 3 + … .
        Assert.True(
            executed.Count > 1,
            $"Структура прочитана одним запитом ({executed.Count}) — це декартів добуток.");

        // І жоден із запитів не з'єднує ДВІ сестринські колекції між собою —
        // саме таке з'єднання і є добутком.
        Assert.DoesNotContain(
            executed,
            sql => sql.Contains("cfg.ColumnDef", StringComparison.Ordinal)
                   && sql.Contains("cfg.RowDef", StringComparison.Ordinal));
    }

    private async Task<int> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        await using var db = CreateContext([]);

        var template = new Template(EcrCode.Create($"SQ{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync(CancellationToken.None);

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync(CancellationToken.None);

        // Чотири колонки й три рядки: добуток (12 рядків на одну таблицю)
        // відрізняється від суми (7) достатньо, щоб різниця була не випадковою.
        for (var i = 1; i <= 4; i++)
        {
            db.ColumnDefs.Add(new ColumnDef(
                table.Id, EcrCode.Create($"C{i}"), Name($"Column {i}"), i, CellDataType.Decimal));
        }

        for (var i = 1; i <= 3; i++)
        {
            db.RowDefs.Add(new RowDef(
                table.Id, RowKey.Create($"700100{i}"), i, Name($"Row {i}"), RowKind.Item));
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return version.Id;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext CreateContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .LogTo(executed.Add, [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted])
            .Options);
}
