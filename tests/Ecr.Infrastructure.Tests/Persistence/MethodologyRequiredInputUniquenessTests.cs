// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyRequiredInputUniquenessTests.cs
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
/// Уникальність <c>(MethodologyVersionId, ColumnDefId)</c> для
/// <see cref="MethodologyRequiredInput"/> — на РЕАЛЬНОМУ SQL Server
/// (директива «обов'язкові вхідні колонки методології», §1.1).
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ (<c>D-134</c>): без <c>UQ_MethodologyRequiredInput</c> у
/// <c>MethodologyRequiredInputConfiguration</c> другий запис тієї самої пари
/// пройшов би мовчки — і адмін-панель могла б завести дві суперечливі вимоги
/// (<c>Block</c> і <c>Warn</c> на ту саму колонку версії одночасно), а
/// <c>PatchCellsHandler</c> тоді читав би непередбачувано яку з двох.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyRequiredInputUniquenessTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторна_пара_версія_колонка_відхиляється_унікальним_індексом()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

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

        var column = new ColumnDef(table.Id, EcrCode.Create($"C{tag}"), Name("column"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();

        var methodology = new Methodology(EcrCode.Create($"ITEST_{tag}"), Name("required input probe"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var methodologyVersion = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(methodologyVersion);
        await db.SaveChangesAsync();

        db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(
            methodologyVersion.Id, column.Id, RequiredInputSeverity.Block, hint: null));
        await db.SaveChangesAsync();

        // ⛔ ДРУГА вимога на ТУ САМУ пару (версія, колонка) — і саме тут
        // потрібен зелений тест на живому індексі, а не рефлексія по атрибуту:
        // атрибут можна оголосити і забути застосувати міграцію.
        db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(
            methodologyVersion.Id, column.Id, RequiredInputSeverity.Warn, hint: null));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string text)
        => new(new Dictionary<string, string> { ["en"] = text });
}
