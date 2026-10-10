// tests/Ecr.Infrastructure.Tests/Persistence/CreateDocumentStructureLockTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// L6-02 / N1-04 (аудит 2026-10-09, AN-76): створення документа не комітиться зі складом версії
/// шаблону, з якої проєкт уже перенесено.
/// </summary>
/// <remarks>
/// ⛔ Що було. Версія проєкту читалась <c>AsNoTracking</c> без блокування, склад документа
/// перевірявся за нею, а запис ішов без транзакції: перенос, що зафіксувався між читанням і
/// <c>INSERT</c>, лишав документ зі складом старої версії під проєктом нової.
///
/// ⚠ Справжній <c>DocumentStore</c>, <c>UnitOfWork</c> і база; підроблені лише метадані, шаблон і
/// права. «Перенос» вклинюється детерміновано — у відповіді сховища шаблонів, яка в обробнику стоїть
/// МІЖ читанням версії проєкту і записом: другим з'єднанням змінюється <c>Project.TemplateVersionId</c>
/// і комітиться. Жодних пауз за годинником.
///
/// ⛔ Мутація: прибрати <c>LockProjectTemplateVersionAsync</c>/<c>EnsureProjectVersionUnchanged</c>
/// з <c>CreateDocumentHandler</c> — перший тест створює документ (і не отримує 409).
/// </remarks>
[Collection("SqlServer")]
public sealed class CreateDocumentStructureLockTests(SqlServerFixture sql)
{
    private const int UserId = 9;

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-02")]
    public async Task Перенос_проєкту_між_читанням_версії_і_записом_дає_409_і_документ_не_створюється()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var before = await DocumentCountAsync(builder, doc.ProjectId);
        var newVersionId = await AddVersionAsync(builder, doc.TemplateId);

        await using var db = builder.CreateContext();
        var handler = await HandlerAsync(
            builder, db, doc, afterVersionRead: () => MigrateProjectAsync(builder, doc.ProjectId, newVersionId));

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => handler.HandleAsync(doc.ProjectId, templateVersionId: null, [doc.SheetDefId], name: null, CancellationToken.None));

        Assert.Equal(ErrorCodes.SheetBusy, error.ErrorCode);
        Assert.Equal(DocumentStructure.StructureChangedKey, error.Details!["messageKey"]);

        // Документ не з'явився: ні зі складом старої версії, ні взагалі.
        Assert.Equal(before, await DocumentCountAsync(builder, doc.ProjectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-02")]
    public async Task Без_переносу_документ_створюється_зі_складом_версії_проєкту()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var before = await DocumentCountAsync(builder, doc.ProjectId);

        await using var db = builder.CreateContext();
        var handler = await HandlerAsync(builder, db, doc, afterVersionRead: () => Task.CompletedTask);

        var id = await handler.HandleAsync(
            doc.ProjectId, templateVersionId: null, [doc.SheetDefId], name: null, CancellationToken.None);

        Assert.Equal(before + 1, await DocumentCountAsync(builder, doc.ProjectId));
        await using var check = builder.CreateContext();
        Assert.Equal(
            [doc.SheetDefId],
            await check.DocumentSheets.AsNoTracking().Where(s => s.DocumentId == id).Select(s => s.SheetDefId).ToListAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-02")]
    public async Task Блок_рядка_проєкту_тримається_до_коміту_і_видає_версію_проєкту()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var holder = builder.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        var locked = await new DocumentStore(holder).LockProjectTemplateVersionAsync(doc.ProjectId, CancellationToken.None);
        Assert.Equal(doc.TemplateVersionId, locked);

        // Друге з'єднання на тому самому блоку: тайм-аут блокування (1222), а не мовчазне читання.
        await using (var other = builder.CreateContext())
        await using (var otherTransaction = await other.Database.BeginTransactionAsync())
        {
            await other.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 300");

            var timeout = await Assert.ThrowsAsync<SqlException>(
                () => new DocumentStore(other).LockProjectTemplateVersionAsync(doc.ProjectId, CancellationToken.None));
            Assert.Equal(1222, timeout.Number);
        }

        await holderTransaction.RollbackAsync();

        // Блок звільнено — тепер береться.
        await using var after = builder.CreateContext();
        await using var afterTransaction = await after.Database.BeginTransactionAsync();
        Assert.Equal(
            doc.TemplateVersionId,
            await new DocumentStore(after).LockProjectTemplateVersionAsync(doc.ProjectId, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-02")]
    public async Task Блок_рядка_проєкту_поза_транзакцією_відмовляє()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DocumentStore(db).LockProjectTemplateVersionAsync(doc.ProjectId, CancellationToken.None));
    }

    private async Task<CreateDocumentHandler> HandlerAsync(
        TestDocumentBuilder builder, EcrDbContext db, TestDocument doc, Func<Task> afterVersionRead)
    {
        SheetDef sheet;
        await using (var read = builder.CreateContext())
        {
            sheet = await read.SheetDefs.AsNoTracking().SingleAsync(s => s.Id == doc.SheetDefId);
        }

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                doc.TemplateVersionId, 0, [sheet],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int, string), RowDef>()));

        // ⚠ Сховище шаблонів питається обробником МІЖ читанням версії проєкту і записом документа:
        // саме тут вклинюється «перенос».
        var templates = Substitute.For<ITemplateVersionStore>();
        templates.FindTemplateOfVersionAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await afterVersionRead();
            return (Template?)new Template(EcrCode.Create("TPL"), Text("Template"), UserId, Now);
        });

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

    /// <summary>Друга версія того самого шаблону — ціль «переносу».</summary>
    private static async Task<int> AddVersionAsync(TestDocumentBuilder builder, int templateId)
    {
        await using var db = builder.CreateContext();
        var version = new TemplateVersion(templateId, "2.0.0.0", 2, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        return version.Id;
    }

    /// <summary>«Перенос»: проєкт переключено на іншу версію й зафіксовано другим з'єднанням.</summary>
    private static async Task MigrateProjectAsync(TestDocumentBuilder builder, int projectId, int versionId)
    {
        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Project SET TemplateVersionId = {versionId} WHERE Id = {projectId}");
    }

    private static async Task<int> DocumentCountAsync(TestDocumentBuilder builder, int projectId)
    {
        await using var db = builder.CreateContext();
        return await db.Documents.AsNoTracking().CountAsync(d => d.ProjectId == projectId);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
