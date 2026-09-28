using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// P8 (перф-аудит): пакетний <c>CanEditSlicesAsync</c> дає ТІ САМІ рішення,
/// що й поштучний <c>CanEditSliceAsync</c>, — для кожного зрізу, кожної
/// комірки й кожного профілю.
/// </summary>
/// <remarks>
/// ⛔ Безпека тут головна, швидкість — друга. Пакетний шлях ділить між
/// зрізами спільне (знімок, умови доступу, правила періоду, рядки), і саме
/// там легко «поділити» зайве: стан ПЕРШОГО аркуша на всі, таблицю ПЕРШОГО
/// зрізу на всі, вікна чинності одного екземпляра на всі. Тому набір навмисно
/// строкатий: два документи (відкритий і закритий період), чотири аркуші
/// (чернетка, поданий, затверджений), правило <c>SourceWindow</c> на таблиці,
/// що не є першою, і вісім профілів — без грантів, <c>Write</c>, <c>Read</c>,
/// <c>Deny</c> на таблицю, на колонку, на проєкт, грант лише на таблицю, лише
/// на аркуш.
///
/// ⚠ Поштучний метод реалізований через пакетний з одним екземпляром, тож
/// ця еквівалентність стереже саме ПАКЕТНІ місця: групування, стан решти
/// аркушів групи, спільні правила й рядки. Мутації, що це доводять, — у
/// звіті коміту.
/// </remarks>
[Collection("SqlServer")]
public sealed class AccessDecisionBatchEquivalenceTests(SqlServerFixture sql) : IDisposable
{
    /// <summary>Період обох документів.</summary>
    /// <remarks>
    /// ⚠ Не 2026-05…07 (їх архівують <c>ArchiveJobTests</c>) і не 202609
    /// (<c>PeriodAccessSliceTests</c>): звільнення партиції йде по періоду.
    /// </remarks>
    private const int PeriodKeyValue = 202610;

