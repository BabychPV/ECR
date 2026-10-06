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
            archived.Archive();
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

        // «%» і «_» — звичайні символи, а не підстановка: жоден шаблон їх у коді чи назві не має.
        Assert.Empty((await ListAsync("%", access)).Items);
        Assert.Empty((await ListAsync("_", access)).Items);
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
