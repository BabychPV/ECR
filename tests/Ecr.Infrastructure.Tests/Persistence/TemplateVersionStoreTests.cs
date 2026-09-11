// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionStoreTests.cs
using Ecr.Application.Common;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Курсорна пагінація версій шаблону — на <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Q-225: до фіксу <c>ListVersionsAsync</c> викликав сховище з
/// <c>new CursorRequest()</c> завжди — тобто дефолтний ліміт 50 і
/// <c>Cursor: null</c>, незалежно від того, що передав клієнт. Версія за
/// 50-ту в одному шаблоні була недосяжна назавжди, а сховище й обробник не
/// мали жодного тесту, який це побачив би. Тест нижче фіксує курсорну
/// поведінку — той самий патерн, що вже перевірений на
/// <c>ListTemplatesAsync</c> поруч.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_версій_гортає_сторінки_за_курсором_а_не_обрізає_дефолтним_лімітом()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        // Ще дві версії того самого шаблону — разом рівно три.
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        await using (var setup = builder.CreateContext())
        {
            setup.TemplateVersions.Add(new TemplateVersion(document.TemplateId, "1.0.1", 1, now));
            setup.TemplateVersions.Add(new TemplateVersion(document.TemplateId, "1.0.2", 1, now));
            await setup.SaveChangesAsync();
        }

        await using var db = builder.CreateContext();
        var store = new TemplateVersionStore(db);

        var firstPage = await store.ListVersionsAsync(
            document.TemplateId, new CursorRequest(Limit: 2), CancellationToken.None);

        Assert.Equal(2, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await store.ListVersionsAsync(
            document.TemplateId, new CursorRequest(Limit: 2, Cursor: firstPage.NextCursor), CancellationToken.None);

        // ⛔ Головне твердження: третя версія ДОСЯЖНА другою сторінкою, а не
        // мовчки відкинута — саме це раніше було неможливим у принципі.
        Assert.Single(secondPage.Items);
        Assert.Null(secondPage.NextCursor);

        var allIds = firstPage.Items.Select(v => v.Id).Concat(secondPage.Items.Select(v => v.Id)).ToList();
        Assert.Equal(3, allIds.Distinct().Count());
    }
}
