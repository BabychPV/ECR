using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Сховище комірок на реальному SQL Server.</summary>
[Collection("SqlServer")]
public sealed class CellStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_значення_створює_рядок_комірки()
    {
        var (doc, store) = await ArrangeAsync();
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);

        await store.ApplyAsync(new CellChangeSet(
            doc.TableInstanceId,
            [new CellRecord(address, doc.TableDefId, new CellValueData { ValueNumeric = 42.5m })],
            [],
            [doc.RowIds[0]],
            ChangedByUserId: 1,
            IsLateEdit: false), CancellationToken.None);

        var read = await store.ReadCellsAsync([address], CancellationToken.None);

        Assert.Equal(42.5m, read[address].ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стирання_видаляє_рядок_комірки_а_не_обнуляє_його()
    {
        var (doc, store) = await ArrangeAsync();
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
        var ct = CancellationToken.None;

        await store.ApplyAsync(new CellChangeSet(
            doc.TableInstanceId,
            [new CellRecord(address, doc.TableDefId, new CellValueData { ValueNumeric = 1m })],
            [], [doc.RowIds[0]], 1, false), ct);

        await store.ApplyAsync(new CellChangeSet(
            doc.TableInstanceId, [], [address], [doc.RowIds[0]], 1, false), ct);

        // ⚠ Рядка не має бути ЗОВСІМ. «Комірка зі значенням NULL» і
        // «комірки немає» — різні стани (R-B4), і обнулення замість видалення
        // тихо перетворило б перший на другий.
        var rows = await CountCellsAsync(doc, address, ct);
        Assert.Equal(0, rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Явна_порожнеча_лишає_рядок_із_прапорцем_IsEmpty()
    {
        var (doc, store) = await ArrangeAsync();
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
        var ct = CancellationToken.None;

        await store.ApplyAsync(new CellChangeSet(
            doc.TableInstanceId,
            [new CellRecord(address, doc.TableDefId, CellValueData.Empty)],
            [], [doc.RowIds[0]], 1, false), ct);

        var read = await store.ReadCellsAsync([address], ct);

        // Третій стан: рядок є, значення немає. Саме він відрізняє
        // «користувач свідомо лишив порожнім» від «ще не заповнював».
        Assert.True(read.ContainsKey(address));
        Assert.True(read[address].IsEmpty);
        Assert.Null(read[address].ValueNumeric);
        Assert.Equal(1, await CountCellsAsync(doc, address, ct));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Незаповнені_комірки_не_створюються_і_не_повертаються()
    {
        var (doc, store) = await ArrangeAsync();
        var ct = CancellationToken.None;

        // Заповнена рівно одна комірка з 4 рядків × 3 колонок = 12.
        var filled = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
        await store.ApplyAsync(new CellChangeSet(
            doc.TableInstanceId,
            [new CellRecord(filled, doc.TableDefId, new CellValueData { ValueNumeric = 7m })],
            [], [doc.RowIds[0]], 1, false), ct);

        var slice = await store.ReadSliceAsync(doc.TableInstanceId, ct);

        // Порожні комірки не існують у базі і не їдуть по мережі —
        // клієнт бере ColumnDef.DefaultValue (ФВ-3.8). Інакше зріз 500×60
        // віз би 30 000 рядків замість фактично заповнених.
        Assert.Single(slice);
        Assert.Equal(filled, slice[0].Address);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання_зрізу_виконує_один_запит_а_не_запит_на_рядок()
    {
        var (doc, store) = await ArrangeAsync();
        var ct = CancellationToken.None;

        var records = doc.RowIds
            .SelectMany(rowId => doc.ColumnDefIds.Skip(1).Select(columnId => new CellRecord(
                new CellAddress(doc.PeriodKey, rowId, columnId),
                doc.TableDefId,
                new CellValueData { ValueNumeric = rowId + columnId })))
            .ToList();

        await store.ApplyAsync(new CellChangeSet(
            doc.TableInstanceId, records, [], doc.RowIds, 1, false), ct);

        // Рахуємо команди, які EF реально відправив, а не час: час залежить
        // від машини, а кількість запитів — ні. N+1 тут коштує не мілісекунди,
        // а бюджет: 500 рядків × окремий запит у 600 мс не вкладаються ніяк.
        var executed = new List<string>();
        await using var counting = CreateCountingContext(executed);
        var countingStore = new NormalizedCellStore(counting, new BulkCellLoader(sql.ConnectionString, 1000));

        var slice = await countingStore.ReadSliceAsync(doc.TableInstanceId, ct);

        Assert.Equal(records.Count, slice.Count);
        Assert.Single(executed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Масове_завантаження_вантажить_рядки_і_комірки_одним_проходом()
    {
        var (doc, store) = await ArrangeAsync(rowCount: 50);
        var ct = CancellationToken.None;

        var records = doc.RowIds
            .SelectMany(rowId => doc.ColumnDefIds.Skip(1).Select(columnId => new CellRecord(
                new CellAddress(doc.PeriodKey, rowId, columnId),
                doc.TableDefId,
                new CellValueData { ValueNumeric = 1m })))
            .ToList();

        await store.BulkInsertAsync(records, ct);

        var slice = await store.ReadSliceAsync(doc.TableInstanceId, ct);
        Assert.Equal(records.Count, slice.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Комірка_з_колонкою_ЧУЖОЇ_таблиці_відхиляється_БАЗОЮ()
    {
        // ⛔ Це перевірка СХЕМИ, а не коду, і саме тому вона тут, а не в
        // прикладних тестах. `FK_CellValue_Column` складений (`D-84`):
        // `(TableDefId, ColumnDefId) → cfg.ColumnDef (TableDefId, Id)`.
        // Простий ключ на самому `ColumnDefId` пропустив би комірку в колонку
        // чужої таблиці — і саме він був би коренем `A7-27`, якби існував.
        //
        // ⚠ Тест блокуючий за вимогою директиви: помилка адресації мусить
        // падати на ключі бази, а не покладатися на те, що код не помилиться.
        // Код теж перевіряється — окремо, у `PatchCellsTests`.
        var (doc, _) = await ArrangeAsync();

        // ⚠ Друга таблиця заводиться ТУТ: фікстура будує одну, а перевірка
        // без чужої колонки не має сенсу — саме її відсутність і робила б тест
        // зеленим ні про що.
        var alien = await AlienColumnIdAsync(doc.SheetDefId, doc.TableDefId, CancellationToken.None);

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT doc.CellValue (PeriodKey, TableRowId, TableDefId, ColumnDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES (@p, @r, @t, @c, 1, 0, 0);
            """;
        command.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
        command.Parameters.AddWithValue("@r", doc.RowIds[0]);
        command.Parameters.AddWithValue("@t", doc.TableDefId);
        command.Parameters.AddWithValue("@c", alien);

        var error = await Assert.ThrowsAsync<SqlException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));

        Assert.Contains("FK_CellValue_Column", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Створює другу таблицю з колонкою і повертає її <c>ColumnDefId</c>.
    /// </summary>
    /// <param name="sheetDefId">Аркуш, до якого належить нова таблиця.</param>
    /// <param name="ownTableDefId">Таблиця документа — щоб не сплутати.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<int> AlienColumnIdAsync(int sheetDefId, int ownTableDefId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @t int;

            INSERT cfg.TableDef (SheetDefId, Code, NameL10n, Ordinal, LayoutKind, RowMode, StorageMode, IsDeleted)
            VALUES (@sheet, CONCAT(N'ALIEN', CAST(@own AS nvarchar(10))), N'{"en":"Alien"}', 99, 0, 0, 0, 0);

            SET @t = SCOPE_IDENTITY();

            INSERT cfg.ColumnDef (TableDefId, Code, HeaderL10n, Ordinal, DataType, IsReadOnly, IsRequired, IsHidden, IsDeleted)
            VALUES (@t, N'ALIEN_COL', N'{"en":"Alien"}', 1, 1, 0, 0, 0, 0);

            SELECT CAST(SCOPE_IDENTITY() AS int);
            """;
        command.Parameters.AddWithValue("@sheet", sheetDefId);
        command.Parameters.AddWithValue("@own", ownTableDefId);

        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<(TestDocument Document, ICellStore Store)> ArrangeAsync(int rowCount = 4)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: rowCount, ct: CancellationToken.None);
        var store = new NormalizedCellStore(
            builder.CreateContext(), new BulkCellLoader(sql.ConnectionString, 1000));
        return (doc, store);
    }

    private async Task<int> CountCellsAsync(TestDocument doc, CellAddress address, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey=@p AND TableRowId=@r AND ColumnDefId=@c";
        command.Parameters.AddWithValue("@p", address.PeriodKey.Value);
        command.Parameters.AddWithValue("@r", address.TableRowId);
        command.Parameters.AddWithValue("@c", address.ColumnDefId);
        return (int)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);
}
