// tests/Ecr.Application.Tests/Documents/RowLockCanLockRowsTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents.RowLocks;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ B143 (D-538): спільне рішення «хто блокує рядки» (<see cref="RowLockRules.LockDenial"/>) і прапорець картки над ним
/// (<see cref="RowLockCapability"/>): кожна гілка рішення й кожна гілка обходу таблиць — окремим рядком.
/// </summary>
/// <remarks>
/// Мутації (локально, правило 401): у <c>RowLockCapability</c> (1) документа немає → <c>true</c>; (2) не пропускати
/// видалену таблицю; (3) зупинятися на першій таблиці (<c>return false</c> після неї); (4) читати факти за іншим
/// періодом чи моментом — кожна червонить свій тест нижче. Гілки самого <c>LockDenial</c> і <c>CanReadTable</c> —
/// фільтри доступу: їх мутує лише аудитор (рядки «Deny mutation (for AU)» у листі готовності).
/// </remarks>
public sealed class RowLockCanLockRowsTests
{
    private const int ProjectId = 31;
    private const int OwnerId = 5;
    private const int PeriodKey = 209905;
    private const int VersionId = 7;
    private const long DocumentId = 900;

    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B143")]
    [InlineData(true, GrantLevel.Write, false, EditDenyReason.None)] // власник із Write, без права
    [InlineData(true, GrantLevel.Read, false, EditDenyReason.NoGrant)] // власник лише з Read
    [InlineData(false, GrantLevel.Write, false, EditDenyReason.NoGrant)] // не власник, Write, без права
    [InlineData(false, GrantLevel.Read, true, EditDenyReason.None)] // носій права, лише Read (Q6)
    [InlineData(false, GrantLevel.Read, false, EditDenyReason.NoGrant)] // ні власник, ні носій
    public void Блокує_власник_з_Write_або_носій_права(bool owner, GrantLevel level, bool right, EditDenyReason expected)
    {
        var (sheet, table) = Sheet(1, "A");
        var profile = Profile(owner ? OwnerId : 8, level, right, sheet, table).Build();

        Assert.Equal(expected, RowLockRules.LockDenial(profile, Target(), sheet, table, PeriodKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B143")]
    public void Симуляція_і_стан_документа_відмовляють_і_власникові_і_носієві_права()
    {
        var (sheet, table) = Sheet(1, "A");
        var both = Profile(OwnerId, GrantLevel.Write, right: true, sheet, table);

        Assert.Equal(
            EditDenyReason.SimulationReadOnly,
            RowLockRules.LockDenial(both.Build(simulation: true), Target(), sheet, table, PeriodKey));
        Assert.Equal(
            EditDenyReason.PeriodClosed,
            RowLockRules.LockDenial(both.Build(), Target(period: PeriodState.Closed), sheet, table, PeriodKey));
        Assert.Equal(
            EditDenyReason.ProjectArchived,
            RowLockRules.LockDenial(both.Build(), Target(project: ProjectStatus.Archived), sheet, table, PeriodKey));
        Assert.Equal(
            EditDenyReason.ArchivingInProgress,
            RowLockRules.LockDenial(both.Build(), Target(archiving: true), sheet, table, PeriodKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B143")]
    public async Task Прапорець_читає_факти_саме_цього_документа_періоду_й_моменту()
    {
        var (sheet, table) = Sheet(1, "A");
        var store = Substitute.For<IRowLockStore>();
        store.GetTargetAsync(DocumentId, PeriodKey, Now, Arg.Any<CancellationToken>()).Returns(Target());
        var capability = Capability(store, sheet);
        var owner = Profile(OwnerId, GrantLevel.Write, right: false, sheet, table).Build();

        Assert.True(await capability.CanLockRowsAsync(owner, DocumentId, PeriodKey, CancellationToken.None));

        // Інший документ чи період — фактів немає (заглушка відповідає лише на точні аргументи) → false, а не true.
        Assert.False(await capability.CanLockRowsAsync(owner, DocumentId + 1, PeriodKey, CancellationToken.None));
        Assert.False(await capability.CanLockRowsAsync(owner, DocumentId, PeriodKey + 1, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B143")]
    public async Task Прапорець_хибний_коли_єдина_таблиця_видалена_або_закрита_читачеві()
    {
        var (sheet, table) = Sheet(1, "A");
        var store = Substitute.For<IRowLockStore>();
        store.GetTargetAsync(DocumentId, PeriodKey, Now, Arg.Any<CancellationToken>()).Returns(Target());
        var capability = Capability(store, sheet);

        // Носій права, якому таблицю заборонено: POST дав би 404 (таблиця невидима) — прапорець хибний.
        var blind = Profile(8, GrantLevel.Read, right: true, sheet, table).Deny(ResourceKind.Table, table.Id).Build();
        Assert.False(await capability.CanLockRowsAsync(blind, DocumentId, PeriodKey, CancellationToken.None));

        // Видалена таблиця не рахується навіть власникові з Write (POST її не знаходить — `FindTable`).
        var owner = Profile(OwnerId, GrantLevel.Write, right: false, sheet, table).Build();
        Assert.True(await capability.CanLockRowsAsync(owner, DocumentId, PeriodKey, CancellationToken.None));
        table.SoftDelete(OwnerId, Now);
        Assert.False(await capability.CanLockRowsAsync(owner, DocumentId, PeriodKey, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B143")]
    public async Task Прапорець_істинний_коли_дозволена_хоч_одна_таблиця_не_лише_перша()
    {
        var first = Sheet(1, "A");
        var second = Sheet(2, "B");
        var store = Substitute.For<IRowLockStore>();
        store.GetTargetAsync(DocumentId, PeriodKey, Now, Arg.Any<CancellationToken>()).Returns(Target());
        var capability = Capability(store, first.Sheet, second.Sheet);

        // Власник: на першій таблиці лише Read, на другій — Write.
        var owner = new AccessBuilder { UserId = OwnerId }
            .Grant(ResourceKind.Project, ProjectId, GrantLevel.Read)
            .Grant(ResourceKind.Sheet, first.Sheet.Id, GrantLevel.Read)
            .Grant(ResourceKind.Table, first.Table.Id, GrantLevel.Read)
            .Grant(ResourceKind.Sheet, second.Sheet.Id, GrantLevel.Write)
            .Grant(ResourceKind.Table, second.Table.Id, GrantLevel.Write)
            .Build();

        Assert.Equal(EditDenyReason.NoGrant, RowLockRules.LockDenial(owner, Target(), first.Sheet, first.Table, PeriodKey));
        Assert.True(await capability.CanLockRowsAsync(owner, DocumentId, PeriodKey, CancellationToken.None));
    }

    /// <remarks>
    /// Мутації: (5) рахувати прапорець і без <c>includeStepApprovers</c> (не картка) — червоніє перший блок; (6) рахувати
    /// без періоду — тут це не компілюється (період — аргумент рішення), а «без періоду → null» тримає другий блок;
    /// (7) не заповнювати <c>CanLockRows</c> — червоніє третій блок.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B143")]
    public async Task Картка_рахує_прапорець_лише_для_картки_з_періодом()
    {
        var (sheet, table) = Sheet(1, "A");
        var store = Substitute.For<IRowLockStore>();
        store.GetTargetAsync(DocumentId, PeriodKey, Now, Arg.Any<CancellationToken>()).Returns(Target());

        Ecr.Application.Documents.GetDocumentHandler HandlerFor(int userId)
        {
            var profile = Profile(userId, GrantLevel.Write, right: false, sheet, table).Permission("Document.View").Build();
            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);

            // Гранти нижче проєкту вмикають звуження картки — межі читача рахуються зі знімка, як у службі доступу.
            var snapshot = new TemplateVersionSnapshot(
                VersionId, 0, [sheet], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>());
            access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
                  .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), ProjectId, snapshot));
            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(userId);
            var documents = Substitute.For<IDocumentStore>();
            documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
                     .Returns(new DocumentSummary(
                         DocumentId, ProjectId, "DOC-900", Now, 1, new Dictionary<string, string>(StringComparer.Ordinal)));

            return new Ecr.Application.Documents.GetDocumentHandler(
                documents, access, user, stepApprovers: null, rowLocks: Capability(store, sheet));
        }

        var handler = HandlerFor(OwnerId);

        // Не картка (історія, активність, версії): прапорця немає й факти блокувань не читаються.
        var plain = await handler.HandleAsync(DocumentId, PeriodKey, includeStepApprovers: false, CancellationToken.None);
        Assert.NotNull(plain);
        Assert.Null(plain.CanLockRows);

        // Картка без періоду: рішення — на документ × період, тож прапорця немає.
        var noPeriod = await handler.HandleAsync(DocumentId, null, includeStepApprovers: true, CancellationToken.None);
        Assert.NotNull(noPeriod);
        Assert.Null(noPeriod.CanLockRows);
        await store.DidNotReceive().GetTargetAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        // Картка з періодом: рішення сервера.
        var card = await handler.HandleAsync(DocumentId, PeriodKey, includeStepApprovers: true, CancellationToken.None);
        Assert.NotNull(card);
        Assert.True(card.CanLockRows);

        // Той самий документ для не власника без права (теж Write): картка каже «ні», а не сталу.
        var foreign = await HandlerFor(8).HandleAsync(DocumentId, PeriodKey, includeStepApprovers: true, CancellationToken.None);
        Assert.NotNull(foreign);
        Assert.False(foreign.CanLockRows);
    }

    private static RowLockCapability Capability(IRowLockStore store, params SheetDef[] sheets)
    {
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            VersionId, 0, sheets, new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>()));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        return new RowLockCapability(store, metadata, clock);
    }

    private static RowLockTarget Target(
        ProjectStatus project = ProjectStatus.Active, bool archiving = false, PeriodState period = PeriodState.Open)
        => new(ProjectId, OwnerId, project, archiving, period, "Asia/Tashkent", VersionId, "DOC");

    private static AccessBuilder Profile(int userId, GrantLevel level, bool right, SheetDef sheet, TableDef table)
    {
        var builder = new AccessBuilder { UserId = userId }
            .Grant(ResourceKind.Project, ProjectId, level)
            .Grant(ResourceKind.Sheet, sheet.Id, level)
            .Grant(ResourceKind.Table, table.Id, level);
        return right ? builder.Permission(RowLockRules.Permission) : builder;
    }

    private static (SheetDef Sheet, TableDef Table) Sheet(int id, string code)
    {
        var sheet = new SheetDef(VersionId, EcrCode.Create(code), Text(code), id);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, id);
        var table = new TableDef(id, EcrCode.Create("T1"), Text("T1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, 100 + id);
        sheet.AddTable(table);
        return (sheet, table);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
