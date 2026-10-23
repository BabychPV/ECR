using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>AN-63: <c>DocumentSummary.PeriodKind</c> = <c>Project.PeriodKind</c> у переліку й картці, без додаткових запитів.</summary>
[Collection("SqlServer")]
public sealed class DocumentPeriodKindStoreTests(SqlServerFixture sql)
{
    [Theory]
    [InlineData(PeriodKind.Monthly)]
    [InlineData(PeriodKind.Quarterly)]
    [InlineData(PeriodKind.Yearly)]
    [InlineData(PeriodKind.Custom)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_і_картка_віддають_періодичність_проєкту(PeriodKind kind)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using (var setup = builder.CreateContext())
        {
            await setup.Projects.Where(p => p.Id == chain.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, kind), CancellationToken.None);
        }

        var period = new PeriodKeyFilter(chain.PeriodKey.Value);
        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);
        var store = new DocumentStore(counted);

        var page = await store.ListAsync(
            chain.ProjectId, period, default, new CursorRequest(Limit: 50), visibleProjectIds: null, CancellationToken.None);
        var card = await store.FindAsync(chain.DocumentId, period, CancellationToken.None);

        var listed = page.Items.Single(d => d.Id == chain.DocumentId);
        Assert.Equal(kind.ToString(), listed.PeriodKind);
        Assert.NotNull(card);
        Assert.Equal(kind.ToString(), card.PeriodKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кількість_запитів_переліку_не_росте_від_періодичності()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);

        var page = await new DocumentStore(counted).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(chain.PeriodKey.Value), default, new CursorRequest(Limit: 50),
            visibleProjectIds: null, CancellationToken.None);

        Assert.NotNull(page.Items.Single(d => d.Id == chain.DocumentId).PeriodKind);

        // Документи, стани, підсумки, пізні правки, застарілі результати (як у DocumentListFiltersStoreTests) - періодичність
        // їде підзапитом у запиті документів, не окремим запитом і не запитом на рядок.
        Assert.True(counter.Tally.Snapshot().Total == 5, counter.Tally.Snapshot().Format());
    }
}
