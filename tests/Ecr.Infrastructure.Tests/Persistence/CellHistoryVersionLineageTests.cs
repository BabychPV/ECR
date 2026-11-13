using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// V-4 (аудит 2026-11, L26): історія комірки й журнал документа НЕ обриваються на переносі проєкту між версіями
/// шаблону. Перенос переселяє комірку на <c>ColumnDefId</c> цільової версії, а <c>aud.CellChange</c> незмінний і
/// лишається на Id старої — читання перекладає їх за шляхом «аркуш → таблиця → колонка» (<see cref="ColumnPathMapper.LineageAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Стан «після переносу» відтворено напряму, без ендпоінта переносу: документ живе на версії V, а рядки журналу
/// лежать на колонках іншої версії P того самого шаблону з тими самими кодами аркуша/таблиці/колонки — рівно те,
/// що лишає перенос (<c>DocumentVersionMigrationStore</c>: «aud.* — незмінний журнал»). Ще в P є таблиця з ІНШИМ
/// кодом і колонкою з ТИМ САМИМ кодом, що й C1, — її правка в історію C1 потрапити не має (шлях, а не код).
/// </para>
/// <para>
/// Справжні <see cref="AuditReader"/>, <see cref="ColumnPathMapper"/>, <see cref="MetadataCache"/> і межі читання
/// справжньої <see cref="AccessDecisionService"/>; підмінено лише побудову профілю. Без фіксу перші два тести
/// червоні: історія C1 — лише «8», журнал документа — лише «8» (рядки на Id версії P відсіяні як «невидимі»).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CellHistoryVersionLineageTests(SqlServerFixture sql) : IDisposable
{
    private const int UserId = 1;
    private const int Key = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    /// <summary>
    /// Документ на версії V (колонки C0, C1) і колонки попередньої версії P: <c>P0</c>/<c>P1</c> — ті самі шляхи,
    /// <c>PX</c> — код C1 в іншій таблиці P. Ланцюжок перейменувань: <c>RR</c> (версія R) → <c>QR</c> (версія Q) → C1.
    /// </summary>
    private sealed record World(
        TestDocument Doc, string RowKey, int C0, int C1, int P0, int P1, int PX, int QR, int RR);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Історія_комірки_після_переносу_показує_зміни_до_нього()
    {
        var world = await ArrangeAsync();
        await AuditAsync(world);

        var page = await HistoryAsync(world, world.C1, Reader(world));

        // Нова правка (на Id версії V) і правка до переносу (на Id тієї самої колонки у версії P), новіша першою.
        Assert.Equal(["8", "7"], page.Items.Select(c => c.NewValue));
        Assert.Equal([world.C1, world.P1], page.Items.Select(c => c.ColumnDefId));

        // ⛔ Той самий код колонки в ІНШІЙ таблиці («99») і сусідня колонка («a») — не ця комірка.
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Журнал_документа_після_переносу_показує_зміни_до_нього()
    {
        var world = await ArrangeAsync();
        await AuditAsync(world);

        var auditor = Reader(world).Permission(GetCellChangesHandler.Permission);
        var page = await LogAsync(world, auditor);

        Assert.Equal(["8", "7", "a"], page.Items.Select(c => c.NewValue));
        Assert.Equal(3, page.TotalCount);

        // ⛔ Таблиці з кодом PX у поточній версії немає — її правка не належить жодній видимій колонці.
        Assert.DoesNotContain(page.Items, c => c.ColumnDefId == world.PX);

        var summary = await SummaryAsync(world, auditor);
        Assert.Equal(3L, summary.Total);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Deny_колонки_ховає_і_її_зміни_до_переносу()
    {
        var world = await ArrangeAsync();
        await AuditAsync(world);

        // Той самий запит, різниться лише Deny C1: родовід розширюється лише від ВИДИМИХ колонок.
        var auditor = Reader(world).Permission(GetCellChangesHandler.Permission).Deny(ResourceKind.Column, world.C1);
        var page = await LogAsync(world, auditor);

        Assert.Equal(["a"], page.Items.Select(c => c.NewValue));
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1L, (await SummaryAsync(world, auditor)).Total);

        var cell = await HistoryAsync(world, world.C1, Reader(world).Deny(ResourceKind.Column, world.C1));
        Assert.Empty(cell.Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Родовід_за_шляхом_і_ланцюжком_перейменувань()
    {
        var world = await ArrangeAsync();

        await using var db = CreateContext();
        var lineage = await new ColumnPathMapper(db).LineageAsync([world.C0, world.C1, world.P1, int.MaxValue], CancellationToken.None);

        Assert.Equal(new[] { world.C0, world.P0 }.Order(), lineage[world.C0]);

        // C1: той самий шлях у P і ланцюжок RR → QR → C1 (транзитивно); PX (той самий код, інша таблиця) — ні.
        Assert.Equal(new[] { world.C1, world.P1, world.QR, world.RR }.Order(), lineage[world.C1]);

        // Симетрично: зі старої версії видно нову; невідомий Id — лише він сам.
        Assert.Contains(world.C1, lineage[world.P1]);
        Assert.Equal([int.MaxValue], lineage[int.MaxValue]);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static AccessBuilder Reader(World world)
        => new AccessBuilder { UserId = UserId }
            .Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write)
            .Permission(GetCellChangesHandler.CellHistoryPermission);

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(Key, columnCount: 2, rowCount: 1);
        var tag = doc.SheetCode["SHEET".Length..];

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        await db.SaveChangesAsync();

        var tableCode = await db.TableDefs.AsNoTracking().Where(t => t.Id == doc.TableDefId).Select(t => t.Code).SingleAsync();
        var codes = await db.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code);
        var rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();
        var c0 = doc.ColumnDefIds[0];
        var c1 = doc.ColumnDefIds[1];

        // Попередня версія P: той самий аркуш і таблиця з тими самими колонками, плюс інша таблиця з колонкою коду C1.
        var pSheet = await VersionSheetAsync(db, doc, $"0.9.0.{tag}");
        var p = await TableAsync(db, pSheet, tableCode, codes[c0], codes[c1]);
        var px = (await TableAsync(db, pSheet, $"OT_{tag}", codes[c1]))[0];

        // Ланцюжок перейменувань: колонка QR версії Q стала C1, а RR версії R — QR.
        var qr = (await TableAsync(db, await VersionSheetAsync(db, doc, $"0.8.0.{tag}"), tableCode, $"OLD_{tag}"))[0];
        var rr = (await TableAsync(db, await VersionSheetAsync(db, doc, $"0.7.0.{tag}"), tableCode, $"OLDER_{tag}"))[0];
        db.ColumnRenames.Add(new ColumnRename(qr, c1));
        db.ColumnRenames.Add(new ColumnRename(rr, qr));
        await db.SaveChangesAsync();

        return new World(doc, rowKey, c0, c1, p[0], p[1], px, qr, rr);
    }

    /// <summary>Нова версія шаблону документа з аркушем того самого коду; повертає Id аркуша.</summary>
    private static async Task<int> VersionSheetAsync(EcrDbContext db, TestDocument doc, string version)
    {
        var v = new TemplateVersion(doc.TemplateId, version, 1, Now, doc.TemplateVersionId);
        db.TemplateVersions.Add(v);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(v.Id, EcrCode.Create(doc.SheetCode), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        return sheet.Id;
    }

    /// <summary>Таблиця з текстовими колонками в аркуші; повертає Id колонок у порядку кодів.</summary>
    private static async Task<IReadOnlyList<int>> TableAsync(
        EcrDbContext db, int sheetId, string tableCode, params string[] columnCodes)
    {
        var table = new TableDef(
            sheetId, EcrCode.Create(tableCode), Name("Table"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var columns = columnCodes
            .Select((code, i) => new ColumnDef(table.Id, EcrCode.Create(code), Name(code), i + 1, CellDataType.String))
            .ToList();
        db.ColumnDefs.AddRange(columns);
        await db.SaveChangesAsync();

        return [.. columns.Select(c => c.Id)];
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>
    /// Журнал документа після переносу: «7» на C1 версії P (до переносу), «8» на C1 версії V (після), «a» на C0
    /// версії P, «99» на колонці коду C1 в іншій таблиці P.
    /// </summary>
    private async Task AuditAsync(World world)
    {
        await using var db = CreateContext();
        foreach (var (column, value, at) in new[]
                 {
                     (world.P0, "a", Now.AddDays(-4)),
                     (world.PX, "99", Now.AddDays(-3)),
                     (world.P1, "7", Now.AddDays(-2)),
                     (world.C1, "8", Now.AddDays(-1)),
                 })
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO aud.CellChange
                    (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin)
                VALUES ({at}, {Key}, {world.Doc.DocumentId}, {world.Doc.RowIds[0]}, {world.RowKey}, {column},
                        NULL, {value}, {UserId}, N'UserEdit')
                """);
        }
    }

    private Task<PagedResult<CellChangeView>> HistoryAsync(World world, int columnDefId, AccessBuilder reader)
        => WithHandlerAsync(reader, h => h.HandleAsync(
            new CellChangeFilter(Now.AddDays(-30), Now.AddDays(1), world.Doc.DocumentId, world.RowKey, columnDefId, PeriodKey: Key),
            new CursorRequest(50),
            CancellationToken.None));

    private Task<PagedResult<CellChangeView>> LogAsync(World world, AccessBuilder reader)
        => WithHandlerAsync(reader, h => h.HandleAsync(
            new CellChangeFilter(Now.AddDays(-30), Now.AddDays(1), world.Doc.DocumentId),
            new CursorRequest(50),
            CancellationToken.None));

    private Task<CellChangeSummaryView> SummaryAsync(World world, AccessBuilder reader)
        => WithHandlerAsync(reader, h => h.SummaryAsync(
            new CellChangeFilter(Now.AddDays(-30), Now.AddDays(1), world.Doc.DocumentId),
            CancellationToken.None));

    private async Task<T> WithHandlerAsync<T>(AccessBuilder reader, Func<GetCellChangesHandler, Task<T>> act)
    {
        await using var db = CreateContext();
        var metadata = new MetadataCache(_memory, db);
        var real = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), new TestClock(Now), Substitute.For<ICurrentUser>(), new WorkflowStore(db));
        var profile = reader.Build();

        // Межі читання — справжні; підмінено лише побудову профілю (ролей у базі тут немає).
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanReadDocumentAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));
        access.DocumentProjectIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.DocumentProjectIdAsync(c.ArgAt<long>(0), c.ArgAt<CancellationToken>(1)));
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.ReadScopeAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var handler = new GetCellChangesHandler(
            new AuditReader(db), access, user, clock: null, metadata: metadata, columns: new ColumnPathMapper(db));

        return await act(handler);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
