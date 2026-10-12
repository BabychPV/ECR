// tests/Ecr.Infrastructure.Tests/Persistence/ProjectTemplateVersionBindingTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ФВ-1.2: проєкт прив'язується до версії шаблону, а публікація нової версії того самого шаблону проєкт не
/// переносить — тільки явний перенос версії (<c>POST /documents/{id}/migrate-version</c>).
/// </summary>
/// <remarks>
/// ⚠ Що саме тут зафіксовано (за кодом, а не за переказом вимоги). «Слідування» проєкту за версією шаблону
/// виражене рівно одним полем: <c>doc.Project.TemplateVersionId</c>. Документ власної версії не зберігає — усе
/// читання й створення беруть версію з проєкту (<c>CreateDocumentHandler.ProjectVersionAsync</c>). Нова
/// опублікована версія цього поля сама не змінює (єдиний запис — <c>DocumentVersionMigrationStore</c>);
/// отже нові документи проєкту до переносу лишаються на його версії, а запит із версією, якої проєкт не має,
/// відмовляється <c>422 ECR-DOC-0422 versionNotProject</c>. Це дослівне прочитання <c>ФВ-1.2</c> («зміна
/// шаблону не змінює вже прив'язані проєкти автоматично»); автоматичного «слідування за останньою
/// опублікованою» в коді немає.
///
/// ⚠ Справжні <c>DocumentStore</c>, <c>UnitOfWork</c> і база; підроблені лише метадані, шаблон і права (як у
/// <c>CreateDocumentStructureLockTests</c>). «Перенос» — прямий <c>UPDATE doc.Project</c> тим самим способом,
/// що й у тому наборі: сам обробник переносу тут не предмет.
///
/// ⛔ Мутація: у <c>DocumentStore.FindProjectTemplateVersionIdAsync</c> повертати останню опубліковану версію
/// шаблону проєкту замість <c>Project.TemplateVersionId</c> — перший тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectTemplateVersionBindingTests(SqlServerFixture sql)
{
    private const int UserId = 9;

    /// <summary>Автор версій: користувач із фікстури будівника (FK на sec.User).</summary>
    private const int AuthorId = 1;

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.2")]
    public async Task Публікація_нової_версії_не_переносить_проєкт_і_новий_документ_лишається_на_версії_проєкту()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var v2 = await PublishNewVersionAsync(builder, doc);

        // Проєкт лишився на v1, хоча v2 опубліковано.
        await using (var check = builder.CreateContext())
        {
            Assert.Equal(
                doc.TemplateVersionId,
                await check.Projects.AsNoTracking().Where(p => p.Id == doc.ProjectId).Select(p => p.TemplateVersionId).SingleAsync());
            Assert.Equal(
                doc.TemplateVersionId,
                await new DocumentStore(check).FindProjectTemplateVersionIdAsync(doc.ProjectId, CancellationToken.None));
            Assert.Equal(
                TemplateVersionStatus.Published,
                await check.TemplateVersions.AsNoTracking().Where(v => v.Id == v2.VersionId).Select(v => v.Status).SingleAsync());
        }

        // Новий документ без вказаної версії заводиться на v1 (аркуш v1 приймається).
        await using var db = builder.CreateContext();
        var handler = Handler(builder, db, doc, v2);
        var id = await handler.HandleAsync(
            doc.ProjectId, templateVersionId: null, [doc.SheetDefId], name: null, CancellationToken.None);

        await using (var check = builder.CreateContext())
        {
            Assert.Equal(
                [doc.SheetDefId],
                await check.DocumentSheets.AsNoTracking().Where(s => s.DocumentId == id).Select(s => s.SheetDefId).ToListAsync());
        }

        // Запит із версією, якої проєкт не має (v2), — відмова, а не мовчазне «слідування».
        var refusal = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(doc.ProjectId, v2.VersionId, [v2.SheetDefId], name: null, CancellationToken.None));
        Assert.Equal("ECR-DOC-0422", refusal.ErrorCode);
        Assert.Equal("err.ECR-DOC-0422.versionNotProject", refusal.Details!["messageKey"]);
        Assert.Equal(
            doc.TemplateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            refusal.Details["projectVersionId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.2")]
    public async Task Після_переносу_версії_проєкту_нові_документи_йдуть_на_нову_версію_а_стара_відхиляється()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var v2 = await PublishNewVersionAsync(builder, doc);

        await MigrateProjectAsync(builder, doc.ProjectId, v2.VersionId);

        await using var db = builder.CreateContext();
        var handler = Handler(builder, db, doc, v2);

        var id = await handler.HandleAsync(
            doc.ProjectId, templateVersionId: null, [v2.SheetDefId], name: null, CancellationToken.None);

        await using (var check = builder.CreateContext())
        {
            Assert.Equal(
                [v2.SheetDefId],
                await check.DocumentSheets.AsNoTracking().Where(s => s.DocumentId == id).Select(s => s.SheetDefId).ToListAsync());
        }

        // Тепер версією проєкту є v2, а v1 — «чужа».
        var refusal = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(doc.ProjectId, doc.TemplateVersionId, [doc.SheetDefId], name: null, CancellationToken.None));
        Assert.Equal("err.ECR-DOC-0422.versionNotProject", refusal.Details!["messageKey"]);
    }

    private sealed record NewVersion(int VersionId, int SheetDefId);

    /// <summary>Публікує v1 і другу версію того самого шаблону (з власним аркушем), теж опубліковану.</summary>
    private static async Task<NewVersion> PublishNewVersionAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();

        var v1 = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        v1.Publish(AuthorId, Now);

        var v2 = new TemplateVersion(doc.TemplateId, "2.0.0.0", AuthorId, Now, clonedFromVersionId: v1.Id);
        db.TemplateVersions.Add(v2);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(
            v2.Id, EcrCode.Create($"V2S{v2.Id}"), Text("Sheet v2"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        v2.Publish(AuthorId, Now);
        await db.SaveChangesAsync();
        return new NewVersion(v2.Id, sheet.Id);
    }

    private static CreateDocumentHandler Handler(
        TestDocumentBuilder builder, EcrDbContext db, TestDocument doc, NewVersion v2)
    {
        SheetDef sheet1;
        SheetDef sheet2;
        using (var read = builder.CreateContext())
        {
            sheet1 = read.SheetDefs.AsNoTracking().Single(s => s.Id == doc.SheetDefId);
            sheet2 = read.SheetDefs.AsNoTracking().Single(s => s.Id == v2.SheetDefId);
        }

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                doc.TemplateVersionId, 0, [sheet1],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int, string), RowDef>()));
        metadata.GetAsync(v2.VersionId, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                v2.VersionId, 0, [sheet2],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int, string), RowDef>()));

        var templates = Substitute.For<ITemplateVersionStore>();
        templates.FindTemplateOfVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult<Template?>(new Template(EcrCode.Create("TPL"), Text("Template"), UserId, Now)));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId }
                .Permission(CreateDocumentHandler.Permission)
                .Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Write)
                .Build());

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        return new CreateDocumentHandler(
            metadata, new DocumentStore(db), templates, new UnitOfWork(db), access, user, clock);
    }

    /// <summary>«Перенос»: проєкт переключено на іншу версію й зафіксовано другим з'єднанням.</summary>
    private static async Task MigrateProjectAsync(TestDocumentBuilder builder, int projectId, int versionId)
    {
        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Project SET TemplateVersionId = {versionId} WHERE Id = {projectId}");
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
