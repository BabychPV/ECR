// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneFormulaTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Клон версії шаблону з формулами — на <b>реальному</b> SQL Server (V-05).
/// </summary>
/// <remarks>
/// ⛔ V-05 (UX-прохід 2026-09-24, третій раунд): «Clone version» / «New
/// version» для будь-якої версії з хоч однією формулою давали <c>500</c>.
/// <c>TemplateVersionCloner.Prepare</c> обнуляв <c>ColumnDefId</c>/<c>RowDefId</c>
/// формул, а перев'язував їх лише ПІСЛЯ першого <c>SaveChanges</c> — тобто
/// перша ж вставка писала формулу з <c>NULL</c> в обох ключах і порушувала
/// <c>CK_Formula_Scope</c>. Структуру опублікованого шаблону з формулами
/// неможливо було змінити взагалі: клон — єдиний шлях (ФВ-7.1).
///
/// ⚠ Тест на справжній базі навмисно: перевірка <c>CK_Formula_Scope</c>
/// живе лише в SQL Server, і InMemory-провайдер цей дефект не побачив би.
///
/// ⚠ Заразом — правило валідації рівня колонки: до виправлення клон
/// зберігав його <c>ColumnDefId</c> як є, тобто правило нової чернетки
/// мовчки посилалося на колонку ДЖЕРЕЛА.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneFormulaTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_версії_з_формулами_колонки_рядка_і_комірки_перев_язує_їх_на_власні_колонки_й_рядки()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: ct);

        await using (var setup = builder.CreateContext())
        {
            var column = new FormulaDef(doc.TableDefId, FormulaScope.Column, "[R1] + 1", ExpressionDialect.Template);
            column.AssignColumn(doc.ColumnDefIds[1]);

            var row = new FormulaDef(doc.TableDefId, FormulaScope.Row, "[R1] + [R2]", ExpressionDialect.Template);
            row.AssignRow(doc.RowDefIds[3]);

            // Область Cell не має доменної точки входу (AssignColumn/AssignRow
            // перевіряють область), тому обидва ключі — рефлексією, як у
            // `TemplateBuilder` для готових знімків.
            var cell = new FormulaDef(doc.TableDefId, FormulaScope.Cell, "1", ExpressionDialect.Template);
            typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!.SetValue(cell, doc.ColumnDefIds[2]);
            typeof(FormulaDef).GetProperty(nameof(FormulaDef.RowDefId))!.SetValue(cell, doc.RowDefIds[0]);

            setup.FormulaDefs.AddRange(column, row, cell);
            setup.ValidationRules.Add(new ValidationRule(
                doc.TableDefId, EcrCode.Create("RULE1"), ValidationSeverity.Error, 0, "[C] >= 0",
                new LocalizedText(new Dictionary<string, string> { ["en"] = "non-negative" }),
                doc.ColumnDefIds[1]));
            await setup.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, "2.0.0.1", 1, new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();

        var sourceTable = await read.TableDefs.AsNoTracking()
            .Include(t => t.Columns).Include(t => t.Rows)
            .FirstAsync(t => t.Id == doc.TableDefId, ct);
        var cloneTable = await read.TableDefs.AsNoTracking()
            .Include(t => t.Columns).Include(t => t.Rows)
            .Include(t => t.Formulas).Include(t => t.ValidationRules)
            .Where(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId))
            .SingleAsync(ct);

        Assert.NotEqual(sourceTable.Id, cloneTable.Id);
        Assert.Equal(3, cloneTable.Formulas.Count);

        string? ColumnCode(TableDef t, int? id) => t.Columns.FirstOrDefault(c => c.Id == id)?.Code;
        string? RowKeyOf(TableDef t, int? id) => t.Rows.FirstOrDefault(r => r.Id == id)?.RowKeyValue;

        var columnFormula = cloneTable.Formulas.Single(f => f.Scope == FormulaScope.Column);
        Assert.Contains(cloneTable.Columns, c => c.Id == columnFormula.ColumnDefId);
        Assert.Equal(ColumnCode(sourceTable, doc.ColumnDefIds[1]), ColumnCode(cloneTable, columnFormula.ColumnDefId));

        var rowFormula = cloneTable.Formulas.Single(f => f.Scope == FormulaScope.Row);
        Assert.Contains(cloneTable.Rows, r => r.Id == rowFormula.RowDefId);
        Assert.Equal(RowKeyOf(sourceTable, doc.RowDefIds[3]), RowKeyOf(cloneTable, rowFormula.RowDefId));

        var cellFormula = cloneTable.Formulas.Single(f => f.Scope == FormulaScope.Cell);
        Assert.Equal(ColumnCode(sourceTable, doc.ColumnDefIds[2]), ColumnCode(cloneTable, cellFormula.ColumnDefId));
        Assert.Equal(RowKeyOf(sourceTable, doc.RowDefIds[0]), RowKeyOf(cloneTable, cellFormula.RowDefId));

        // Правило валідації колонки — на колонку КЛОНУ, не джерела.
        var rule = Assert.Single(cloneTable.ValidationRules);
        Assert.Contains(cloneTable.Columns, c => c.Id == rule.ColumnDefId);
        Assert.Equal(ColumnCode(sourceTable, doc.ColumnDefIds[1]), ColumnCode(cloneTable, rule.ColumnDefId));

        // Джерело не зачеплене: три формули й досі на своїх колонках.
        var sourceFormulas = await read.FormulaDefs.AsNoTracking()
            .Where(f => f.TableDefId == doc.TableDefId).ToListAsync(ct);
        Assert.Equal(3, sourceFormulas.Count);
        Assert.Contains(sourceFormulas, f => f.ColumnDefId == doc.ColumnDefIds[1] && f.Scope == FormulaScope.Column);
    }
}
