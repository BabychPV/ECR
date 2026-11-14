using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Documents;
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
/// B73 (V-4 для журналу аркуша): журнал дій аркуша НЕ обривається на переносі проєкту між версіями шаблону.
/// <c>aud.CellChange</c> незмінний і лишається на Id колонок старої версії; межі журналу — видимі колонки поточної версії
/// разом з їхнім родоводом (<see cref="ColumnPathMapper.LineageAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Стан «після переносу» відтворено напряму (як <see cref="CellHistoryVersionLineageTests"/>): документ на версії V, рядки
/// журналу — на колонках попередньої версії P того самого шаблону з тими самими кодами. У P є ще таблиця з ІНШИМ кодом і
/// колонкою коду C1 — її правка в журнал аркуша не потрапляє (шлях, а не код).
/// </para>
/// <para>
/// Справжні <see cref="GetSheetActivityHandler"/>, <see cref="SheetActivityQuery"/>, <see cref="AuditReader"/>,
/// <see cref="ColumnPathMapper"/> і межі читання <see cref="AccessDecisionService"/>; підмінено лише побудову профілю.
/// Без фіксу перший тест червоний: журнал — лише «8».
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetActivityVersionLineageTests(SqlServerFixture sql) : IDisposable
{
    private const int UserId = 1;

    /// <summary>Тестовий період далеко від реальних (правило: 2099xx).</summary>
    private const int Key = 209910;

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    private sealed record World(TestDocument Doc, string RowKey, int C0, int C1, int P0, int P1, int PX);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Журнал_аркуша_після_переносу_показує_правки_до_нього()
    {
        var world = await ArrangeAsync();
        await AuditAsync(world);

        var items = await ActivityAsync(world, Reader(world));

        // Нова правка (Id версії V), правки до переносу (Id тих самих колонок у P); найновіша першою.
        Assert.Equal(["8", "7", "a"], items.Select(i => i.NewValue));
        Assert.Equal([world.C1, world.P1, world.P0], items.Select(i => i.ColumnDefId!.Value));

        // ⛔ Той самий код колонки в ІНШІЙ таблиці версії P («99») — не правка цього аркуша.
        Assert.DoesNotContain(items, i => i.ColumnDefId == world.PX);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Deny_колонки_ховає_і_її_правки_до_переносу_диференційно()
    {
        var world = await ArrangeAsync();
        await AuditAsync(world);

        // Той самий запит; різниться лише Deny C1: родовід розширюється лише від ВИДИМИХ колонок.
        var allowed = await ActivityAsync(world, Reader(world));
        var denied = await ActivityAsync(world, Reader(world).Deny(ResourceKind.Column, world.C1));

        Assert.Contains(allowed, i => i.ColumnDefId == world.P1);
        Assert.Equal(["a"], denied.Select(i => i.NewValue));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static AccessBuilder Reader(World world)
        => new AccessBuilder { UserId = UserId }
            .Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write)
            .Permission(GetSheetActivityHandler.Permission);

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
        var v = new TemplateVersion(doc.TemplateId, $"0.9.0.{tag}", 1, DateTime.UtcNow, doc.TemplateVersionId);
        db.TemplateVersions.Add(v);
        await db.SaveChangesAsync();
        var pSheet = new SheetDef(v.Id, EcrCode.Create(doc.SheetCode), Name("Sheet"), 1);
        db.SheetDefs.Add(pSheet);
        await db.SaveChangesAsync();

        var p = await TableAsync(db, pSheet.Id, tableCode, codes[c0], codes[c1]);
        var px = (await TableAsync(db, pSheet.Id, $"OT_{tag}", codes[c1]))[0];

        return new World(doc, rowKey, c0, c1, p[0], p[1], px);
    }

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
    /// Журнал після переносу (людські правки, у вікні журналу аркуша від поточного моменту): «a» на C0 версії P,
    /// «99» на колонці коду C1 в іншій таблиці P, «7» на C1 версії P, «8» на C1 версії V.
    /// </summary>
    /// <remarks>
    /// Прибирання (retro 375): <c>aud.CellChange</c> незмінний (тригер), тож рядки не видаляються — вони лежать на
    /// власному <c>DocumentId</c> і періоді 2099, а час — ~300 днів тому (усередині вікна журналу аркуша, 396 днів, але
    /// далеко від «останніх змін», які читають інші тести спільної бази). Інший стан тест не змінює: версії шаблону й
    /// склад — лише свого шаблону й документа.
    /// </remarks>
    private async Task AuditAsync(World world)
    {
        var now = DateTime.UtcNow.AddDays(-296);
        await using var db = CreateContext();
        foreach (var (column, value, at) in new[]
                 {
                     (world.P0, "a", now.AddDays(-4)),
                     (world.PX, "99", now.AddDays(-3)),
                     (world.P1, "7", now.AddDays(-2)),
                     (world.C1, "8", now.AddDays(-1)),
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

    private async Task<IReadOnlyList<SheetActivityItem>> ActivityAsync(World world, AccessBuilder reader)
    {
        await using var db = CreateContext();
        var metadata = new MetadataCache(_memory, db);
        var real = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), new TestClock(DateTime.UtcNow), Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));
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

        var handler = new GetSheetActivityHandler(
            new GetDocumentHandler(new DocumentStore(db), access, user),
            new SheetActivityQuery(new AuditReader(db), new WorkflowStore(db), clock: null, rowChanges: null),
            access, user, monthRouter: null, metadata: metadata, columnPaths: new ColumnPathMapper(db));

        var page = await handler.HandleAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, Key, byUserId: null, includeSystem: false, limit: 100,
            cursor: null, CancellationToken.None);
        return [.. page.Items.Where(i => i.Kind == SheetActivityKinds.CellEdit)];
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
