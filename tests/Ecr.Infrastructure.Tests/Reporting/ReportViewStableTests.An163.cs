// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableTests.An163.cs
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// AN-163 (Land T3_00 на 1.0.14.0, Аудит-2 10.11: rpt-вʼюхи 7-19,6 с при бюджеті 5 с). Форма вʼюх, що тримає бюджет:
/// <list type="number">
/// <item>стабільна вʼюха читає прив'язки методологій лише ВЛАСНОЇ версії рядка (як зріз API і вʼюха <c>_vN</c>), а не
/// кожної версії групи (Land, 9 версій: 88 032 результати множилися до 704 256 рядків);</item>
/// <item>стабільна вʼюха, де код має кілька ColumnDefId (кілька версій), агрегує pc за мапою слотів <c>rpt.ViewSlot</c>,
/// а не IN-списком у кожному з ~260 агрегатів (Land: stable Y 8-8,7 с -> 2,3-2,9 с, ті самі значення);</item>
/// <item>кожне читання комірок рядка (pc/sc) - seek по ОБОХ ключах кластерного індексу <c>doc.CellValue</c>
/// (<c>FORCESEEK (1 (PeriodKey, TableRowId))</c>): план із seek лише по PeriodKey перечитував партицію періоду на кожен
/// рядок (Аудит-2: 588 виконань / 2,07 млн читань на 788 рядків, CPU 55 с).</item>
/// </list>
/// </summary>
/// <remarks>
/// Мутації (докази, AN-163): (1) у <c>rpt.usp_GenerateStableViews</c> повернути <c>bb.TableDefId IN (' + @tids + N')</c> -
/// рядок версії 2 отримує результат прив'язки версії 1 (10,5 замість NULL), червоне; (2) прибрати гілку
/// <c>(SELECT COUNT(*) FROM @slot) > @n</c> - мапа слотів стабільної вʼюхи порожня, червоне; (3) прибрати
/// <c>WITH (FORCESEEK ...)</c> з pc - червоне на перевірці тексту.
/// </remarks>
public sealed partial class ReportViewStableTests
{
    private const string CellSeek = "FROM doc.CellValue AS x WITH (FORCESEEK (1 (PeriodKey, TableRowId)))";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Стабільна_вʼюха_бере_привʼязки_лише_версії_рядка_агрегує_за_слотами_і_читає_комірки_seekом()
    {
        var (templateId, tpl) = await NewTemplateAsync();
        var v1 = (await AddVersionAsync(templateId, "1", [new("S", "T", [("A", CellDataType.String), ("K", CellDataType.Calculated)])]))["S/T"];
        var v2 = (await AddVersionAsync(templateId, "2", [new("S", "T", [("A", CellDataType.String), ("K", CellDataType.Calculated)])]))["S/T"];
        var p = await NewProjectAsync(v1.VersionId, 202609, 202610);
        await SeedAsync(p, 202609, v1, ("R1", 1, new() { ["A"] = "a9" }));
        await SeedAsync(p, 202610, v2, ("R1", 1, new() { ["A"] = "a10" }));

        // Прив'язка K лише у версії 1; актуальні прогони документа в обох місяцях дають результат для рядка R1.
        await SeedCurrentResultsAsync(p, v1.TableId, v1.Cols["K"], (202609, 9.5m), (202610, 10.5m));

        await GenerateViaProcedureAsync(v1.VersionId, routing: true);
        await GenerateViaProcedureAsync(v2.VersionId, routing: true);
        await ExecStableAsync(v2.VersionId, routing: true);
        var stable = StableName(tpl, "S", "T");
        var view1 = VersionedName(tpl, "S", "T", "1");
        var view2 = VersionedName(tpl, "S", "T", "2");

        // (1) Рядок версії 1 - число методології; рядок версії 2 (прив'язки немає) - NULL, як у його вʼюсі _v2 і в зрізі API.
        Assert.Equal(
            [(202609, "a9", (decimal?)9.5m), (202610, "a10", null)],
            (await RowsAsync(stable, p.ProjectId, "_PeriodKey", "A", "K"))
                .Select(r => ((int)r[0]!, (string)r[1]!, (decimal?)r[2])).ToList());
        Assert.Equal([(decimal?)9.5m], (await RowsAsync(view1, p.ProjectId, "K")).Select(r => (decimal?)r[0]).ToList());
        Assert.Equal([(decimal?)null], (await RowsAsync(view2, p.ProjectId, "K")).Select(r => (decimal?)r[0]).ToList());

        // (2) Код із кількома ColumnDefId (дві версії) - агрегація за мапою слотів: у мапі всі Id обох версій.
        var slots = (await ScalarRowsAsync($"SELECT ColumnDefId FROM rpt.ViewSlot WHERE ViewName = N'{stable}' ORDER BY ColumnDefId"))
            .Select(r => (int)r[0]!).ToList();
        Assert.Equal(new[] { v1.Cols["A"], v1.Cols["K"], v2.Cols["A"], v2.Cols["K"] }.Order().ToList(), slots);
        Assert.Contains("JOIN rpt.ViewSlot AS m", await DefinitionAsync(stable), StringComparison.Ordinal);

        // (3) Кожне читання комірок рядка (pc/sc) - seek по обох ключах, у стабільній і в обох _vN.
        foreach (var view in new[] { stable, view1, view2 })
        {
            var text = await DefinitionAsync(view);
            var reads = Regex.Count(text, @"FROM doc\.CellValue AS x\b");
            Assert.True(reads > 0, $"{view}: немає читання комірок рядка");
            Assert.Equal(reads, Regex.Count(text, Regex.Escape(CellSeek)));
        }
    }

    /// <summary>Методологія, прив'язка (лише до колонки <paramref name="columnId"/>), актуальний документний прогін і результат R1 на місяць.</summary>
    private async Task SeedCurrentResultsAsync(Project p, int tableId, int columnId, params (int Period, decimal Value)[] results)
    {
        int versionId, unitId;
        var runs = new List<(int Period, long RunId)>();
        await using (var db = sql.CreateContext())
        {
            var methodology = new Methodology(EcrCode.Create("A163" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "an163" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);
            db.CalculationBindings.Add(new CalculationBinding(tableId, columnId, methodology.Id, "OUT", "{}"));
            await db.SaveChangesAsync();
            versionId = version.Id;
            unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

            foreach (var (period, _) in results)
            {
                var run = new CalculationRun(p.ProjectId, period, triggeredByUserId: 1, Now, p.DocumentId);
                db.CalculationRuns.Add(run);
                await db.SaveChangesAsync();
                run.Complete("Completed", Now, profileJson: null, errorMessage: null);
                run.MakeCurrent();
                await db.SaveChangesAsync();
                runs.Add((period, run.Id));
            }
        }

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        foreach (var (period, value) in results)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO calc.CalculationResult
                    (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, Kind)
                VALUES (@pk, NEXT VALUE FOR calc.CalculationResultSeq, @run, @ver, @doc, N'R1', N'OUT', @v, @unit, 0);
                """;
            command.Parameters.AddWithValue("@pk", period);
            command.Parameters.AddWithValue("@run", runs.Single(r => r.Period == period).RunId);
            command.Parameters.AddWithValue("@ver", versionId);
            command.Parameters.AddWithValue("@doc", p.DocumentId);
            command.Parameters.AddWithValue("@v", value);
            command.Parameters.AddWithValue("@unit", unitId);
            await command.ExecuteNonQueryAsync();
        }
    }
}
