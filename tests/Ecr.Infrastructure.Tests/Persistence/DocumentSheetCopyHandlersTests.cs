// tests/Ecr.Infrastructure.Tests/Persistence/DocumentSheetCopyHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): обробники «додати копію / перейменувати групу / видалити копію» над
/// справжньою базою — відмови з ключами каталогу, перенумерація, журнал безпеки.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (локально, не комітяться): без перенумерації в <c>DocumentSheetCopyStore.DeleteCopyAsync</c> —
/// червоний <see cref="Видалення_копії_2_із_3_перенумеровує_третю_у_другу_і_пише_журнал"/>; без перевірки
/// <c>copy.IsCopy</c> в обробнику — червоний <see cref="Оригінал_не_видаляється_409_з_ключем_sheetCopyIsOriginal"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentSheetCopyHandlersTests(SqlServerFixture sql)
{
    private const int UserId = 1;
    private static readonly DateTime Now = new(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Додавання_копії_позначеного_аркуша_дає_номер_2_і_пише_журнал()
    {
        var world = await ArrangeAsync(copyable: true);

        var created = await world.Run(h => h.AddAsync(world.Chain.DocumentId, world.Chain.SheetDefId, default));
        var second = await world.Run(h => h.AddAsync(world.Chain.DocumentId, world.Chain.SheetDefId, default));

        Assert.Equal((short)2, created.Ordinal);
        Assert.Equal((short)3, second.Ordinal);

        await using var db = world.Builder.CreateContext();
        Assert.Equal(
            2, await db.DocumentSheets.CountAsync(s => s.DocumentId == world.Chain.DocumentId && s.CopyOfId != null));
        Assert.True(await HasSecurityEventAsync(db, DocumentSheetCopyHandler.AddedEventType, created.SheetCopyId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Аркуш_без_позначки_копіюваного_не_копіюється_422_з_ключем_sheetNotCopyable()
    {
        var world = await ArrangeAsync(copyable: false);

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => world.Run(h => h.AddAsync(world.Chain.DocumentId, world.Chain.SheetDefId, default)));

        Assert.Equal("ECR-DOC-0422", thrown.ErrorCode);
        Assert.Equal("err.ECR-DOC-0422.sheetNotCopyable", thrown.Details!["messageKey"]);

        await using var db = world.Builder.CreateContext();
        Assert.False(await db.DocumentSheets.AnyAsync(s => s.DocumentId == world.Chain.DocumentId && s.CopyOfId != null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Перейменування_групи_пише_назву_на_оригіналі_а_порожня_назва_відхиляється()
    {
        var world = await ArrangeAsync(copyable: true);
        await world.Run(h => h.AddAsync(world.Chain.DocumentId, world.Chain.SheetDefId, default));

        await world.Run(h => h.RenameGroupAsync(
            world.Chain.DocumentId, world.Chain.SheetDefId,
            new Dictionary<string, string> { ["en"] = " Stacks ", ["ru"] = "Штабели" }, default));

        await using (var db = world.Builder.CreateContext())
        {
            var original = await db.DocumentSheets.AsNoTracking().SingleAsync(
                s => s.DocumentId == world.Chain.DocumentId && s.CopyOfId == null);
            Assert.Equal("Stacks", original.NameOverrideL10n!.Get("en"));
            Assert.Equal("Штабели", original.NameOverrideL10n.Get("ru"));
            Assert.True(await HasSecurityEventAsync(db, DocumentSheetCopyHandler.RenamedEventType, null));
        }

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => world.Run(h => h.RenameGroupAsync(
                world.Chain.DocumentId, world.Chain.SheetDefId, new Dictionary<string, string> { ["en"] = "  " }, default)));
        Assert.Equal("err.ECR-DOC-0422.sheetNameInvalid", thrown.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Оригінал_не_видаляється_409_з_ключем_sheetCopyIsOriginal()
    {
        var world = await ArrangeAsync(copyable: true);
        await world.Run(h => h.AddAsync(world.Chain.DocumentId, world.Chain.SheetDefId, default));
        long originalId;
        await using (var db = world.Builder.CreateContext())
        {
            originalId = (await db.DocumentSheets.AsNoTracking().SingleAsync(
                s => s.DocumentId == world.Chain.DocumentId && s.CopyOfId == null)).Id;
        }

        var thrown = await Assert.ThrowsAsync<DomainException>(
            () => world.Run(h => h.DeleteCopyAsync(world.Chain.DocumentId, originalId, default)));

        Assert.Equal("ECR-DOC-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.sheetCopyIsOriginal", thrown.Details!["messageKey"]);

        await using var check = world.Builder.CreateContext();
        Assert.Equal(2, await check.DocumentSheets.CountAsync(s => s.DocumentId == world.Chain.DocumentId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Затверджена_копія_не_видаляється_409_з_періодом_і_станом()
    {
        var world = await ArrangeAsync(copyable: true);
        var copy = await world.Run(h => h.AddAsync(world.Chain.DocumentId, world.Chain.SheetDefId, default));

        await using (var db = world.Builder.CreateContext())
        {
            var state = new ApprovalState(world.Chain.DocumentId, world.Chain.SheetDefId, 202601, copy.SheetCopyId);
            state.Submit(UserId, Now);
            state.Approve(UserId, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        var thrown = await Assert.ThrowsAsync<DomainException>(
            () => world.Run(h => h.DeleteCopyAsync(world.Chain.DocumentId, copy.SheetCopyId, default)));

        Assert.Equal("ECR-DOC-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.sheetCopyHasWorkflow", thrown.Details!["messageKey"]);
        Assert.Equal("202601", thrown.Details["periodKey"]);
        Assert.Equal("Approved", thrown.Details["status"]);

        await using var check = world.Builder.CreateContext();
        Assert.True(await check.DocumentSheets.AnyAsync(s => s.Id == copy.SheetCopyId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Видалення_копії_2_із_3_перенумеровує_третю_у_другу_і_пише_журнал()
    {
        var world = await ArrangeAsync(copyable: true);
        var two = await world.Run(h => h.AddAsync(world.Chain.DocumentId, world.Chain.SheetDefId, default));
        var three = await world.Run(h => h.AddAsync(world.Chain.DocumentId, world.Chain.SheetDefId, default));

        await world.Run(h => h.DeleteCopyAsync(world.Chain.DocumentId, two.SheetCopyId, default));

        await using var db = world.Builder.CreateContext();
        var copies = await db.DocumentSheets.AsNoTracking()
            .Where(s => s.DocumentId == world.Chain.DocumentId && s.CopyOfId != null)
            .Select(s => new { s.Id, s.CopyOrdinal })
            .ToListAsync();

        var remaining = Assert.Single(copies);
        Assert.Equal((three.SheetCopyId, (short)2), (remaining.Id, remaining.CopyOrdinal));
        Assert.True(await HasSecurityEventAsync(db, DocumentSheetCopyHandler.DeletedEventType, two.SheetCopyId));
    }

    private static async Task<bool> HasSecurityEventAsync(EcrDbContext db, string eventType, long? sheetCopyId)
    {
        var rows = await db.Database
            .SqlQuery<string>($"""
                SELECT TOP (50) ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent
                WHERE EventType = {eventType} ORDER BY Id DESC
                """)
            .ToListAsync();

        return sheetCopyId is null
            ? rows.Count > 0
            : rows.Any(json => json.Contains($"\"sheetCopyId\":{sheetCopyId}", StringComparison.Ordinal));
    }

    private async Task<World> ArrangeAsync(bool copyable)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey: 202603, ct: CancellationToken.None);

        SheetDef sheet;
        await using (var db = builder.CreateContext())
        {
            if (!await db.DocumentSheets.AnyAsync(s => s.DocumentId == chain.DocumentId && s.CopyOfId == null))
            {
                db.DocumentSheets.Add(new DocumentSheet(chain.DocumentId, chain.SheetDefId));
            }

            sheet = await db.SheetDefs.SingleAsync(s => s.Id == chain.SheetDefId);
            sheet.SetCopyable(copyable);
            await db.SaveChangesAsync();
            db.Entry(sheet).State = EntityState.Detached;
        }

        var snapshot = new TemplateVersionSnapshot(
            chain.TemplateVersionId, 0, [sheet], new Dictionary<int, ColumnDef>(),
            new Dictionary<(int, string), RowDef>());
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(chain.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var access = Substitute.For<IAccessDecisionService>();
        var profile = new AccessBuilder { UserId = UserId }
            .Permission(DocumentSheetCopyHandler.Permission)
            .Grant(ResourceKind.Project, chain.ProjectId, GrantLevel.Write)
            .Build();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(profile);
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), chain.DocumentId, Arg.Any<CancellationToken>())
            .Returns(_ => DocumentReadScope.For(profile, chain.ProjectId, snapshot));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        user.CorrelationId.Returns("sheet-copy");

        return new World(builder, chain, metadata, access, user);
    }

    private sealed class World(
        TestDocumentBuilder builder, TestDocument chain, IMetadataCache metadata, IAccessDecisionService access,
        ICurrentUser user)
    {
        public TestDocumentBuilder Builder { get; } = builder;

        public TestDocument Chain { get; } = chain;

        /// <summary>Новий контекст і обробник на КОЖЕН виклик — як на кожен HTTP-запит.</summary>
        public async Task<T> Run<T>(Func<DocumentSheetCopyHandler, Task<T>> action)
        {
            await using var db = Builder.CreateContext();
            return await action(Handlers(db)).ConfigureAwait(false);
        }

        public async Task Run(Func<DocumentSheetCopyHandler, Task> action)
        {
            await using var db = Builder.CreateContext();
            await action(Handlers(db)).ConfigureAwait(false);
        }

        private DocumentSheetCopyHandler Handlers(EcrDbContext db)
            => new(
                new DocumentStore(db), new DocumentSheetCopyStore(db), metadata, new SheetEditGate(db), access,
                new UnitOfWork(db), new AuditWriter(db), user, new FixedClock());
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = Now;
    }
}
