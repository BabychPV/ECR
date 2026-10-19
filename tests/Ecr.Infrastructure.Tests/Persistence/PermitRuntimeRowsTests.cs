// tests/Ecr.Infrastructure.Tests/Persistence/PermitRuntimeRowsTests.cs
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Рантайм прив'язок (Permits, PM-10): рядки й заблоковані ліміти документа створюються з дозволу під час заведення
/// екземплярів таблиць - на живому SQL, через справжній <c>RowStore.EnsureTableInstancesAsync</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати охоронець <c>AnyActiveRowsAsync</c> у <c>PermitBindingService.MaterializeAsync</c> - червоніє
/// «Таблиця_без_прив_язки…»; прибрати виклик сервісу в кінці <c>CreateMissingTableInstancesAsync</c> - червоніють усі, що
/// чекають рядків; прибрати <c>catch</c> на <c>UQ_TableInstance</c> - червоніє «Два_паралельні…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitRuntimeRowsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly PeriodKey January = new(202601);
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Type => $"AIR{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дозвіл_за_об_єктом_створює_рядки_P_id_з_ключами_лімітами_й_аудитом_Permit_і_повторне_відкриття_не_дублює()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;

        await using var db = scene.NewContext();
        var created = await NewStore(db, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, default);
        Assert.Equal(1, created);

        var rows = await scene.RowsAsync();
        Assert.Equal(scene.Permit.RowEntryIds.Select(id => $"P{id}"), rows.Select(r => r.RowKey));
        Assert.Equal([1, 2, 3], rows.Select(r => r.Ordinal));

        var doc = scene.Sheet.Doc;
        var first = await scene.CellsAsync(rows[0].Id);
        Assert.Equal("NOx", first[doc.ColumnDefIds[0]].Text);
        Assert.Equal(12.5m, first[doc.ColumnDefIds[1]].Number);
        Assert.Equal("first", first[scene.Sheet.NoteColumnId].Text);
        Assert.False(first.ContainsKey(doc.ColumnDefIds[2]), "незіставлена колонка лишається порожньою");

        // Порожнє поле дозволу не породжує комірки.
        Assert.False((await scene.CellsAsync(rows[1].Id)).ContainsKey(scene.Sheet.NoteColumnId));

        // 3 рядки: ключ + ліміт у кожного, примітка у двох = 8 записів аудиту з походженням Permit.
        Assert.Equal(8, await scene.PermitAuditCountAsync());

        // Друге відкриття: екземпляр уже є - нічого не створюється й не дублюється.
        await using var again = scene.NewContext();
        Assert.Equal(0, await NewStore(again, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, default));
        Assert.Equal(3, (await scene.RowsAsync()).Count);
        Assert.Equal(8, await scene.PermitAuditCountAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дозвіл_з_поля_шапки_Lookup_дає_ті_самі_рядки()
    {
        var scene = await ArrangeAsync(fromHeader: true);
        await using var _ = scene.H;

        await using var db = scene.NewContext();
        await NewStore(db, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, default);

        Assert.Equal(scene.Permit.RowEntryIds.Select(id => $"P{id}"), (await scene.RowsAsync()).Select(r => r.RowKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дозволу_немає_екземпляр_без_рядків_відкриття_не_падає_а_попередження_є()
    {
        var scene = await ArrangeAsync(facility: "Nowhere");
        await using var _ = scene.H;

        await using var db = scene.NewContext();
        var created = await NewStore(db, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, default);

        Assert.Equal(1, created);
        Assert.Empty(await scene.RowsAsync());

        // Те саме, що бачить журнал: попередження про відсутній дозвіл, а не виняток.
        await using var direct = scene.NewContext();
        var result = await NewService(direct).MaterializeAsync(scene.DocumentId, January, [scene.Sheet.Doc.TableDefId], default);
        Assert.Equal(0, result.Rows);
        Assert.Contains(result.Warnings, w => w.Case == "noPermit");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стеля_MaxDynamicRows_урізає_рядки_з_попередженням()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;
        await scene.ExecuteAsync($"UPDATE cfg.TableDef SET MaxDynamicRows = 2 WHERE Id = {scene.Sheet.Doc.TableDefId}");

        // Екземпляр - без дозволу, потім сервіс напряму, щоб побачити попередження.
        await using var db = scene.NewContext();
        await NewStore(db, permits: false).EnsureTableInstancesAsync(scene.DocumentId, January, default);

        await using var direct = scene.NewContext();
        var result = await NewService(direct).MaterializeAsync(scene.DocumentId, January, [scene.Sheet.Doc.TableDefId], default);

        Assert.Equal(2, result.Rows);
        Assert.Contains(result.Warnings, w => w.Case == "rowsClamped");
        Assert.Equal(scene.Permit.RowEntryIds.Take(2).Select(id => $"P{id}"), (await scene.RowsAsync()).Select(r => r.RowKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заблокований_ліміт_не_редагується_а_незіставлена_колонка_редагується()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;

        await using var db = scene.NewContext();
        await NewStore(db, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, default);
        var rows = await scene.RowsAsync();
        var instanceId = await scene.InstanceIdAsync();

        await using var setup = scene.NewContext();
        var period = await setup.Periods.FirstAsync(p => p.ProjectId == scene.ProjectId && p.PeriodKeyValue == January.Value);
        period.AdvanceTo(PeriodState.Open, Now);
        var user = new Ecr.Domain.Entities.Security.User($"pm10_{Guid.NewGuid():N}"[..20], "PM-10 test user", Ecr.Domain.Enums.AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        setup.Users.Add(user);
        await setup.SaveChangesAsync();

        var profile = new AccessBuilder { UserId = user.Id }
            .Grant(Ecr.Domain.Enums.ResourceKind.Project, scene.ProjectId, Ecr.Domain.Enums.GrantLevel.Write).Build();

        await using var serviceDb = scene.NewContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var access = new AccessDecisionService(
            serviceDb, new MetadataCache(memory, serviceDb), new AccessProfileCache(memory), new TestClock(Now),
            Substitute.For<ICurrentUser>(), new WorkflowStore(serviceDb));

        var doc = scene.Sheet.Doc;
        var limit = new CellAddress(January, rows[0].Id, doc.ColumnDefIds[1]);
        var info = new CellAddress(January, rows[0].Id, scene.Sheet.NoteColumnId);
        var fact = new CellAddress(January, rows[0].Id, doc.ColumnDefIds[2]);

        var decisions = await access.CanEditCellsAsync(profile, instanceId, January, [limit, info, fact], default);

        Assert.False(decisions[limit].IsAllowed);
        Assert.Equal(EditDenyReason.ColumnReadOnly, decisions[limit].Reason);
        Assert.Equal(EditDenyReason.ColumnReadOnly, decisions[info].Reason);
        Assert.True(decisions[fact].IsAllowed, decisions[fact].Reason.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиця_без_прив_язки_поводиться_як_раніше_і_не_питає_дозвіл()
    {
        // Land-подібна таблиця: фіксовані рядки шаблону, прив'язки немає.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(rowCount: 3, rowMode: TableRowMode.Fixed, ct: CancellationToken.None);
        await using (var prep = builder.CreateContext())
        {
            prep.DocumentSheets.Add(new DocumentSheet(chain.DocumentId, chain.SheetDefId));
            await prep.SaveChangesAsync();
            await prep.TableRows.Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.TableInstanceId == chain.TableInstanceId).ExecuteDeleteAsync();
            await prep.TableInstances.Where(i => i.DocumentId == chain.DocumentId).ExecuteDeleteAsync();
        }

        await using var db = builder.CreateContext();
        var created = await NewStore(db, permits: true).EnsureTableInstancesAsync(chain.DocumentId, chain.PeriodKey, default);

        Assert.Equal(1, created);
        await using var check = builder.CreateContext();
        var keys = await check.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && check.TableInstances.Any(i => i.Id == r.TableInstanceId && i.DocumentId == chain.DocumentId))
            .OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue).ToListAsync();
        Assert.Equal(3, keys.Count);
        Assert.All(keys, k => Assert.DoesNotMatch("^P[0-9]+$", k));
        Assert.Equal(0, await check.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM aud.CellChange WHERE DocumentId = {chain.DocumentId}").SingleAsync());

        // Охоронець: без прив'язки сервіс не читає ні прив'язок, ні дозволів.
        var store = Substitute.For<IPermitBindingStore>();
        store.AnyActiveRowsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(false);
        var resolver = Substitute.For<IPermitResolver>();
        var service = new PermitBindingService(
            check, new BulkCellLoader(sql.ConnectionString, 1000), store, Substitute.For<IPermitTypeStore>(), resolver,
            Substitute.For<IRegistrySnapshotLoader>(), Substitute.For<IDocumentHeaderStore>(), Substitute.For<IRegistryStore>(),
            Substitute.For<IPeriodStore>(), Substitute.For<ICellStore>(), Substitute.For<IAuditWriter>(), new TestClock(Now));

        var result = await service.MaterializeAsync(chain.DocumentId, chain.PeriodKey, [chain.TableDefId], default);

        Assert.Equal(PermitMaterialization.None, result);
        await store.DidNotReceive().ListActiveRowsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<PermitResolveRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_паралельні_заведення_екземплярів_створюють_рядки_з_дозволу_рівно_один_раз()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;

        var gate = new AsyncGate(2);

        async Task<int> EnsureAsync()
        {
            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(new GateBeforeInstanceSave(gate))
                .Options);
            return await NewStore(db, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, CancellationToken.None);
        }

        var results = await Task.WhenAll(Task.Run(EnsureAsync), Task.Run(EnsureAsync));

        Assert.Equal(1, results.Sum());
        var rows = await scene.RowsAsync();
        Assert.Equal(scene.Permit.RowEntryIds.Select(id => $"P{id}"), rows.Select(r => r.RowKey));
        Assert.Equal(8, await scene.PermitAuditCountAsync());
    }

    // ── Побудова ────────────────────────────────────────────────────────────

    private RowStore NewStore(EcrDbContext db, bool permits)
    {
        var clock = new TestClock(Now);
        return new RowStore(
            db, new BulkCellLoader(sql.ConnectionString, 1000), clock, permits: permits ? NewService(db) : null);
    }

    private PermitBindingService NewService(EcrDbContext db)
    {
        var registries = new RegistryStore(db);
        return new PermitBindingService(
            db, new BulkCellLoader(sql.ConnectionString, 1000), new PermitBindingStore(db), new PermitTypeStore(db),
            new PermitResolver(registries), new RegistrySnapshotLoader(db), new DocumentHeaderStore(db), registries,
            new PeriodStore(db), new NormalizedCellStore(db), new AuditWriter(db), new TestClock(Now));
    }

    /// <summary>Шаблон з прив'язкою, дозвіл з трьома рядками й документ без екземплярів таблиць.</summary>
    private async Task<Scene> ArrangeAsync(bool fromHeader = false, string facility = "Site A")
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync(Type);

        int? headerFieldId = null;
        HeaderFieldDef? permitRef = null;
        if (fromHeader)
        {
            var type = (await h.Types.Permits.FindByCodeAsync(Type, default))!;
            await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
            permitRef = new HeaderFieldDef(sheet.Doc.TemplateVersionId, EcrCode.Create("PermitRef"), PermitHarness.Text("Permit"), 2, CellDataType.Lookup);
            permitRef.SetLookup(type.HeaderRegistryDefId);
            db.HeaderFieldDefs.Add(permitRef);
            await db.SaveChangesAsync();
            headerFieldId = permitRef.Id;
        }

        var permit = await h.SeedPermitAsync(
            Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), null, true, ("NOx", 12.5m, "first"), ("SO2", 3m, null), ("CO", 7.25m, "third"));
        await h.SeedPermitAsync(Type, $"N2{_tag}", "Site B", new DateOnly(2026, 1, 1), null, true, ("OTHER", 2m, null));

        await using (var save = await h.NextRequestAsync())
        {
            await save.Save.HandleAsync(
                sheet.Doc.TemplateVersionId,
                $"B{_tag}",
                new SavePermitBindingRequest(
                    sheet.Doc.TableDefId, Type, "Emissions", "Rows",
                    fromHeader ? "FromHeaderLookup" : "ByFacilityPeriodEnd", headerFieldId, true,
                    [
                        new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                        new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
                        new PermitBindingColumnRequest("Info", "Note", sheet.NoteColumnId),
                    ]),
                default);
        }

        // Документ на цій версії; екземплярів таблиць ще немає - їх створить EnsureTableInstancesAsync.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var attach = builder.CreateContext();
        var policyId = await attach.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Project(
            EcrCode.Create($"PRJ{_tag}"), PermitHarness.Text($"Project {_tag}"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            sheet.Doc.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        attach.Projects.Add(project);
        await attach.SaveChangesAsync();
        attach.Periods.Add(new Period(project.Id, January, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        var document = new Document(project.Id, $"DOC{_tag}", 1, Now);
        attach.Documents.Add(document);
        await attach.SaveChangesAsync();
        attach.DocumentSheets.Add(new DocumentSheet(document.Id, sheet.Doc.SheetDefId));
        await attach.SaveChangesAsync();

        var values = new Dictionary<int, Ecr.Domain.ValueObjects.DocumentHeaderValueData>
        {
            [sheet.FacilityHeaderId] = new() { ValueString = facility },
        };
        if (permitRef is not null)
        {
            values[permitRef.Id] = new() { ValueRegistryEntryId = permit.EntryId };
        }

        await new DocumentHeaderStore(attach).SaveValuesAsync(document.Id, values, default);

        return new Scene(sql, h, sheet, document.Id, project.Id, permit);
    }

    private sealed class Scene(
        SqlServerFixture fixture, PermitBindingHarness h, PreparedSheet sheet, long documentId, int projectId, SeededPermit permit)
    {
        public PermitBindingHarness H { get; } = h;

        public PreparedSheet Sheet { get; } = sheet;

        public long DocumentId { get; } = documentId;

        public int ProjectId { get; } = projectId;

        public SeededPermit Permit { get; } = permit;

        public EcrDbContext NewContext() => new TestDocumentBuilder(fixture.ConnectionString).CreateContext();

        public async Task<List<RowInfo>> RowsAsync()
        {
            await using var db = NewContext();
            return await db.Database.SqlQuery<RowInfo>($"""
                SELECT r.Id AS Id, r.RowKey AS RowKey, r.Ordinal AS Ordinal
                FROM doc.TableRow r
                JOIN doc.TableInstance i ON i.Id = r.TableInstanceId AND i.PeriodKey = r.PeriodKey
                WHERE i.DocumentId = {DocumentId} AND r.IsDeleted = 0
                ORDER BY r.Ordinal
                """).ToListAsync();
        }

        public async Task<long> InstanceIdAsync()
        {
            await using var db = NewContext();
            return await db.TableInstances.AsNoTracking().Where(i => i.DocumentId == DocumentId).Select(i => i.Id).SingleAsync();
        }

        public async Task<Dictionary<int, CellInfo>> CellsAsync(long rowId)
        {
            await using var db = NewContext();
            var cells = await db.Database.SqlQuery<CellInfo>($"""
                SELECT c.ColumnDefId AS ColumnDefId, c.ValueString AS Text, c.ValueNumeric AS Number
                FROM doc.CellValue c WHERE c.PeriodKey = {January.Value} AND c.TableRowId = {rowId}
                """).ToListAsync();
            return cells.ToDictionary(c => c.ColumnDefId);
        }

        public async Task<int> PermitAuditCountAsync()
        {
            await using var db = NewContext();
            return await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM aud.CellChange WHERE DocumentId = {DocumentId} AND Origin = N'Permit'
                """).SingleAsync();
        }

        public async Task ExecuteAsync(string text)
        {
            await using var db = NewContext();
            await db.Database.ExecuteSqlRawAsync(text);
        }
    }

    private sealed class RowInfo
    {
        public long Id { get; set; }

        public string RowKey { get; set; } = "";

        public int Ordinal { get; set; }
    }

    private sealed class CellInfo
    {
        public int ColumnDefId { get; set; }

        public string? Text { get; set; }

        public decimal? Number { get; set; }
    }

    /// <summary>Асинхронний бар'єр на N учасників.</summary>
    private sealed class AsyncGate(int parties)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= parties)
            {
                _open.TrySetResult();
            }

            return _open.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Затримує збереження, що додає екземпляри таблиць, доки не дійшли всі учасники.</summary>
    private sealed class GateBeforeInstanceSave(AsyncGate gate) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TableInstance>().Any(e => e.State == EntityState.Added))
            {
                await gate.ArriveAsync().ConfigureAwait(false);
            }

            return result;
        }
    }
}
