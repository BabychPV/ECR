using Ecr.Application.Ports;
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
/// B49 (борг V-4): ще три читання журналу після переносу проєкту між версіями шаблону — остання зміна комірки для
/// діалогу конфлікту (<see cref="AuditReader.ReadLastChangesAsync"/>), значок «правка поза вікном»
/// (<see cref="AuditReader.ReadOutOfWindowCellsAsync"/>) і останній запис комірки в інспекторі джерела
/// (<see cref="CellSourceStore.LastWriteAsync"/>). Перенос переселяє комірку на <c>ColumnDefId</c> цільової версії, а
/// <c>aud.CellChange</c> лишається на Id старої — читання мусять іти родоводом колонки
/// (<see cref="ColumnPathMapper.LineageAsync"/>), а не лише поточним Id.
/// </summary>
/// <remarks>
/// Стан «після переносу» — як у <see cref="CellHistoryVersionLineageTests"/>: документ на версії V, рядки журналу на
/// колонках попередньої версії P з тими самими кодами аркуша/таблиці/колонки, плюс таблиця P з іншим кодом і колонкою
/// коду C1 (<c>PX</c>) — її правка до комірки C1 не належить (шлях, а не код). Без фіксу всі три тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditVersionLineageReadsTests(SqlServerFixture sql)
{
    private const int UserId = 1;
    private const int Key = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Документ на V (C0, C1; два рядки) і колонки версії P: P0/P1 — ті самі шляхи, PX — код C1 в іншій таблиці.</summary>
    private sealed record World(TestDocument Doc, IReadOnlyList<string> RowKeys, int C0, int C1, int P0, int P1, int PX);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Остання_зміна_для_конфлікту_бачить_правку_до_переносу()
    {
        var world = await ArrangeAsync();
        await AuditAsync(world, (0, world.P1, "7", Now.AddDays(-2), false), (0, world.PX, "99", Now.AddDays(-1), false));

        await using var db = CreateContext();
        var last = await new AuditReader(db).ReadLastChangesAsync(
            world.Doc.DocumentId,
            [(world.Doc.RowIds[0], world.C1), (world.Doc.RowIds[0], world.C0)],
            Now.AddDays(-30),
            CancellationToken.None);

        // Ключ — запитана (поточна) адреса; момент — правки «7» на Id версії P. Новіша «99» (PX) — не ця комірка.
        var change = Assert.Single(last);
        Assert.Equal((world.Doc.RowIds[0], world.C1), change.Key);
        Assert.Equal(Now.AddDays(-2), change.Value.ChangedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Значок_поза_вікном_іде_за_колонкою_через_перенос()
    {
        var world = await ArrangeAsync();
        await AuditAsync(
            world,

            // Рядок 0: правка поза вікном до переносу, після — нічого. Значок є — на ПОТОЧНІЙ колонці C1.
            (0, world.P1, "7", Now.AddDays(-2), true),

            // Рядок 1: поза вікном до переносу, звичайна правка C1 після. Остання — звичайна: значка немає.
            (1, world.P1, "5", Now.AddDays(-3), true),
            (1, world.C1, "6", Now.AddDays(-1), false));

        await using var db = CreateContext();
        var cells = await new AuditReader(db).ReadOutOfWindowCellsAsync(world.Doc.DocumentId, Key, CancellationToken.None);

        Assert.Equal([(world.Doc.RowIds[0], world.C1)], cells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Останній_запис_у_джерелі_комірки_бачить_правку_до_переносу()
    {
        var world = await ArrangeAsync();
        await AuditAsync(world, (0, world.P1, "7", Now.AddDays(-2), false), (0, world.PX, "99", Now.AddDays(-1), false));

        await using var db = CreateContext();
        var store = new CellSourceStore(db, Substitute.For<IUnitCatalog>());
        var write = await store.LastWriteAsync(
            world.Doc.DocumentId, world.Doc.TableDefId, world.RowKeys[0], world.C1, [Key], CancellationToken.None);

        Assert.NotNull(write);
        Assert.Equal("7", write.NewValue);

        // Сусідня колонка C0 правок не має.
        Assert.Null(await store.LastWriteAsync(
            world.Doc.DocumentId, world.Doc.TableDefId, world.RowKeys[0], world.C0, [Key], CancellationToken.None));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(Key, columnCount: 2, rowCount: 2);
        var tag = doc.SheetCode["SHEET".Length..];

        await using var db = builder.CreateContext();
        var tableCode = await db.TableDefs.AsNoTracking().Where(t => t.Id == doc.TableDefId).Select(t => t.Code).SingleAsync();
        var codes = await db.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code);
        var rowKeys = new List<string>();
        foreach (var rowId in doc.RowIds)
        {
            rowKeys.Add(await db.TableRows.AsNoTracking().Where(r => r.Id == rowId).Select(r => r.RowKeyValue).SingleAsync());
        }

        var c0 = doc.ColumnDefIds[0];
        var c1 = doc.ColumnDefIds[1];

        var pSheet = await VersionSheetAsync(db, doc, $"0.9.0.{tag}");
        var p = await TableAsync(db, pSheet, tableCode, codes[c0], codes[c1]);
        var px = (await TableAsync(db, pSheet, $"OT_{tag}", codes[c1]))[0];

        return new World(doc, rowKeys, c0, c1, p[0], p[1], px);
    }

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

    private async Task AuditAsync(World world, params (int Row, int Column, string Value, DateTime At, bool OutOfWindow)[] changes)
    {
        await using var db = CreateContext();
        foreach (var (row, column, value, at, outOfWindow) in changes)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO aud.CellChange
                    (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin,
                     IsOutOfWindow)
                VALUES ({at}, {Key}, {world.Doc.DocumentId}, {world.Doc.RowIds[row]}, {world.RowKeys[row]}, {column},
                        NULL, {value}, {UserId}, N'UserEdit', {outOfWindow})
                """);
        }
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
