using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Перелік документів на реальному SQL Server (`Q-167`).</summary>
[Collection("SqlServer")]
public sealed class DocumentStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стан_погодження_читається_одним_запитом_на_сторінку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc1 = await builder.BuildAsync(ct: CancellationToken.None);

        await using (var setup = builder.CreateContext())
        {
            var doc2 = new Document(
                doc1.ProjectId, "DOC2-Q167", 1, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
            setup.Documents.Add(doc2);
            await setup.SaveChangesAsync(CancellationToken.None);

            setup.ApprovalStates.Add(new ApprovalState(doc1.DocumentId, doc1.SheetDefId, doc1.PeriodKey.Value));
            setup.ApprovalStates.Add(new ApprovalState(doc2.Id, doc1.SheetDefId, doc1.PeriodKey.Value));
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        // Рахуємо команди, які EF реально відправив, а не результат: N+1
        // на КОЖЕН документ сторінки видно лише по кількості запитів, не по
        // вмісту відповіді (той самий прийом, що й `CellStoreTests`).
        var executed = new List<string>();
        await using var counting = CreateCountingContext(executed);
        var store = new DocumentStore(counting);

        var page = await store.ListAsync(
            doc1.ProjectId, new PeriodKeyFilter(doc1.PeriodKey.Value), new CursorRequest(Limit: 50),
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);

        // ⛔ Q-271: ключ словника — КОД аркуша (`SheetDef.Code`), а не
        // числовий `SheetDefId.ToString()`. Контракт задокументований у
        // `GetDocumentTablesHandler.DocumentTableDto.SheetCode` ("він же
        // ключ у DocumentSummary.SheetStates") і саме за кодом читає
        // фронтенд (`DocumentPage.tsx`: `sheetStates[s.code]`). Ключ за
        // числовим ідентифікатором ніколи не збігається з кодом — це і є
        // причина, чому бейдж статусу подання/затвердження в браузері не
        // оновлювався НІКОЛИ, попри справжню зміну стану на сервері.
        var numericKey = doc1.SheetDefId.ToString(CultureInfo.InvariantCulture);
        Assert.All(page.Items, d => Assert.True(d.SheetStates.ContainsKey(doc1.SheetCode)));
        Assert.All(page.Items, d => Assert.False(d.SheetStates.ContainsKey(numericKey)));

        // ⛔ Q-167: сторінка з ДВОМА документами — а запитів у базу рівно
        // ДВА (перелік документів + стан погодження ВСІЄЇ сторінки), а не
        // три (перелік + стан на кожен документ окремо).
        Assert.Equal(2, executed.Count);
    }

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);
}
