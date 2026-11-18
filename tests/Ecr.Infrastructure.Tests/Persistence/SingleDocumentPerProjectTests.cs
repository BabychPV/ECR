// tests/Ecr.Infrastructure.Tests/Persistence/SingleDocumentPerProjectTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B151 (рішення людини 16.11): шаблон з прапорцем «один документ на проєкт» (HSE400, HSE401) не дає завести другий
/// документ у тому самому проєкті — ні послідовно, ні двома одночасними запитами; увімкнути прапорець, коли дублікати
/// вже є, не можна. Справжні база, <see cref="DocumentStore"/>, <see cref="TemplateVersionStore"/> і
/// <see cref="UnitOfWork"/>; підроблені лише метадані, права й журнал.
/// </summary>
/// <remarks>
/// ⛔ Мутації: прибрати перевірку в <c>CreateDocumentHandler</c> — червоні перший і паралельний тести; прибрати
/// <c>HOLDLOCK</c> із читання прапорця в <c>DocumentStore.FindDocumentBlockingSingleTemplateAsync</c> — червоний тест
/// «увімкнення чекає на створення»; прибрати перевірку дублікатів у <c>SetTemplateDocumentPolicyHandler</c> — червоний
/// тест увімкнення з дублікатами. Паралельний тест — запускати 20 разів.
/// </remarks>
[Collection("SqlServer")]
public sealed class SingleDocumentPerProjectTests(SqlServerFixture sql)
{
    private const int UserId = 9;

