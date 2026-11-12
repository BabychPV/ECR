using System.Text.Json;
using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Ports;
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
/// Q-17 (LAND-YEAR G2): CSV-експорт журналу змін комірок за документом і періодом (<c>GET audit/cells/export.csv</c>)
/// містить і зміни колонок, що лежать у періоді-ВЛАСНИКУ (не-місячна — якір 202601; M{m} — період m), а подія експорту
/// пише ЗАПИТАНИЙ період.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Дефект: фільтр документ + період був фізичним (<c>aud.CellChange.PeriodKey = 202610</c>) — правка не-місячної
/// колонки з жовтня (лежить у якорі) у файлі жовтня була відсутня.
/// </para>
/// <para>
/// Справжній шлях: <see cref="ExportCellChangesHandler"/> → <see cref="GetCellChangesHandler"/> над справжніми
/// <see cref="AuditReader"/>, <see cref="MonthColumnRouter"/>, <c>MetadataCache</c> і межами читання
/// <c>AccessDecisionService</c>; підмінено лише запис події безпеки (її перевіряє тест).
/// </para>
/// <para>
/// ⚠ МУТАЦІЙНИЙ ДОКАЗ (локально, не в коміті): <c>AddOwnerPeriodColumnsAsync</c> повертає фільтр без змін — червоніє
/// <see cref="Експорт_жовтня_містить_правки_з_власника_а_подія_пише_жовтень"/>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditExportMonthRoutedTests(SqlServerFixture sql)
{
    private const int Author = 1;
    private const int Oct = MonthRoutedReadWorld.Oct;
    private const int Jan = MonthRoutedReadWorld.Jan;
    private const int Nov = MonthRoutedReadWorld.Nov;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Експорт_жовтня_містить_правки_з_власника_а_подія_пише_жовтень()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);
        await world.PatchAsync(Author, Oct, ("PLAIN", 7m), ("M11", 5m), ("M10", 3m));

        var events = new List<SecurityEventRecord>();
        var (total, rows) = await ExportAsync(world, Oct, Reader(world), events);

        var byColumn = rows.ToDictionary(r => r.ColumnDefId, r => (r.PeriodKey, Value: MonthRoutedReadWorld.Trim(r.NewValue)));
        Assert.Equal((Jan, "7"), byColumn[world.Plain]);
        Assert.Equal((Nov, "5"), byColumn[world.Month(11)]);
        Assert.Equal((Oct, "3"), byColumn[world.Month(10)]);
        Assert.Equal(3, total);

        // ⛔ Подія експорту — чесна: запитаний період (жовтень), а не власники.
        var exported = Assert.Single(events);
        Assert.Equal(ExportCellChangesHandler.ExportedEventType, exported.EventType);
        using var details = JsonDocument.Parse(exported.DetailsJson!);
        Assert.Equal(Oct, details.RootElement.GetProperty("periodKey").GetInt32());
        Assert.Equal(3, details.RootElement.GetProperty("rows").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Deny_колонки_ховає_правку_з_власника_в_експорті_диференційно()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);
        await world.PatchAsync(Author, Oct, ("PLAIN", 7m), ("M11", 5m));

        // Той самий експорт (жовтень), різниться лише Deny колонки PLAIN.
        var (allowedTotal, allowed) = await ExportAsync(world, Oct, Reader(world), []);
        var (deniedTotal, denied) = await ExportAsync(world, Oct, Reader(world).Deny(ResourceKind.Column, world.Plain), []);

        Assert.Contains(allowed, r => r.ColumnDefId == world.Plain);
        Assert.DoesNotContain(denied, r => r.ColumnDefId == world.Plain);
        Assert.Contains(denied, r => r.ColumnDefId == world.Month(11) && r.PeriodKey == Nov);
        Assert.Equal(allowedTotal - 1, deniedTotal);
    }

    /// <summary>Контроль: маршрутизація вимкнена — експорт фізичний, як був, і в порт іде фільтр без пар власників.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Без_маршрутизації_експорт_і_запит_як_були()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString, routing: false);
        await world.PatchAsync(Author, Oct, ("PLAIN", 7m));

        var filters = new List<CellChangeFilter>();
        var (_, rows) = await ExportAsync(world, Oct, Reader(world), [], filters);

        var row = Assert.Single(rows);
        Assert.Equal((world.Plain, Oct), (row.ColumnDefId, row.PeriodKey));

        // Одна сторінка + один підрахунок (як до фіксу), обидва — з фізичним фільтром періоду.
        Assert.Equal(2, filters.Count);
        Assert.All(filters, f => Assert.Null(f.OwnerPeriodColumns));
        Assert.All(filters, f => Assert.Equal(Oct, f.PeriodKey));
    }

    /// <summary>
    /// V-4 × Q-17: правка колонки-власника, зроблена ДО переносу проєкту (лежить у якорі 202601 на Id колонки PLAIN
    /// попередньої версії шаблону), — в експорті жовтня; та сама колонка в чужому періоді (202603) — ні.
    /// </summary>
    /// <remarks>
    /// ⚠ МУТАЦІЙНИЙ ДОКАЗ (локально, не в коміті): прибрати розширення пар родоводом в <c>AddOwnerPeriodColumnsAsync</c> —
    /// рядка «6» немає, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Правка_власника_до_переносу_версії_є_в_експорті_і_ховається_Deny()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);
        await world.PatchAsync(Author, Oct, ("PLAIN", 7m));
        var previousPlain = await PreviousVersionPlainAsync(world);
        await AuditOnColumnAsync(world, previousPlain, Jan, "6", MonthRoutedReadWorld.Now.AddDays(-2));
        await AuditOnColumnAsync(world, previousPlain, 202603, "4", MonthRoutedReadWorld.Now.AddDays(-3));

        var (total, rows) = await ExportAsync(world, Oct, Reader(world), []);

        var old = Assert.Single(rows, r => r.ColumnDefId == previousPlain);
        Assert.Equal((Jan, "6"), (old.PeriodKey, old.NewValue));
        Assert.Contains(rows, r => r.ColumnDefId == world.Plain && r.PeriodKey == Jan);
        Assert.Equal(2, total);

        // Диференційно: той самий експорт, лише Deny PLAIN — родовід не розширюється від прихованої колонки.
        var (deniedTotal, denied) = await ExportAsync(
            world, Oct, Reader(world).Deny(ResourceKind.Column, world.Plain), []);
        Assert.DoesNotContain(denied, r => r.ColumnDefId == previousPlain || r.ColumnDefId == world.Plain);
        Assert.Equal(0, deniedTotal);
    }

    /// <summary>Попередня версія шаблону документа: той самий аркуш, таблиця й колонка PLAIN; повертає Id колонки.</summary>
    private static async Task<int> PreviousVersionPlainAsync(MonthRoutedReadWorld world)
    {
        await using var db = world.CreateContext();
        var doc = world.Doc;
        var tableCode = await db.TableDefs.AsNoTracking().Where(t => t.Id == doc.TableDefId).Select(t => t.Code).SingleAsync();
        var tag = doc.SheetCode["SHEET".Length..];

        var version = new TemplateVersion(doc.TemplateId, $"0.9.0.{tag}", 1, MonthRoutedReadWorld.Now, doc.TemplateVersionId);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create(doc.SheetCode), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create(tableCode), Name("Table"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create(world.Codes[1]), Name("Plain"), 1, CellDataType.String);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();
        return column.Id;
    }

    /// <summary>Рядок незмінного журналу на колонці <paramref name="columnDefId"/> (як його лишає перенос проєкту).</summary>
    private static async Task AuditOnColumnAsync(
        MonthRoutedReadWorld world, int columnDefId, int periodKey, string value, DateTime at)
    {
        await using var db = world.CreateContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin)
            VALUES ({at}, {periodKey}, {world.Doc.DocumentId}, {world.Doc.RowIds[0]}, {world.RowKey}, {columnDefId},
                    NULL, {value}, {Author}, N'UserEdit')
            """);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessBuilder Reader(MonthRoutedReadWorld world)
        => world.Profile(Author, GetCellChangesHandler.Permission);

    private static async Task<(int Total, List<CellChangeView> Rows)> ExportAsync(
        MonthRoutedReadWorld world, int periodKey, AccessBuilder reader, List<SecurityEventRecord> events,
        List<CellChangeFilter>? filters = null)
    {
        await using var db = world.CreateContext();
        var router = world.Router(db);
        var metadata = world.Metadata(db);
        var access = world.Access(db, reader.Build(), metadata, router);
        var user = MonthRoutedReadWorld.User(Author);

        // Порт журналу — справжній; обгортка лише записує фільтри.
        var real = new AuditReader(db);
        var audit = Substitute.For<IAuditReader>();
        audit.ReadCellChangesAsync(Arg.Any<CellChangeFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                filters?.Add(c.ArgAt<CellChangeFilter>(0));
                return real.ReadCellChangesAsync(c.ArgAt<CellChangeFilter>(0), c.ArgAt<CursorRequest>(1), c.ArgAt<CancellationToken>(2));
            });
        audit.CountCellChangesByColumnAsync(Arg.Any<CellChangeFilter>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                filters?.Add(c.ArgAt<CellChangeFilter>(0));
                return real.CountCellChangesByColumnAsync(c.ArgAt<CellChangeFilter>(0), c.ArgAt<DateTime>(1), c.ArgAt<CancellationToken>(2));
            });

        var writer = Substitute.For<IAuditWriter>();
        writer.WriteIndependentSecurityEventAsync(Arg.Do<SecurityEventRecord>(events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var export = new ExportCellChangesHandler(
            new GetCellChangesHandler(
                audit, access, user, clock: null, monthRouter: router, metadata: metadata, columns: new ColumnPathMapper(db)),
            writer, new TestClock(MonthRoutedReadWorld.Now), user);

        var prepared = await export.PrepareAsync(
            new CellChangeFilter(
                MonthRoutedReadWorld.Now.AddDays(-30), MonthRoutedReadWorld.Now.AddDays(1), world.Doc.DocumentId,
                PeriodKey: periodKey),
            maxRows: 1000,
            CancellationToken.None);

        var rows = new List<CellChangeView>();
        await foreach (var row in prepared.Rows)
        {
            rows.Add(row);
        }

        return (prepared.Total, rows);
    }
}
