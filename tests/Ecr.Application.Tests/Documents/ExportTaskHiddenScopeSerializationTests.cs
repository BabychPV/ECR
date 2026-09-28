// tests/Ecr.Application.Tests/Documents/ExportTaskHiddenScopeSerializationTests.cs
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// S6: межі читання (<c>HiddenTableDefIds</c>/<c>HiddenColumnDefIds</c>) їдуть у
/// завданні експорту через чергу — JSON із <c>JsonSerializerDefaults.Web</c>, як
/// у <c>QuartzJobScheduler</c> і <c>ExportPayload.Parse</c>.
/// </summary>
public sealed class ExportTaskHiddenScopeSerializationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Межі_переживають_чергу()
    {
        var task = new ExcelExportTask(
            7, new ExcelExportOptions(false, false, "en", 202601, [3, 5], [11]), "x", DocumentExportFormat.Csv);

        var back = JsonSerializer.Deserialize<ExcelExportTask>(JsonSerializer.Serialize<object>(task, Web), Web)!;

        Assert.Equal([3, 5], back.Options.HiddenTableDefIds);
        Assert.Equal([11], back.Options.HiddenColumnDefIds);
    }

    /// <summary>
    /// ⚠ Завдання, поставлене ДО S6, полів не має: вони читаються як <c>null</c>,
    /// і експорт іде без фільтра — так, як ішов на момент постановки.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Завдання_до_S6_читається_без_меж()
    {
        const string legacy =
            """{"documentId":7,"options":{"includeFormulas":false,"includeStyles":false,"language":"en","periodKey":202601},"exportId":"x","format":"xlsx"}""";

        var back = JsonSerializer.Deserialize<ExcelExportTask>(legacy, Web)!;

        Assert.Null(back.Options.HiddenTableDefIds);
        Assert.Null(back.Options.HiddenColumnDefIds);
        Assert.Equal(202601, back.Options.PeriodKey);
    }
}
