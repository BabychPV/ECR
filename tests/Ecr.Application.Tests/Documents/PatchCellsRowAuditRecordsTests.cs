using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Записи журналу рядків для нових рядків PATCH/книги (<see cref="PatchCellsHandler.RowAddedRecords"/>): адреса
/// рядка з екземпляра, справжній Id з порядку створення, походження — те саме, що й у значень (D-442/D-445).
/// </summary>
public sealed class PatchCellsRowAuditRecordsTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TableInstanceRef Instance = new(TableInstanceId: 70, DocumentId: 5, TableDefId: 31, TemplateVersionId: 2, PeriodKey: 202610);

    [Theory]
    [InlineData("UserEdit", false, "UserEdit")]
    [InlineData("Import", false, "Import")]
    [InlineData("UserEdit", true, "ApproverEdit")]
    [InlineData("Import", true, "ApproverEdit")]
    [InlineData("Integration", true, "Integration")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.21")]
    public void Новий_рядок_пишеться_з_походженням_батчу_і_адресою_екземпляра(string origin, bool approverEdit, string expected)
    {
        PatchRow[] creations = [new("N1", null, []), new("N2", null, [])];
        var request = new PatchCellsRequest(Instance.TableInstanceId, Instance.PeriodKey, origin, creations);

        var records = PatchCellsHandler.RowAddedRecords(
            request, Instance, new PeriodKey(Instance.PeriodKey), creations, [901, 902], userId: 4, Now, approverEdit, "corr");

        Assert.Equal(
            [
                new RowChangeRecord(Now, 5, 202610, 70, 31, 901, "N1", RowChangeAction.Added, 4, expected, "corr"),
                new RowChangeRecord(Now, 5, 202610, 70, 31, 902, "N2", RowChangeAction.Added, 4, expected, "corr"),
            ],
            records);
    }
}
