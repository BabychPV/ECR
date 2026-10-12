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

    /// <summary>
    /// ФВ-1.2 (рішення людини 12.10): форма створення проєкту пропонує останню ОПУБЛІКОВАНУ версію — клієнт
    /// вибирає її з цього переліку (<c>status == Published</c>, найпізніший <c>publishedAt</c>). Тест доводить,
    /// що перелік віддає саме те, на що спирається вибір: стан і момент публікації кожної версії, а чернетка
    /// з більшим Id й виведена з обігу версія в «опубліковані» не потрапляють.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.2")]
    public async Task Перелік_версій_віддає_стан_і_момент_публікації_для_вибору_останньої_опублікованої()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var t1 = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);

        int v2Id;
        int draftId;
        int deprecatedId;
        await using (var setup = builder.CreateContext())
        {
            // v1 — версія з документа-фікстури (чернетка) — публікується першою.
            var v1 = await setup.TemplateVersions.FindAsync(document.TemplateVersionId);
            Assert.NotNull(v1);
            v1.Publish(1, t1);

            var v2 = new TemplateVersion(document.TemplateId, "2.0.0", 1, t1);
            var draft = new TemplateVersion(document.TemplateId, "3.0.0-draft", 1, t1);
            var deprecated = new TemplateVersion(document.TemplateId, "2.5.0-old", 1, t1);
            setup.TemplateVersions.AddRange(v2, draft, deprecated);
            await setup.SaveChangesAsync();

            v2.Publish(1, t2);
            deprecated.Publish(1, t3);
            deprecated.Deprecate(1, t3);
            await setup.SaveChangesAsync();

            v2Id = v2.Id;
            draftId = draft.Id;
            deprecatedId = deprecated.Id;
        }

        await using var db = builder.CreateContext();
        var store = new TemplateVersionStore(db);

        var page = await store.ListVersionsAsync(
            document.TemplateId, new CursorRequest(Limit: 100), CancellationToken.None);

        var byId = page.Items.ToDictionary(v => v.Id);
        Assert.Equal(Ecr.Domain.Enums.TemplateVersionStatus.Draft, byId[draftId].Status);
        Assert.Null(byId[draftId].PublishedAt);
        Assert.Equal(Ecr.Domain.Enums.TemplateVersionStatus.Deprecated, byId[deprecatedId].Status);
        Assert.Equal(Ecr.Domain.Enums.TemplateVersionStatus.Published, byId[v2Id].Status);
        Assert.Equal(t2, byId[v2Id].PublishedAt);

        // ⛔ Саме правило вибору форми: серед Published — найпізніший PublishedAt. Версія-чернетка має більший
        // Id, а виведена з обігу — пізніший момент публікації; обидві мусять бути поза вибором.
        var latestPublished = page.Items
            .Where(v => v.Status == Ecr.Domain.Enums.TemplateVersionStatus.Published)
            .OrderByDescending(v => v.PublishedAt)
            .ThenByDescending(v => v.Id)
            .First();

        Assert.Equal(v2Id, latestPublished.Id);
        Assert.True(draftId > v2Id, "фікстура: чернетка мала б бути новішою за Id");
    }
}
