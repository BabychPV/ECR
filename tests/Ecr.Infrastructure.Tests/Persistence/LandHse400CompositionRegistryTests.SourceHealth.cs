// tests/Ecr.Infrastructure.Tests/Persistence/LandHse400CompositionRegistryTests.SourceHealth.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>B80 (N1, RW.5): факти здоров'я зв'язки HSE400 → Land у <see cref="SystemHealthStore"/> на справжньому SQL Server.</summary>
public sealed partial class LandHse400CompositionRegistryTests
{
    /// <remarks>
    /// Мутація (B80-M11, факт здоров'я): <c>monthsWithData.Exists(m => !approved.Contains(m))</c> → <c>false</c> —
    /// незатверджений лютий із даними не дає попередження, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Факти_здоров_я_стан_воріт_затверджені_місяці_і_незатверджений_місяць_з_даними()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(
            ("FG_Internal", "Density_20", 1, 0.81m),
            ("FG_Internal", "Density_20", 2, 0.82m));
        var doc = lastBuilt!;
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        // Зв'язки немає — фактів немає (null), а не «any».
        Assert.Null((await HealthAsync(template, f.CompositionCode, now)).Hse400Source);

        await AddBindingAsync(f, doc.TemplateId, DocumentSourceState.Any);
        var any = (await HealthAsync(template, f.CompositionCode, now)).Hse400Source!;
        Assert.False(any.ApprovedOnly);
        Assert.Equal(0, any.ApprovedMonths);
        Assert.True(any.UnapprovedMonthWithData);

        await using (var db = sql.CreateContext())
        {
            await db.DocumentSourceBindings.Where(b => b.RegistryDefId == f.CompositionId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, DocumentSourceState.ApprovedOnly));
        }

        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202601, DocumentStatus.Approved);
        var january = (await HealthAsync(template, f.CompositionCode, now)).Hse400Source!;
        Assert.True(january.ApprovedOnly);
        Assert.Equal(1, january.ApprovedMonths);
        Assert.True(january.UnapprovedMonthWithData);

        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202602, DocumentStatus.Approved);
        var both = (await HealthAsync(template, f.CompositionCode, now)).Hse400Source!;
        Assert.Equal(2, both.ApprovedMonths);
        Assert.False(both.UnapprovedMonthWithData);

        // Інший рік — документа немає: місяців null, попередження з даних немає.
        var otherYear = (await HealthAsync(template, f.CompositionCode, now.AddYears(1))).Hse400Source!;
        Assert.Null(otherYear.ApprovedMonths);
        Assert.False(otherYear.UnapprovedMonthWithData);
    }

    private async Task<Ecr.Application.Ports.SystemHealthSnapshot> HealthAsync(string templateCode, string registryCode, DateTime utcNow)
    {
        await using var db = sql.CreateContext();
        var store = new SystemHealthStore(
            db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SystemHealthStore>.Instance,
            hse400TemplateCode: templateCode,
            hse400RegistryCode: registryCode);
        return await store.ReadAsync(utcNow, CancellationToken.None);
    }
}
