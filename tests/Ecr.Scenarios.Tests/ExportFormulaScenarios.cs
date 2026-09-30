using ClosedXML.Excel;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// `V-10`: формули вивантаженої книги посилаються на комірки книги, а не на
/// <c>#REF!</c> (у книзі DOC-000009 стояло <c>=(#REF!*2)</c> і
/// <c>=(#REF!*#REF!)</c>).
/// </summary>
/// <remarks>
/// ⚠ Книга вивантажується з живого документа через HTTP і розбирається
/// ClosedXML — перевіряється файл, який відкриє людина.
/// </remarks>
[Collection("SqlServer")]
public sealed class ExportFormulaScenarios(SqlServerFixture sql)
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Finding", "V-10")]
    public async Task Формули_книги_посилаються_на_комірки_книги_а_не_на_REF()
    {
        using var app = new EcrApiFactory(sql);

        var admin = await Provisioning.AdministratorAsync(
            app,
            ImportRoundTripScenarios.Prefix,
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
                "Calculation.View", "Calculation.EditFormula", "Calculation.Recalculate",
                "Document.Export", "System.ViewHealth",
            ]);

        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(
            app, admin, ImportRoundTripScenarios.Prefix, formulaColumn: ("F", "[A] * 2"));
        admin = doc.Admin;

        var instanceId = await ImportRoundTripScenarios.TableInstanceAsync(admin.Client, doc);
        await ImportRoundTripScenarios.WriteAsync(app, admin.Client, doc, instanceId, "R1", 5m);

        var book = await ImportRoundTripScenarios.ExportAsync(app, admin.Client, doc, includeFormulas: true);

        using var workbook = new XLWorkbook(new MemoryStream(book));
        var sheet = workbook.Worksheets.First(w => w.Visibility == XLWorksheetVisibility.Visible);

        // ⛔ Жодна формула книги не несе `#REF!`: Excel показав би помилку там,
        // де в системі стоїть число.
        var broken = sheet.CellsUsed(c => c.HasFormula && c.FormulaA1.Contains("#REF!", StringComparison.Ordinal))
            .Select(c => $"{c.Address}: ={c.FormulaA1}")
            .ToList();
        Assert.True(broken.Count == 0, $"формули з #REF!: {string.Join("; ", broken)}");

        // ⚠ І формула колонки F стоїть у КОЖНОМУ рядку та посилається на A
        // ТОГО САМОГО рядка — так, як її рахує система (`Scope = Column`).
        var (a, f) = (ImportRoundTripScenarios.Column(sheet, "A"), ImportRoundTripScenarios.Column(sheet, "F"));
        foreach (var rowKey in doc.RowKeys)
        {
            var row = ImportRoundTripScenarios.Row(sheet, rowKey);
            var cell = sheet.Cell(row, f);

            Assert.True(cell.HasFormula, $"у {cell.Address} ({rowKey}.F) немає формули");
            Assert.Contains($"{XLHelper.GetColumnLetterFromNumber(a)}{row}", cell.FormulaA1, StringComparison.Ordinal);
        }
    }
}