    private const byte MonthNumber = 10;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакетні_рішення_тотожні_поштучним_для_кожного_зрізу_і_профілю()
    {
        var world = await ArrangeAsync();

        var reasons = new HashSet<EditDenyReason>();
        var allowed = 0;

        foreach (var (name, profile) in world.Profiles)
        {
            IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>> batch;
            await using (var db = world.Builder.CreateContext())
            {
                batch = await Service(db).CanEditSlicesAsync(profile, world.InstanceIds, CancellationToken.None);
            }

            Assert.Equal(world.InstanceIds.Order(), batch.Keys.Order());

            foreach (var instanceId in world.InstanceIds)
            {
                IReadOnlyDictionary<CellAddress, EditDecision> single;
                await using (var db = world.Builder.CreateContext())
                {
                    single = await Service(db).CanEditSliceAsync(profile, instanceId, CancellationToken.None);
                }

                var batched = batch[instanceId];

                Assert.NotEmpty(single);
                Assert.Equal(single.Keys.OrderBy(Key), batched.Keys.OrderBy(Key));

                foreach (var (address, expected) in single)
                {
                    var actual = batched[address];
                    var where = $"профіль «{name}», екземпляр {instanceId}, {address}";

                    Assert.True(expected.IsAllowed == actual.IsAllowed, $"{where}: IsAllowed {expected.IsAllowed} ≠ {actual.IsAllowed}");
                    Assert.True(expected.Reason == actual.Reason, $"{where}: Reason {expected.Reason} ≠ {actual.Reason}");
                    Assert.True(
                        expected.RequiresConfirmation == actual.RequiresConfirmation,
                        $"{where}: RequiresConfirmation");
                    Assert.True(string.Equals(expected.Detail, actual.Detail, StringComparison.Ordinal), $"{where}: Detail");

                    reasons.Add(expected.Reason);
                    allowed += expected.IsAllowed ? 1 : 0;
                }
            }
        }

        // ⛔ Проти хибнозеленого: набір мусить справді проходити крізь гілки,
        // які пакетний шлях ділить. Збіг «усе заборонено» з «усе заборонено»
        // нічого б не доводив.
        Assert.True(allowed > 0, "Жодної дозволеної комірки — набір не перевіряє дозволу.");
        Assert.Contains(EditDenyReason.NoGrant, reasons);
        Assert.Contains(EditDenyReason.DocumentSubmitted, reasons);
        Assert.Contains(EditDenyReason.DocumentApproved, reasons);
        Assert.Contains(EditDenyReason.PeriodClosed, reasons);
        Assert.Contains(EditDenyReason.OutsidePermitWindow, reasons);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакет_бачить_Deny_на_таблицю_і_стан_кожного_аркуша_окремо()
    {
        // ⚠ Предметна перевірка тих самих випадків, що й еквівалентність вище:
        // якщо обидва шляхи колись разом «забудуть» Deny чи стан аркуша,
        // еквівалентність лишиться зеленою — а ця ні.
        var world = await ArrangeAsync();

        var profile = world.Profiles["Write + Deny таблиці E2"];

        await using var db = world.Builder.CreateContext();
        var batch = await Service(db).CanEditSlicesAsync(profile, world.InstanceIds, CancellationToken.None);

        Assert.All(batch[world.Denied.TableInstanceId].Values, d => Assert.Equal(EditDenyReason.NoGrant, d.Reason));
        Assert.All(batch[world.Submitted.TableInstanceId].Values, d => Assert.Equal(EditDenyReason.DocumentSubmitted, d.Reason));
        Assert.All(batch[world.Approved.TableInstanceId].Values, d => Assert.Equal(EditDenyReason.DocumentApproved, d.Reason));
        Assert.All(batch[world.ClosedInstanceId].Values, d => Assert.Equal(EditDenyReason.PeriodClosed, d.Reason));
        Assert.Contains(batch[world.Base.TableInstanceId].Values, d => d.IsAllowed);
    }

    private static string Key(CellAddress address) => address.ToString();

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Base,
        ExtraTable Submitted,
        ExtraTable Approved,
        ExtraTable Denied,
        long ClosedInstanceId,
        IReadOnlyList<long> InstanceIds,
        IReadOnlyDictionary<string, AccessProfile> Profiles);

    /// <summary>
    /// Документ A (відкритий період): базова таблиця на своєму аркуші, аркуш
    /// SA з двома таблицями (поданий), SB з однією (чернетка, правило
    /// <c>SourceWindow</c>), SC з однією (затверджений). Документ B — інший
    /// проєкт, закритий період, дві таблиці на двох аркушах.
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var docA = await builder.BuildAsync(PeriodKeyValue, columnCount: 3, rowCount: 3, ct: CancellationToken.None);
        var extraA = await MultiTableDocument.AddTablesAsync(builder, docA, [2, 1, 1], ct: CancellationToken.None);

