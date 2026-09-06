using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Граф залежностей формул у базі (ФВ-9.4).
/// </summary>
/// <remarks>
/// ⛔ Таблиця <c>cfg.FormulaDependency</c> не наповнювалася нічим, і сама
/// сутність цього не дозволяла: конструктор приймав лише вид і порядок, а
/// решта полів не мала сетерів. Наслідок був найтихішим із можливих — граф
/// порожній, каскадний перерахунок не бачить похідних комірок, числа
/// лишаються старими без жодної помилки (<c>A7-63</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaDependencyStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Залежності_зберігаються_і_читаються_назад()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202612, ct: CancellationToken.None);

        var formulaId = await AddFormulaAsync(builder, doc);

        await using (var db = builder.CreateContext())
        {
            await new TemplateVersionStore(db).ReplaceFormulaDependenciesAsync(
                doc.TemplateVersionId,
                [
                    FormulaDependency.ForFormula(
                        formulaId, dependsOnKind: 0, doc.TableDefId, "R1", doc.ColumnDefIds[1],
                        filterJson: null, periodOffset: null, sortOrder: 0),
                    FormulaDependency.ForFormula(
                        formulaId, dependsOnKind: 0, doc.TableDefId, "R2", doc.ColumnDefIds[1],
                        filterJson: null, periodOffset: null, sortOrder: 1),
                ],
                CancellationToken.None);

            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = builder.CreateContext();
        var saved = await new TemplateVersionStore(read)
            .ListFormulaDependenciesAsync(doc.TemplateVersionId, CancellationToken.None);

        Assert.Equal(2, saved.Count);
        Assert.Equal(["R1", "R2"], saved.Select(d => d.RowKey));
        Assert.All(saved, d => Assert.Equal(formulaId, d.FormulaDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Повторна_публікація_замінює_граф_а_не_дописує()
    {
        // ⛔ Публікація фіксує граф версії ЦІЛКОМ. «Доліпити» до попереднього
        // набору означало б тримати в таблиці залежності формул, яких у
        // версії вже немає, — і перераховувати від комірок, від яких ніхто
        // більше не залежить.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202612, ct: CancellationToken.None);

        var formulaId = await AddFormulaAsync(builder, doc);

        for (var pass = 0; pass < 2; pass++)
        {
            await using var db = builder.CreateContext();

            await new TemplateVersionStore(db).ReplaceFormulaDependenciesAsync(
                doc.TemplateVersionId,
                [
                    FormulaDependency.ForFormula(
                        formulaId, dependsOnKind: 0, doc.TableDefId, "R1", doc.ColumnDefIds[1],
                        filterJson: null, periodOffset: null, sortOrder: 0),
                ],
                CancellationToken.None);

            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var check = builder.CreateContext();
        var saved = await new TemplateVersionStore(check)
            .ListFormulaDependenciesAsync(doc.TemplateVersionId, CancellationToken.None);

        Assert.Single(saved);
    }

    /// <summary>Формула колонки в таблиці документа; повертає її ідентифікатор.</summary>
    private static async Task<int> AddFormulaAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();

        var formula = new FormulaDef(
            doc.TableDefId, FormulaScope.Column, "[C2] * 2", ExpressionDialect.Template);

        typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!
            .SetValue(formula, doc.ColumnDefIds[2]);

        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync(CancellationToken.None);

        return formula.Id;
    }
}
