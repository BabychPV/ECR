using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>UI-38, C3: пошук <c>q</c> по журналу змін комірок — параметр, спецсимволи LIKE буквальні.</summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>.Replace("%", ...)</c> з <c>AuditReader.EscapeLike</c> — червоніє
/// <see cref="Відсоток_і_підкреслення_у_пошуку_шукаються_буквально"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderQueryTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 3, 4, 9, 15, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Відсоток_і_підкреслення_у_пошуку_шукаються_буквально()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var keys = new[] { "K100%", "K1000", "K_x", "KAx", "K[1]" };

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync([.. keys.Select(k => Change(doc, k))], CancellationToken.None);

        var reader = new AuditReader(db);

        async Task<List<string>> Find(string q)
        {
            var page = await reader.ReadCellChangesAsync(
                new CellChangeFilter(At.AddMinutes(-1), At.AddMinutes(1), DocumentId: doc.DocumentId, Query: q),
                new CursorRequest(50),
                CancellationToken.None);
            return [.. page.Items.Select(c => c.RowKey).Order(StringComparer.Ordinal)];
        }

        Assert.Equal(["K100%"], await Find("100%"));
        Assert.Equal(["K_x"], await Find("K_x"));
        Assert.Equal(["K[1]"], await Find("[1]"));
        Assert.Equal(["K100%", "K1000"], await Find("K100"));

        // Звичайний підрядок без спецсимволів і порожня видача, а не помилка.
        Assert.Equal(["KAx"], await Find("kax"));
        Assert.Empty(await Find("немає-такого"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Пошук_знаходить_за_бізнес_ключем_документа_і_кодом_колонки_а_не_лише_за_рядком()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var (businessKey, columnCode) = await FactsAsync(doc);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync([Change(doc, "ZZ-row")], CancellationToken.None);

        var reader = new AuditReader(db);

        async Task<int> Count(string q)
            => (await reader.ReadCellChangesAsync(
                new CellChangeFilter(At.AddMinutes(-1), At.AddMinutes(1), DocumentId: doc.DocumentId, Query: q),
                new CursorRequest(50),
                CancellationToken.None)).Items.Count(c => c.DocumentId == doc.DocumentId);

        Assert.Equal(1, await Count(businessKey));
        Assert.Equal(1, await Count(columnCode));
        Assert.Equal(0, await Count("ZZ-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Лічильники_за_колонкою_розрізняють_джерело_і_сьогодні()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [
                Change(doc, "R-a", "Import", At),
                Change(doc, "R-b", "Import", At.AddDays(-1)),
                Change(doc, "R-c", "Recalculation", At),
                Change(doc, "R-d", "UserEdit", At),
            ],
            CancellationToken.None);

        var counts = await new AuditReader(db).CountCellChangesByColumnAsync(
            new CellChangeFilter(At.AddDays(-2), At.AddDays(1), DocumentId: doc.DocumentId),
            At.Date,
            CancellationToken.None);

        var column = Assert.Single(counts);
        Assert.Equal(doc.ColumnDefIds[1], column.ColumnDefId);
        Assert.Equal(4, column.Total);
        Assert.Equal(3, column.Today);
        Assert.Equal(2, column.ByImport);
        Assert.Equal(1, column.ByRecalculation);

        // Фільтр походження звужує й підрахунок.
        var onlyImport = await new AuditReader(db).CountCellChangesByColumnAsync(
            new CellChangeFilter(At.AddDays(-2), At.AddDays(1), DocumentId: doc.DocumentId, Origin: "Import"),
            At.Date,
            CancellationToken.None);
        Assert.Equal(2, Assert.Single(onlyImport).Total);
    }

    private static CellChangeRecord Change(TestDocument doc, string rowKey, string origin, DateTime at)
        => new(
            at,
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]),
            doc.DocumentId,
            rowKey,
            OldValue: "1",
            NewValue: "2",
            ChangedByUserId: 1,
            Origin: origin,
            IsLateEdit: false,
            CorrelationId: null);

    private static CellChangeRecord Change(TestDocument doc, string rowKey)
        => new(
            At,
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]),
            doc.DocumentId,
            rowKey,
            OldValue: "1",
            NewValue: "2",
            ChangedByUserId: 1,
            Origin: "UserEdit",
            IsLateEdit: false,
            CorrelationId: null);

    private async Task<(string BusinessKey, string ColumnCode)> FactsAsync(TestDocument doc)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.BusinessKey, c.Code
              FROM doc.Document AS d CROSS JOIN cfg.ColumnDef AS c
             WHERE d.Id = @doc AND c.Id = @col;
            """;
        command.Parameters.AddWithValue("@doc", doc.DocumentId);
        command.Parameters.AddWithValue("@col", doc.ColumnDefIds[1]);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return (reader.GetString(0), reader.GetString(1));
    }
}
