// tests/Ecr.Infrastructure.Tests/Persistence/PeriodWithinDocumentProjectTests.cs
using Ecr.Application.Errors;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ФВ-1.11: період документа має лежати в межах його проєкту; порушення —
/// <c>ECR-PRD-0422</c>.
/// </summary>
/// <remarks>
/// ⚠ Перевірку робить <c>WorkflowStore.LockPeriodAsync</c> — через неї йдуть
/// дії над документом за період (<c>ReopenDocumentHandler</c>,
/// <c>RecallSheetHandler</c>). Періоди в базі ділять ключ між проєктами
/// (<c>202603</c> є в кожному), тож загроза тут не «ключа не існує», а «ключ
/// існує — але в ЧУЖОМУ проєкті». Саме її тест і відтворює: у сусіднього
/// проєкту період <c>202603</c> є, у проєкту документа — ні.
///
/// Мутаційний доказ: у запиті <c>LockPeriodAsync</c> прибрати зв'язку
/// <c>d.ProjectId = p.ProjectId</c> (наприклад, <c>JOIN … ON 1 = 1</c>) — метод
/// поверне період сусіда замість відмови, і тест почервоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodWithinDocumentProjectTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.11")]
    public async Task Період_чужого_проєкту_відхиляється_кодом_ECR_PRD_0422()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var mine = await builder.BuildAsync(periodKey: 202602, rowCount: 1, ct: CancellationToken.None);
        var neighbour = await builder.BuildAsync(periodKey: 202603, rowCount: 1, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var store = new WorkflowStore(db);

        // Власний період документа — знаходиться.
        var own = await store.LockPeriodAsync(mine.DocumentId, mine.PeriodKey, CancellationToken.None);
        Assert.Equal(mine.ProjectId, own.ProjectId);

        // Період із тим самим ключем існує, але в іншому проєкті.
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => store.LockPeriodAsync(mine.DocumentId, neighbour.PeriodKey, CancellationToken.None));

        Assert.Equal("ECR-PRD-0422", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0422.periodNotInProjectOfDocument", error.Details!["messageKey"]);
        Assert.Equal("202603", error.Details["periodKey"]);
    }
}
