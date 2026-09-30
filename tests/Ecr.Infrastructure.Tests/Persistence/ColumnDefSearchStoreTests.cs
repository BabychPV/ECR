// tests/Ecr.Infrastructure.Tests/Persistence/ColumnDefSearchStoreTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Пошук колонки для прив'язки методології — фільтр у SQL ДО обмеження
/// (F-03, четвертий раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Відтворено на стенді: 5991 колонка, пошук <c>EMISSION</c> → <c>[]</c>.
/// Сховище брало перші 5000 за абеткою коду і лише потім шукало підрядок, тож
/// колонка з кодом «пізніше» за п'ятитисячну не знаходилася НІКОЛИ.
/// </remarks>
[Collection("SqlServer")]
public sealed class ColumnDefSearchStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// Мутація: повернути в <c>ColumnDefSearchStore.SearchAsync</c> фільтр після
    /// <c>Take(5000)</c> (прибрати гілку <c>if (!string.IsNullOrEmpty(trimmed))</c>
    /// над запитом) — 5000 колонок <c>AAA…</c> забирають усю стелю, і ціль не
    /// знаходиться.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_поза_першими_п_ятьма_тисячами_знаходиться_за_кодом()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var tableId = await TableAsync(tag);

        await using (var db = CreateContext())
        {
            // 5000 колонок, що за абеткою стоять ПЕРЕД ціллю, — одним INSERT.
            await db.Database.ExecuteSqlAsync($$"""
                INSERT INTO cfg.ColumnDef (TableDefId, Code, HeaderL10n, Ordinal, DataType)
                SELECT TOP (5000) {{tableId}},
                       CONCAT(N'AAA_R4M_', RIGHT(CONCAT(N'0000', ROW_NUMBER() OVER (ORDER BY (SELECT 1))), 5)),
                       N'{"en":"filler"}',
                       ROW_NUMBER() OVER (ORDER BY (SELECT 1)),
                       2
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                """);

            db.ColumnDefs.Add(new ColumnDef(
                tableId, EcrCode.Create($"ZZ_R4M_{tag}_EMISSION"), Name("Emission"), 9000, CellDataType.Calculated));
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();

        try
        {
            var found = await new ColumnDefSearchStore(read).SearchAsync($"{tag}_EMISSION", 50, CancellationToken.None);

            var hit = Assert.Single(found);
            Assert.Equal($"ZZ_R4M_{tag}_EMISSION", hit.Code);
        }
        finally
        {
            // Бази `EcrTest_*` живуть між прогонами: 5000 заповнювачів на кожен
            // прогін роздували б `cfg.ColumnDef` для всіх інших тестів.
            await read.Database.ExecuteSqlAsync(
                $"DELETE FROM cfg.ColumnDef WHERE TableDefId = {tableId} AND Code LIKE N'AAA[_]R4M[_]%'");
        }
    }

    /// <remarks>
    /// ⚠ Заголовок пишеться <c>LocalizedText.ToJson</c>, тобто кирилиця лежить
    /// в базі екранованою (<c>В…</c>). Мутація: прибрати шаблон
    /// <c>jsonLike</c> — кириличний заголовок не знаходиться.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_знаходиться_за_кириличним_заголовком()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var tableId = await TableAsync(tag);
        var header = $"Викиди {tag}";

        await using (var db = CreateContext())
        {
            db.ColumnDefs.Add(new ColumnDef(
                tableId, EcrCode.Create($"C_{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["ru"] = header }), 1, CellDataType.Decimal));
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();

        var found = await new ColumnDefSearchStore(read).SearchAsync(header, 50, CancellationToken.None);

        Assert.Equal($"C_{tag}", Assert.Single(found).Code);
    }

    private async Task<int> TableAsync(string tag)
    {
        await using var db = CreateContext();

        var template = new Template(EcrCode.Create($"TPL{tag}"), Name("template"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"SH{tag}"), Name("sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"TBL{tag}"), Name("table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        return table.Id;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string text)
        => new(new Dictionary<string, string> { ["en"] = text });
}
