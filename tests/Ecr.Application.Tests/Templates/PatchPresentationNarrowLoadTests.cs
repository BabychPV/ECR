using System.Collections.Concurrent;
using System.Data.Common;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// B169: <c>PATCH …/presentation</c> однієї таблиці читає колонки лише ЗАЧЕПЛЕНИХ таблиць, а не всієї версії.
/// На LAND_UI (90 таблиць, ~25 тис. <c>ColumnDef</c>) перевірка груп заголовків читала всі колонки версії на кожен
/// виклик і під навантаженням виходила за CommandTimeout 60 с (503). Замір — рядки, які EF реально прочитав із
/// <c>cfg.ColumnDef</c> / <c>cfg.TableDef</c> (<c>DataReaderClosingEventData.ReadCount</c>), на реальному SQL.
/// </summary>
[Collection("SqlServer")]
public sealed class PatchPresentationNarrowLoadTests(SqlServerFixture sql)
{
    private const int TableCount = 24;
    private const int ColumnsPerTable = 12;
    private const int GroupedColumns = 4;

    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Ecr.Application.Ports.IMetadataCache _cache = Substitute.For<Ecr.Application.Ports.IMetadataCache>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Patch_of_one_table_reads_only_that_tables_columns_not_the_whole_version()
    {
        var v = await VersionAsync();
        var table = v.Tables[0];
        var next = table.Columns[GroupedColumns]; // одразу після згрупованих — суміжна, правило груп виконане
        var last = table.Columns[^1];

        var rows = new RowCounter();
        await using (var db = Context(rows))
        {
            var revision = await Handler(db).PatchAsync(
                v.VersionId,
                $$"""
                [{"entityType":"ColumnDef","entityId":{{next}},"field":"HeaderGroupId","value":"{{table.GroupId}}"},
                 {"entityType":"ColumnDef","entityId":{{last}},"field":"Ordinal","value":"{{ColumnsPerTable + 5}}"}]
                """,
                9, CancellationToken.None);
            Assert.Equal(1, revision);
        }

        var columnRows = rows.Of("SELECT cfg.ColumnDef");
        var tableRows = rows.Of("SELECT cfg.TableDef");

        // ⛔ Межа не залежить від кількості таблиць версії: колонки однієї таблиці + малий запас. До B169 тут було
        // TableCount × ColumnsPerTable = 288 рядків (усі таблиці мають групу, тож усі їхні колонки йшли в перевірку).
        Assert.True(columnRows <= ColumnsPerTable + 2, $"cfg.ColumnDef rows read: {columnRows}\n{rows.Format()}");
        Assert.True(tableRows <= 2, $"cfg.TableDef rows read: {tableRows}\n{rows.Format()}");

        await using var verify = Context();
        Assert.Equal(table.GroupId, (await verify.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == next)).HeaderGroupId);
        Assert.Equal(ColumnsPerTable + 5, (await verify.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == last)).Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Patch_touching_two_tables_still_validates_each_touched_table_and_rolls_back()
    {
        var v = await VersionAsync();
        var a = v.Tables[0];
        var b = v.Tables[TableCount - 1];

        // A: законна правка; B: колонка після пропуску в групу — розрив суміжності саме в другій таблиці.
        await using (var db = Context())
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler(db).PatchAsync(
                v.VersionId,
                $$"""
                [{"entityType":"ColumnDef","entityId":{{a.Columns[GroupedColumns]}},"field":"HeaderGroupId","value":"{{a.GroupId}}"},
                 {"entityType":"ColumnDef","entityId":{{b.Columns[GroupedColumns + 2]}},"field":"HeaderGroupId","value":"{{b.GroupId}}"}]
                """,
                9, CancellationToken.None));

            Assert.Equal("ECR-TMPL-4229", ex.ErrorCode);
            Assert.Equal("err.ECR-TMPL-4229.notContiguous", ex.Details?["messageKey"]);
            Assert.Equal(b.Code, ex.Details?["tableCode"]);
        }

        await using var verify = Context();
        Assert.Null((await verify.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == a.Columns[GroupedColumns])).HeaderGroupId);
        Assert.Null((await verify.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == b.Columns[GroupedColumns + 2])).HeaderGroupId);
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────

    private void Allow()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());
    }

    private PatchPresentationHandler Handler(EcrDbContext db)
    {
        Allow();
        return new PatchPresentationHandler(
            new Repository<TemplateVersion, int>(db), new TemplateVersionStore(db), new ChangeClassifier(), _cache,
            new AuditWriter(db), new UnitOfWork(db), new TestClock(Now), _access, _user);
    }

    private EcrDbContext Context(params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(interceptors)
            .Options);

    /// <summary>
    /// Версія з <see cref="TableCount"/> таблицями по <see cref="ColumnsPerTable"/> колонок; у кожній таблиці група
    /// <c>G</c>, якій належать перші <see cref="GroupedColumns"/> колонки (тож перевірка груп мусила б читати всі).
    /// </summary>
    private async Task<VersionFixture> VersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"B169{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var tables = new List<TableDef>();
        for (var t = 0; t < TableCount; t++)
        {
            var table = new TableDef(
                sheet.Id, EcrCode.Create($"T{t}_{tag}"), Name($"Table {t}"), t + 1,
                TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
            tables.Add(table);
            db.TableDefs.Add(table);
        }

        await db.SaveChangesAsync();

        var groups = tables.Select(t => new ColumnHeaderGroupDef(t.Id, EcrCode.Create("G"), Name("G"), 1)).ToList();
        db.ColumnHeaderGroupDefs.AddRange(groups);

        var columns = new List<List<ColumnDef>>();
        foreach (var table in tables)
        {
            var ofTable = new List<ColumnDef>();
            for (var c = 1; c <= ColumnsPerTable; c++)
            {
                var column = new ColumnDef(table.Id, EcrCode.Create($"C{c}"), Name($"C{c}"), c, CellDataType.Decimal);
                ofTable.Add(column);
                db.ColumnDefs.Add(column);
            }

            columns.Add(ofTable);
        }

        await db.SaveChangesAsync();

        for (var t = 0; t < TableCount; t++)
        {
            foreach (var column in columns[t].Take(GroupedColumns))
            {
                column.SetHeaderGroup(groups[t].Id);
            }
        }

        await db.SaveChangesAsync();

        return new VersionFixture(
            version.Id,
            [.. tables.Select((t, i) => new TableFixture(t.Id, t.Code, groups[i].Id, [.. columns[i].Select(c => c.Id)]))]);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record TableFixture(int TableId, string Code, int GroupId, IReadOnlyList<int> Columns);

    private sealed record VersionFixture(int VersionId, IReadOnlyList<TableFixture> Tables);

    /// <summary>Скільки рядків EF прочитав із кожної категорії запиту (<c>«SELECT схема.Таблиця»</c>).</summary>
    private sealed class RowCounter : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<string, int> _rows = new(StringComparer.Ordinal);

        public int Of(string category) => _rows.GetValueOrDefault(category);

        public string Format()
            => string.Join('\n', _rows.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"  {p.Key}: {p.Value}"));

        public override InterceptionResult DataReaderClosing(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Record(command, eventData);
            return base.DataReaderClosing(command, eventData, result);
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Record(command, eventData);
            return base.DataReaderClosingAsync(command, eventData, result);
        }

        private void Record(DbCommand command, DataReaderClosingEventData eventData)
            => _rows.AddOrUpdate(CommandCategory.Of(command.CommandText), eventData.ReadCount, (_, n) => n + eventData.ReadCount);
    }
}