    private static readonly DateTime Now = new(2099, 1, 15, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public async Task Прапорець_увімкнено_другий_документ_у_проєкті_не_створюється_409()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await BuildAsync(builder);
        await SetFlagAsync(builder, doc.TemplateId, true);

        await using var db = builder.CreateContext();
        var error = await Assert.ThrowsAsync<DomainException>(
            async () => await (await CreateHandlerAsync(builder, db, doc, doc.ProjectId)).HandleAsync(
                doc.ProjectId, templateVersionId: null, [doc.SheetDefId], name: null, CancellationToken.None));

        Assert.Equal("ECR-DOC-4092", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-4092.documentExistsForTemplate", error.Details!["messageKey"]);
        Assert.Equal(doc.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Details["existingDocumentId"]);
        Assert.Equal(1, await DocumentCountAsync(builder, doc.ProjectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public async Task Прапорець_вимкнено_другий_документ_створюється()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await BuildAsync(builder);

        await using var db = builder.CreateContext();
        await (await CreateHandlerAsync(builder, db, doc, doc.ProjectId)).HandleAsync(
            doc.ProjectId, templateVersionId: null, [doc.SheetDefId], name: null, CancellationToken.None);

        Assert.Equal(2, await DocumentCountAsync(builder, doc.ProjectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public async Task Два_одночасні_створення_в_порожньому_проєкті_один_документ_і_одна_відмова_409()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await BuildAsync(builder);
        await SetFlagAsync(builder, doc.TemplateId, true);
        var projectId = await EmptyProjectAsync(builder, doc);

        await using var a = builder.CreateContext();
        await using var b = builder.CreateContext();
        var handlerA = await CreateHandlerAsync(builder, a, doc, projectId);
        var handlerB = await CreateHandlerAsync(builder, b, doc, projectId);
        using var gate = new Barrier(2);

        Task<Exception?> Create(CreateDocumentHandler handler) => Task.Run(async () =>
        {
            gate.SignalAndWait();
            try
            {
                await handler.HandleAsync(projectId, templateVersionId: null, [doc.SheetDefId], name: null, CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        var outcomes = await Task.WhenAll(Create(handlerA), Create(handlerB));

        Assert.Single(outcomes, o => o is null);
        var refusal = Assert.IsType<DomainException>(Assert.Single(outcomes, o => o is not null));
        Assert.Equal("ECR-DOC-4092", refusal.ErrorCode);
        Assert.Equal(1, await DocumentCountAsync(builder, projectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public async Task Увімкнення_прапорця_з_дублікатами_409_і_прапорець_лишається_вимкненим()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await BuildAsync(builder);

        long second;
        await using (var db = builder.CreateContext())
        {
            second = await (await CreateHandlerAsync(builder, db, doc, doc.ProjectId)).HandleAsync(
                doc.ProjectId, templateVersionId: null, [doc.SheetDefId], name: null, CancellationToken.None);
        }

        await using var policyDb = builder.CreateContext();
        var error = await Assert.ThrowsAsync<DomainException>(
            () => PolicyHandler(policyDb, readable: id => id != second).HandleAsync(doc.TemplateId, true, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0409.singleDocumentDuplicates", error.Details!["messageKey"]);
        Assert.Equal("2", error.Details["duplicateCount"]);

        // Невидимий дублікат не називається: у переліку лише видимий.
        var ids = Assert.IsAssignableFrom<IEnumerable<long>>(error.Details["documentIds"]);
        Assert.Equal([doc.DocumentId], ids);

        Assert.False(await FlagAsync(builder, doc.TemplateId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public async Task Увімкнення_прапорця_без_дублікатів_проходить_і_вимкнення_теж()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await BuildAsync(builder);

        await using (var db = builder.CreateContext())
        {
            var card = await PolicyHandler(db, readable: _ => true).HandleAsync(doc.TemplateId, true, CancellationToken.None);
            Assert.True(card.SingleDocumentPerProject);
        }

        Assert.True(await FlagAsync(builder, doc.TemplateId));

        await using (var db = builder.CreateContext())
        {
            var card = await PolicyHandler(db, readable: _ => true).HandleAsync(doc.TemplateId, false, CancellationToken.None);
            Assert.False(card.SingleDocumentPerProject);
        }

        Assert.False(await FlagAsync(builder, doc.TemplateId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public async Task Читання_прапорця_при_створенні_тримає_рядок_шаблону_до_коміту()
    {
        // Увімкнення прапорця (UPDATE рядка шаблону) мусить чекати на відкриту транзакцію створення, яка прочитала
        // прапорець вимкненим: інакше увімкнення не побачить документа, що от-от закомітиться.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await BuildAsync(builder);

        await using var holder = builder.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        Assert.Null(await new DocumentStore(holder).FindDocumentBlockingSingleTemplateAsync(
            doc.ProjectId, doc.TemplateId, CancellationToken.None));

        await using (var other = builder.CreateContext())
        await using (var otherTransaction = await other.Database.BeginTransactionAsync())
        {
            await other.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 300");
            var timeout = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
                () => other.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE cfg.Template SET SingleDocumentPerProject = 1 WHERE Id = {doc.TemplateId}"));
            Assert.Equal(1222, timeout.Number);
        }

        await holderTransaction.RollbackAsync();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public async Task Перевірка_одного_документа_поза_транзакцією_відмовляє()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await BuildAsync(builder);

        await using var db = builder.CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DocumentStore(db).FindDocumentBlockingSingleTemplateAsync(doc.ProjectId, doc.TemplateId, CancellationToken.None));
    }

    private static async Task<CreateDocumentHandler> CreateHandlerAsync(
        TestDocumentBuilder builder, EcrDbContext db, TestDocument doc, int projectId)
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

        var templates = Substitute.For<ITemplateVersionStore>();
        templates.FindTemplateOfVersionAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns((Template?)new Template(EcrCode.Create("TPL"), Text("Template"), UserId, Now));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId }
                .Permission(CreateDocumentHandler.Permission)
                .Grant(ResourceKind.Project, projectId, GrantLevel.Write)
                .Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        return new CreateDocumentHandler(metadata, new DocumentStore(db), templates, new UnitOfWork(db), access, user, clock);
    }

    private static SetTemplateDocumentPolicyHandler PolicyHandler(EcrDbContext db, Func<long, bool> readable)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId }.Permission(SetTemplateDocumentPolicyHandler.Permission).Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci => readable(ci.ArgAt<long>(1)) ? EditDecision.Allow() : EditDecision.Deny(EditDenyReason.NoGrant));

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        return new SetTemplateDocumentPolicyHandler(
            new TemplateVersionStore(db), access, new UnitOfWork(db), Substitute.For<IAuditWriter>(), user, clock);
    }

    /// <summary>
    /// Ланцюг будівника, документ якого несе власний шаблон — як кожен документ, створений обробником (будівник
    /// заводить документ без <c>TemplateId</c>, а такі рядки на розгортанні доповнює 99-epilogue).
    /// </summary>
    private static async Task<TestDocument> BuildAsync(TestDocumentBuilder builder)
    {
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET TemplateId = {doc.TemplateId} WHERE Id = {doc.DocumentId}");
        return doc;
    }

    /// <summary>Ще один проєкт на тій самій версії шаблону — без жодного документа.</summary>
    private static async Task<int> EmptyProjectAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();
        var source = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == doc.ProjectId);
        var project = new Project(
            EcrCode.Create($"B151P{Random.Shared.Next(100_000, 999_999)}"), Text("B151 empty project"),
            new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31),
            doc.TemplateVersionId, PeriodKind.Monthly, source.PeriodPolicyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task SetFlagAsync(TestDocumentBuilder builder, int templateId, bool value)
    {
        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.Template SET SingleDocumentPerProject = {value} WHERE Id = {templateId}");
    }

    private static async Task<bool> FlagAsync(TestDocumentBuilder builder, int templateId)
    {
        await using var db = builder.CreateContext();
        return await db.Templates.AsNoTracking().Where(t => t.Id == templateId).Select(t => t.SingleDocumentPerProject).SingleAsync();
    }

    private static async Task<int> DocumentCountAsync(TestDocumentBuilder builder, int projectId)
    {
        await using var db = builder.CreateContext();
        return await db.Documents.AsNoTracking().CountAsync(d => d.ProjectId == projectId);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
