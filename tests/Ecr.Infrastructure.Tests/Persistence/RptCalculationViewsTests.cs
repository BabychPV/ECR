using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ P-1124-3 (D-NEW; людина 24.11: «зауваж що дані розрахунків методологія тощо мають бути доступні для ssrs»): сирі вʼюхи
/// <c>rpt.*</c> для результатів і проблем розрахунку, методологій, проєктів, періодів і документів — частина публічного
/// контракту <c>rpt.*</c> (D-53), як і <c>rpt.v_DocumentCells</c>.
/// </summary>
/// <remarks>
/// ⛔ Два сторожі: (1) склад колонок за порядком — колонки лише ДОДАЮТЬСЯ в кінець (зміна імені чи порядку ламає RDL
/// замовника мовчки); (2) вʼюхи компілюються в розгорнутій базі і віддають рядки на засіяних даних, причому результати й
/// проблеми — лише АКТУАЛЬНОГО прогону.
/// МУТАЦІЙНИЙ ДОКАЗ: прибрати в <c>rpt.v_CalculationResult</c> умову <c>run.Status = N'Current'</c> — тест даних червоніє
/// (рядків заміщеного прогону стає вдвічі більше); перейменувати колонку будь-якої з семи вʼюх — червоніє контракт.
/// </remarks>
[Collection("SqlServer")]
public sealed class RptCalculationViewsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Контракт: вʼюха → імена колонок за порядком.</summary>
    public static TheoryData<string, string[]> Contracts => new()
    {
        { "v_Project", ["ProjectId", "ProjectCode", "Name_en", "Name_ru", "Name_kz", "Year", "PeriodStart", "PeriodEnd",
            "TimeZoneId", "Status", "StatusName"] },
        { "v_Period", ["ProjectId", "ProjectCode", "PeriodKey", "Sequence", "PeriodStart", "PeriodEnd", "State", "StateName"] },
        { "v_Document", ["DocumentId", "DocumentKey", "Name_en", "Name_ru", "Name_kz", "ProjectId", "ProjectCode",
            "TemplateCode", "CreatedAt", "ModifiedAt"] },
        { "v_MethodologyVersion", ["MethodologyId", "MethodologyCode", "Name_en", "Name_ru", "Name_kz", "MethodologyGroup",
            "IsActive", "MethodologyVersionId", "Version", "EffectiveFrom", "PublishedAt", "Status", "StatusName", "Engine",
            "EngineName", "IsLatestPublished"] },
        { "v_MethodologyOutput", ["MethodologyId", "MethodologyCode", "MethodologyVersionId", "MethodologyVersion",
            "OutputCode", "Ordinal", "IsPerSubstance", "UnitId", "UnitCode"] },
        { "v_CalculationResult", ["ResultId", "ProjectId", "ProjectCode", "DocumentId", "DocumentKey", "PeriodKey",
            "MethodologyId", "MethodologyCode", "MethodologyVersionId", "MethodologyVersion", "OutputCode", "SourceRowKey",
            "SubstanceEntryId", "Value", "UnitId", "UnitCode", "Kind", "KindName", "CalculationRunId", "RunFinishedAt"] },
        { "v_CalculationIssue", ["IssueId", "ProjectId", "ProjectCode", "DocumentId", "DocumentKey", "PeriodKey", "TableCode",
            "SheetCopyId", "RowKey", "RowNumber", "Kind", "MethodologyId", "MethodologyCode", "ErrorCode", "StepCode",
            "AffectedCount", "DetectedAt", "CalculationRunId"] },
    };

    [Theory]
    [MemberData(nameof(Contracts))]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Контракт_вʼюхи_колонки_за_порядком(string view, string[] expected)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name FROM sys.columns AS c
             WHERE c.object_id = OBJECT_ID(QUOTENAME(N'rpt') + N'.' + QUOTENAME(@view))
             ORDER BY c.column_id;
            """;
        command.Parameters.AddWithValue("@view", view);
        var actual = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                actual.Add(reader.GetString(0));
            }
        }

        // Порожньо — вʼюхи немає в розгорнутій базі (05-rpt-views.sql не накочено або вʼюху прибрали).
        Assert.True(actual.Count > 0, $"Вʼюхи rpt.{view} у базі немає.");
        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Вʼюхи_віддають_засіяні_дані_а_результати_й_проблеми_лише_актуального_прогону()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var methodologyCode = $"RPTV_{tag}";

        int methodologyId;
        int versionId;
        long currentRunId;
        await using (var db = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create(methodologyCode), new LocalizedText(new Dictionary<string, string> { ["en"] = "SSRS view" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            methodologyId = methodology.Id;

            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
            versionId = version.Id;

            var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

            var superseded = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, Now, doc.DocumentId);
            superseded.Complete(CalculationRun.SupersededStatus, Now, null, null);
            var current = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, Now.AddMinutes(1), doc.DocumentId);
            current.Complete(CalculationRun.SupersededStatus, Now.AddMinutes(1), null, null);
            db.CalculationRuns.AddRange(superseded, current);
            await db.SaveChangesAsync();
            current.MakeCurrent();
            await db.SaveChangesAsync();
            currentRunId = current.Id;

            // Опубліковано (як після публікації) і вихід методології з одиницею.
            await db.Database.ExecuteSqlAsync($"""
                UPDATE calc.MethodologyVersion SET Status = 1, PublishedAt = {Now}, EffectiveFrom = '2026-01-01', ChangeReason = N'SSRS view test'
                 WHERE Id = {versionId};
                INSERT INTO calc.MethodologyOutput (MethodologyVersionId, Code, UnitId, Ordinal, IsPerSubstance)
                VALUES ({versionId}, N'OUT', {unitId}, 1, 0);
                """);

            foreach (var run in (CalculationRun[])[superseded, current])
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO calc.CalculationResult
                        (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                    SELECT NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {versionId}, {doc.PeriodKey.Value},
                           {doc.DocumentId}, CONCAT(N'R', n.n), N'OUT', {(run.Id == currentRunId ? 2m : 1m)}, {unitId}
                      FROM (SELECT TOP (3) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) AS n;
                    INSERT INTO calc.CalculationIssue
                        (DocumentId, PeriodKey, TableDefId, RowKey, RowNumber, Kind, MethodologyId, CalculationRunId, DetectedAt)
                    VALUES ({doc.DocumentId}, {doc.PeriodKey.Value}, {doc.TableDefId}, N'R1', 1, N'Missing', {methodologyId},
                            {run.Id}, {Now});
                    """);
            }
        }

        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM rpt.v_Project WHERE ProjectId = {doc.ProjectId} AND ProjectCode IS NOT NULL AND StatusName IS NOT NULL"));
        Assert.True(await ScalarAsync($"SELECT COUNT(*) FROM rpt.v_Period WHERE ProjectId = {doc.ProjectId} AND PeriodKey = {doc.PeriodKey.Value} AND StateName IS NOT NULL") == 1);
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM rpt.v_Document WHERE DocumentId = {doc.DocumentId} AND ProjectId = {doc.ProjectId}"));
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM rpt.v_MethodologyVersion WHERE MethodologyVersionId = {versionId} AND MethodologyCode = N'{methodologyCode}' AND StatusName = N'Published' AND EngineName IS NOT NULL AND IsLatestPublished = 1"));
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM rpt.v_MethodologyOutput WHERE MethodologyVersionId = {versionId} AND OutputCode = N'OUT' AND UnitCode IS NOT NULL"));

        // Результати — лише актуального прогону (3 рядки зі значенням 2), а не 6 (заміщений прогін — історія).
        Assert.Equal(3, await ScalarAsync($"SELECT COUNT(*) FROM rpt.v_CalculationResult WHERE DocumentId = {doc.DocumentId}"));
        Assert.Equal(3, await ScalarAsync($"SELECT COUNT(*) FROM rpt.v_CalculationResult WHERE DocumentId = {doc.DocumentId} AND CalculationRunId = {currentRunId} AND Value = 2 AND MethodologyCode = N'{methodologyCode}' AND KindName = N'Output' AND UnitCode IS NOT NULL"));
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM rpt.v_CalculationIssue WHERE DocumentId = {doc.DocumentId} AND CalculationRunId = {currentRunId} AND TableCode IS NOT NULL AND MethodologyCode = N'{methodologyCode}'"));
    }

    private async Task<int> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
