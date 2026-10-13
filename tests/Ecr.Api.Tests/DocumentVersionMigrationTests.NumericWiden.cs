// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.NumericWiden.cs
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// LP-29: розширення Precision/Scale колонки Decimal (28/12 → 34/16) у <c>migrate-version</c> переносить значення без
/// відмови <c>guardedWithData</c>; звуження (Scale 12 → 10) з даними — як і раніше 422.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server. Мутаційний доказ: повернути в
/// <c>VersionMigrationPlanner.CompareColumns</c> безумовні <c>Guarded</c> для Precision/Scale — «розширення»
/// червоніє (422 guardedWithData); прибрати перевірку <c>scaleAfter &gt;= scaleBefore</c> у <c>IsWidening</c> — червоніє «звуження».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Розширення_Precision_Scale_сухий_прогін_дозволяє_і_нічого_не_змінює()
    {
        var s = await ArrangeAsync(Target.C2PrecisionWidened).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var body = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {body} {app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.True(report.GetProperty("canApply").GetBoolean(), body);
        Assert.Equal(0, report.GetProperty("guardedValues").GetInt64());
        Assert.Equal(0, report.GetProperty("lostValues").GetInt64());
        Assert.Equal(2, report.GetProperty("transferredValues").GetInt64());
        Assert.Contains(
            report.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("kind").GetString() == "Widened" && i.GetProperty("field").GetString() == "Precision"
                 && i.GetProperty("oldValue").GetString() == "28" && i.GetProperty("newValue").GetString() == "34");

        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Розширення_Precision_Scale_значення_збережені_відмови_немає()
    {
        var s = await ArrangeAsync(Target.C2PrecisionWidened).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("applied").GetBoolean());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
        Assert.Equal(s.TargetVersionId, project.TemplateVersionId);

        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(c.TableRowId))
            .ToListAsync().ConfigureAwait(true);
        Assert.Contains(cells, c => c.ValueString == "a" && c.ColumnDefId == s.TargetColumns["C1"]);
        Assert.Contains(cells, c => c.ValueNumeric == 42m && c.ColumnDefId == s.TargetColumns["C2"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Звуження_Scale_з_даними_лишається_guardedWithData_і_нічого_не_змінюється()
    {
        var s = await ArrangeAsync(Target.C2ScaleNarrowed).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);

        var body = await AssertProblemAsync(
            response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateDataLoss").ConfigureAwait(true);
        Assert.Contains("guardedWithData", body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }
}