        var docB = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 2, ct: CancellationToken.None);
        var extraB = await MultiTableDocument.AddTablesAsync(builder, docB, [1], ct: CancellationToken.None);

        var submitted = extraA[0];
        var permitted = extraA[2];
        var approved = extraA[3];

        await using (var db = builder.CreateContext())
        {
            await AdvanceAsync(db, docA.ProjectId, PeriodState.Open);
            await AdvanceAsync(db, docB.ProjectId, PeriodState.Open);
            await AdvanceAsync(db, docB.ProjectId, PeriodState.Closed);

            var submittedState = new ApprovalState(docA.DocumentId, submitted.SheetDefId, PeriodKeyValue);
            submittedState.Submit(userId: 1, Now);

            var approvedState = new ApprovalState(docA.DocumentId, approved.SheetDefId, PeriodKeyValue);
            approvedState.Submit(userId: 1, Now);
            approvedState.Approve(userId: 2, Now);

            db.ApprovalStates.AddRange(submittedState, approvedState);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await ArrangePermitAsync(builder, docA.TemplateVersionId, permitted);

        var projects = new[] { docA.ProjectId, docB.ProjectId };

        AccessBuilder Grant(GrantLevel level)
        {
            var access = new AccessBuilder();
            foreach (var project in projects)
            {
                access.Grant(ResourceKind.Project, project, level);
            }

            return access;
        }

        var profiles = new Dictionary<string, AccessProfile>(StringComparer.Ordinal)
        {
            ["без грантів"] = new AccessBuilder().Build(),
            ["Write на проєкти"] = Grant(GrantLevel.Write).Build(),
            ["Read на проєкти"] = Grant(GrantLevel.Read).Build(),
            ["Write + Deny таблиці E2"] = Grant(GrantLevel.Write).Deny(ResourceKind.Table, permitted.TableDefId).Build(),
            ["Write + Deny колонки базової таблиці"] =
                Grant(GrantLevel.Write).Deny(ResourceKind.Column, docA.ColumnDefIds[1]).Build(),
            ["Write + Deny проєкту A"] = Grant(GrantLevel.Write).Deny(ResourceKind.Project, docA.ProjectId).Build(),
            ["Read на проєкти + Write лише на таблицю E3"] =
                Grant(GrantLevel.Read).Grant(ResourceKind.Table, approved.TableDefId, GrantLevel.Write)
                    .Grant(ResourceKind.Table, docA.TableDefId, GrantLevel.Write).Build(),
            ["Read на проєкти + Write лише на аркуш SB"] =
                Grant(GrantLevel.Read).Grant(ResourceKind.Sheet, permitted.SheetDefId, GrantLevel.Write).Build(),
        };

        // ⚠ Базова таблиця документа A навмисно НЕ перша в списку: порядок
        // запиту не мусить збігатися з порядком «першого аркуша» групи.
        var ids = new List<long>();
        ids.AddRange(extraA.Select(t => t.TableInstanceId));
        ids.Add(docA.TableInstanceId);
        ids.Add(docB.TableInstanceId);
        ids.AddRange(extraB.Select(t => t.TableInstanceId));

        return new World(
            builder, docA, submitted, approved, permitted, docB.TableInstanceId, ids, profiles);
    }

    private static async Task AdvanceAsync(EcrDbContext db, int projectId, PeriodState target)
    {
        var period = await db.Periods.FirstAsync(
            p => p.ProjectId == projectId && p.PeriodKeyValue == PeriodKeyValue, CancellationToken.None);

        period.AdvanceTo(target, Now);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Правило <c>SourceWindow</c> на таблиці <paramref name="table"/>: перша
    /// колонка обирає дозвіл (діє до 31 серпня), друга — жовтнева.
    /// </summary>
    private async Task ArrangePermitAsync(TestDocumentBuilder builder, int templateVersionId, ExtraTable table)
    {
        await ExecuteAsync(
            "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = @month WHERE Id = @id",
            ("@id", table.ColumnDefIds[1]), ("@month", MonthNumber));

        await using var db = builder.CreateContext();

        var registry = new RegistryDef(
            EcrCode.Create($"PERMIT_P8_{table.TableDefId}"), Text("Permits"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(CancellationToken.None);

        var entry = new RegistryEntry(registry.Id, EcrCode.Create("P1"), Text("Permit 1"));
        entry.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 8, 31));
        db.RegistryEntries.Add(entry);

        db.PeriodAccessRules.Add(PeriodAccessRuleDef
            .ForSourceWindow(templateVersionId, table.ColumnDefIds[0], OutOfWindowBehavior.ReadOnly)
            .ForTable(table.TableDefId));
        await db.SaveChangesAsync(CancellationToken.None);

        // Лише ПЕРШИЙ рядок обрав дозвіл: другий лишається редагованим, і
        // пакет мусить розрізнити їх так само, як поштучний шлях.
        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(PeriodKeyValue), table.RowIds[0], table.ColumnDefIds[0]),
            table.TableDefId,
            new CellValueData { ValueRegistryEntryId = entry.Id }));

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    private async Task ExecuteAsync(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
