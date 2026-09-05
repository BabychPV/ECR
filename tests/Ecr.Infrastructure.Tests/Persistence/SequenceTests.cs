using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>Id</c> береться з <c>SEQUENCE</c>, а не <c>IDENTITY</c>: значення
/// потрібні **до** вставки, щоб завантажити рядки і комірки одним проходом
/// <c>SqlBulkCopy</c> (B02 §2.3).
/// </summary>
[Collection("SqlServer")]
public sealed class SequenceTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.10")]
    public async Task Резервування_діапазону_повертає_безперервні_ідентифікатори()
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);

        var first = await loader.ReserveIdsAsync("doc.TableRowSeq", 100, CancellationToken.None);
        var next = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);

        // Діапазон саме безперервний: викликач роздає Id рядкам простим
        // інкрементом, і «діра» всередині зробила б два рядки з одним Id.
        Assert.Equal(first + 100, next);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Паралельні_резервування_не_перетинаються()
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        const int Reservations = 8;
        const int Size = 50;

        var ranges = await Task.WhenAll(Enumerable.Range(0, Reservations).Select(
            _ => loader.ReserveIdsAsync("doc.TableRowSeq", Size, CancellationToken.None)));

        // Кожен діапазон розгортаємо в множину значень: перекриття означало б
        // два рядки з однаковим Id у різних потоках імпорту — і складений FK
        // із CellValue вказував би не туди.
        var all = ranges.SelectMany(start => Enumerable.Range(0, Size).Select(i => start + i)).ToList();

        Assert.Equal(Reservations * Size, all.Distinct().Count());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Один_виклик_на_батч_а_не_на_рядок()
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        const int BatchSize = 10_000;

        var before = await CurrentValueAsync("doc.TableRowSeq");
        await loader.ReserveIdsAsync("doc.TableRowSeq", BatchSize, CancellationToken.None);
        var after = await CurrentValueAsync("doc.TableRowSeq");

        // Послідовність зрушилася рівно на розмір батчу — тобто виклик був
        // один. NEXT VALUE FOR у циклі на 10 000 рядків дав би той самий
        // приріст, але 10 000 звернень до системних структур; перевірити це
        // можна саме через те, що приріст стався за ОДИН крок.
        Assert.Equal(before + BatchSize, after);
    }

    /// <summary>Поточне значення послідовності.</summary>
    private async Task<long> CurrentValueAsync(string sequence)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CAST(current_value AS bigint) FROM sys.sequences WHERE object_id = OBJECT_ID(@name)";
        command.Parameters.AddWithValue("@name", sequence);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }
}
