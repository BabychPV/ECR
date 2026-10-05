using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>ФВ-2.16</c>, <c>D-239</c>: <c>IAuditReader.ReadOutOfWindowCellsAsync</c>
/// проти живого <c>aud.CellChange</c> — які комірки зрізу несуть значок
/// «правка поза вікном» після перезавантаження сторінки.
/// </summary>
/// <remarks>
/// ⛔ Проти справжньої СУБД, бо предмет — властивості ЗАПИТУ: «остання зміна
/// комірки», розвід однакового <c>ChangedAt</c> за <c>Id</c>, межі документа й
/// періоду і фільтрований індекс, на який запит спирається.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderOutOfWindowCellsTests(SqlServerFixture sql)
{
    private static readonly DateTime Earlier = new(2026, 3, 2, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Значок_несе_лише_комірка_чия_остання_зміна_поза_вікном()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);
        await writer.WriteCellChangesAsync(
            [
                // Рядок 0: звичайна → поза вікном. Остання — поза вікном: значок є.
                Change(doc, row: 0, Earlier, outOfWindow: false),
                Change(doc, row: 0, Later, outOfWindow: true),

                // Рядок 1: поза вікном → звичайна (імпорт, перевідкрите вікно).
                // У комірці вже інше значення — значка немає.
                Change(doc, row: 1, Earlier, outOfWindow: true),
                Change(doc, row: 1, Later, outOfWindow: false),

                // Рядок 2: лише звичайні правки.
                Change(doc, row: 2, Later, outOfWindow: false),
            ],
            CancellationToken.None);

        var cells = await new AuditReader(db).ReadOutOfWindowCellsAsync(
            doc.DocumentId, doc.PeriodKey.Value, CancellationToken.None);

        // ⛔ Мутація «прибрати NOT EXISTS» (тобто «хоч раз поза вікном») дає ще
        // й рядок 1; мутація «прибрати IsOutOfWindow = 1» — ще й рядок 2.
        Assert.Equal([(doc.RowIds[0], doc.ColumnDefIds[1])], cells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Однаковий_момент_зміни_розводить_Id_а_не_випадок()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);

        // ⚠ Два окремі записи, а не один пакет: порядок Id усередині пакета
        // `INSERT … SELECT` не гарантований, а між двома записами — так.
        await writer.WriteCellChangesAsync([Change(doc, row: 0, Later, outOfWindow: true)], CancellationToken.None);
        await writer.WriteCellChangesAsync([Change(doc, row: 0, Later, outOfWindow: false)], CancellationToken.None);
        await writer.WriteCellChangesAsync([Change(doc, row: 1, Later, outOfWindow: false)], CancellationToken.None);
        await writer.WriteCellChangesAsync([Change(doc, row: 1, Later, outOfWindow: true)], CancellationToken.None);

        var cells = await new AuditReader(db).ReadOutOfWindowCellsAsync(
            doc.DocumentId, doc.PeriodKey.Value, CancellationToken.None);

        // ⛔ Мутація «прибрати розвід за Id» дає обидві комірки: кожна з двох
        // правок з однаковим ChangedAt вважала б себе останньою.
        Assert.Equal([(doc.RowIds[1], doc.ColumnDefIds[1])], cells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Чужий_документ_і_чужий_період_не_потрапляють()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var other = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [
                Change(doc, row: 0, Later, outOfWindow: true),
                Change(other, row: 0, Later, outOfWindow: true),
                Change(doc, row: 1, Later, outOfWindow: true, period: doc.PeriodKey.Value + 1),
            ],
            CancellationToken.None);

        var cells = await new AuditReader(db).ReadOutOfWindowCellsAsync(
            doc.DocumentId, doc.PeriodKey.Value, CancellationToken.None);

        Assert.Equal([(doc.RowIds[0], doc.ColumnDefIds[1])], cells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Кандидатів_несе_фільтрований_вирівняний_індекс()
    {
        // ⚠ Ціна запиту без вікна часу тримається саме на цьому індексі
        // (`11-audit-tables.sql`): без фільтра зріз читав би всі правки документа.
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   + '|' + i.filter_definition + '|' + ds.name
              FROM sys.indexes AS i
              JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
              JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
             WHERE i.object_id = OBJECT_ID(N'aud.CellChange') AND i.name = N'IX_CellChange_OutOfWindow'
             GROUP BY i.filter_definition, ds.name;
            """;

        var shape = (string?)await command.ExecuteScalarAsync();

        Assert.Equal("DocumentId,PeriodKey,TableRowId,ColumnDefId|([IsOutOfWindow]=(1))|ps_AuditByMonth", shape);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Запит_не_читає_партицій_аудиту_до_відкриття_періоду()
    {
        // ⛔ L6-12. Без нижньої межі `ChangedAt` запит на КОЖЕН GET зрізу читав
        // усі місячні партиції `aud.CellChange`. Період 202612, відкриття
        // 2026-12-01 → межа 2026-11-30: партиції до листопада 2026 не читаються.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202612, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE p SET ComputedOpenAt = '2026-12-01' FROM doc.Period AS p WHERE p.ProjectId = {doc.ProjectId} AND p.PeriodKey = 202612");

        var at = new DateTime(2026, 12, 5, 9, 0, 0, DateTimeKind.Utc);
        await new AuditWriter(db).WriteCellChangesAsync([Change(doc, row: 0, at, outOfWindow: true)], CancellationToken.None);

        // Межа не губить законну правку: вона після відкриття — значок є.
        Assert.Equal(
            [(doc.RowIds[0], doc.ColumnDefIds[1])],
            await new AuditReader(db).ReadOutOfWindowCellsAsync(doc.DocumentId, 202612, CancellationToken.None));

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        int fanout;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT fanout FROM sys.partition_functions WHERE name = N'pf_AuditByMonth';";
            fanout = (int)(await count.ExecuteScalarAsync())!;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS XML ON;\n" + AuditReader.OutOfWindowCellsSql + "\nSET STATISTICS XML OFF;";
        command.Parameters.Add("@documentId", System.Data.SqlDbType.BigInt).Value = doc.DocumentId;
        command.Parameters.Add("@periodKey", System.Data.SqlDbType.Int).Value = 202612;

        var plans = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal))
                {
                    while (await reader.ReadAsync())
                    {
                        plans.Add(reader.GetString(0));
                    }
                }
                else
                {
                    while (await reader.ReadAsync())
                    {
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var accessed = plans
            .SelectMany(p => System.Xml.Linq.XDocument.Parse(p).Descendants(ns + "RelOp"))
            .Where(op => op.Descendants(ns + "Object").Any(o => (string?)o.Attribute("Table") == "[CellChange]")
                         && op.Element(ns + "RunTimePartitionSummary") is not null)
            .Select(op => (int)op.Element(ns + "RunTimePartitionSummary")!.Element(ns + "PartitionsAccessed")!.Attribute("PartitionCount")!)
            .ToList();

        Assert.NotEmpty(accessed);
        Assert.All(accessed, n => Assert.True(n < fanout, $"прочитано {n} партицій із {fanout}"));
    }

    private static CellChangeRecord Change(
        TestDocument doc, int row, DateTime at, bool outOfWindow, int? period = null)
        => new(
            at,
            new CellAddress(new PeriodKey(period ?? doc.PeriodKey.Value), doc.RowIds[row], doc.ColumnDefIds[1]),
            doc.DocumentId,
            $"R{(row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            OldValue: null,
            NewValue: "1",
            ChangedByUserId: 42,
            Origin: "UserEdit",
            IsLateEdit: false,
            CorrelationId: null,
            IsOutOfWindow: outOfWindow);
}
