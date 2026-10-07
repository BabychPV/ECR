// tests/Ecr.Infrastructure.Tests/Persistence/TemplateListSummaryTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// UI-34: поля переліку шаблонів (назва, архів, лічильник документів, оновлення, автор чернетки,
/// пошук <c>q</c>); лічильник документів — лише за проєктами, які бачить читач.
/// </summary>
/// <remarks>
/// Мутаційні докази: у <c>ListTemplatesHandler</c> передати в сховище всі проєкти замість
/// <c>ReadableProjects</c> — червоний «чужий проєкт не рахується»; прибрати екранування в
/// <c>EscapeLike</c> — червоний «% не збігається з усім».
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateListSummaryTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лічильник_документів_рахує_лише_видимі_проєкти_і_несе_архів_автора_чернетки_й_назву()
    {
        var visible = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);
        var hidden = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);

        int authorId;
        await using (var db = sql.CreateContext())
        {
            var author = new User($"ada_{_tag}", $"Ada Lovelace {_tag}", AuthProvider.Local);
            author.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash("Tpl-Summary-Probe-2026!"));
            db.Users.Add(author);
            await db.SaveChangesAsync();
            authorId = author.Id;

            // Чернетка шаблону «видимого» проєкту, створена іншим користувачем.
            db.TemplateVersions.Add(new TemplateVersion(
                visible.TemplateId, $"9.9.{_tag}", authorId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));

            var archived = await db.Templates.SingleAsync(t => t.Id == hidden.TemplateId);
            archived.Archive(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
        }

        var page = await ListAsync(
            query: null,
            new AccessBuilder { UserId = 9 }
                .Permission("Template.View").Permission("Document.View")
                .Grant(Ecr.Domain.Enums.ResourceKind.Project, visible.ProjectId, Ecr.Domain.Enums.GrantLevel.Read));

        var seen = page.Items.Single(t => t.Id == visible.TemplateId);
        var other = page.Items.Single(t => t.Id == hidden.TemplateId);

        Assert.Equal(1, seen.DocumentCount);
        Assert.NotNull(seen.NameL10n);
        Assert.False(seen.IsArchived);
        Assert.Equal($"Ada Lovelace {_tag}", seen.DraftAuthorDisplayName);
        Assert.NotNull(seen.DraftCreatedAt);
        Assert.NotNull(seen.UpdatedAt);

        // ⛔ Документ чужого проєкту в лічильник не входить: 0, а не 1.
        Assert.Equal(0, other.DocumentCount);
        Assert.True(other.IsArchived);

        // RC7: момент архівування доходить до переліку; в обігу — null.
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), other.ArchivedAt);
        Assert.Null(seen.ArchivedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_чернетки_дає_draftEditedAt_і_піднімає_updatedAt_а_повернення_з_архіву_скидає_ArchivedAt()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);
        var edited = new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var created = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        int draftId;

        await using (var db = sql.CreateContext())
        {
            var draft = new TemplateVersion(doc.TemplateId, $"8.8.{_tag}", 1, created);
            db.TemplateVersions.Add(draft);
            await db.SaveChangesAsync();
            draftId = draft.Id;
        }

        var access = new AccessBuilder { UserId = 9 }.Permission("Template.View");

        // До правки: draftEditedAt = момент створення.
        var before = (await ListAsync(query: null, access)).Items.Single(t => t.Id == doc.TemplateId);
        Assert.Equal(created, before.DraftEditedAt);

        await using (var db = sql.CreateContext())
        {
            var draft = await db.TemplateVersions.SingleAsync(v => v.Id == draftId);
            draft.TouchDraft(7, edited);
            var template = await db.Templates.SingleAsync(t => t.Id == doc.TemplateId);
            template.Archive(edited);
            await db.SaveChangesAsync();
        }

        var after = (await ListAsync(query: null, access)).Items.Single(t => t.Id == doc.TemplateId);
        Assert.Equal(edited, after.DraftEditedAt);
        Assert.Equal(edited, after.UpdatedAt);
        Assert.Equal(edited, after.ArchivedAt);

        await using (var db = sql.CreateContext())
        {
            (await db.Templates.SingleAsync(t => t.Id == doc.TemplateId)).Restore();
            await db.SaveChangesAsync();
        }

        var restored = (await ListAsync(query: null, access)).Items.Single(t => t.Id == doc.TemplateId);
        Assert.False(restored.IsArchived);
        Assert.Null(restored.ArchivedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пошук_q_шукає_за_кодом_і_назвою_а_метасимволи_LIKE_екрануються()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);
        var access = new AccessBuilder { UserId = 9 }.Permission("Template.View");

        string code;
        await using (var db = sql.CreateContext())
        {
            code = (await db.Templates.SingleAsync(t => t.Id == doc.TemplateId)).Code;
        }

        var byCode = await ListAsync(code.ToLowerInvariant(), access);
        Assert.Contains(byCode.Items, t => t.Id == doc.TemplateId);

        var byName = await ListAsync($"Template {code[3..]}", access);
        Assert.Contains(byName.Items, t => t.Id == doc.TemplateId);

        // «%» і «_» — звичайні символи, а не підстановка. База тестів спільна: інші тести лишають
        // шаблони з «_» у коді (DUP_…), тож «порожньо» хибне. Доказ: у видачі лише ті, що
        // ЛІТЕРАЛЬНО містять символ у коді чи назві (підстановка віддала б і решту).
        foreach (var meta in new[] { "%", "_" })
        {
            var items = (await ListAsync(meta, access)).Items;
            Assert.DoesNotContain(items, t =>
                !t.Code.Contains(meta, StringComparison.Ordinal)
                && !(t.NameL10n?.Values.Values.Any(v => v.Contains(meta, StringComparison.Ordinal)) ?? false));
        }

        Assert.Empty((await ListAsync($"{code}zzz", access)).Items);
    }

    private async Task<PagedResult<TemplateSummary>> ListAsync(string? query, AccessBuilder builder)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        await using var db = sql.CreateContext();

        return await new ListTemplatesHandler(new TemplateVersionStore(db), access, user)
            .HandleAsync(new CursorRequest(CursorRequest.MaxLimit, null), query, CancellationToken.None);
    }
}
